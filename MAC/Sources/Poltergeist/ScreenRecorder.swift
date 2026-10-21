import AppKit
import SwiftUI
import AVFoundation

/// Copies a finished file into the user's backup folder without overwriting anything.
enum FileSaver {
    static func save(_ temp: URL, into folder: String, label: String) -> URL? {
        guard !folder.isEmpty else { return nil }
        let fm = FileManager.default
        let dir = URL(fileURLWithPath: (folder as NSString).expandingTildeInPath, isDirectory: true)
        do {
            try fm.createDirectory(at: dir, withIntermediateDirectories: true)
            let base = temp.deletingPathExtension().lastPathComponent
            var dest = dir.appendingPathComponent(temp.lastPathComponent)
            var n = 2
            while fm.fileExists(atPath: dest.path) {
                dest = dir.appendingPathComponent("\(base) \(n).\(temp.pathExtension)")
                n += 1
            }
            try fm.copyItem(at: temp, to: dest)
            try? fm.removeItem(at: temp)
            return dest
        } catch {
            UploadManager.shared.report(label, "Couldn't save to \(dir.path): \(error.localizedDescription)")
            return nil
        }
    }
}

private final class FloatingPanel: NSPanel {
    override var canBecomeKey: Bool { true }
}

/// A thin accent border just outside the recorded area, so you can see what's being captured.
private final class OutlineView: NSView {
    override func draw(_ dirtyRect: NSRect) {
        NSColor(Theme.accent).setStroke()
        let p = NSBezierPath(rect: bounds.insetBy(dx: 1.5, dy: 1.5))
        p.lineWidth = 2
        p.setLineDash([6, 4], count: 2, phase: 0)
        p.stroke()
    }
}

enum RecordMode { case area, fullscreen, window }

final class ScreenRecorder: ObservableObject {
    static let shared = ScreenRecorder()

    enum Phase { case idle, selecting, starting, recording, paused }
    @Published private(set) var phase: Phase = .idle
    @Published private(set) var elapsed: TimeInterval = 0

    var isActive: Bool { phase != .idle }
    var isRecording: Bool { phase == .recording || phase == .paused }

    /// Menu-bar text while recording ("" when finished).
    var onTick: ((String) -> Void)?
    /// Called with the finished MP4 when the user stops and wants it uploaded.
    /// The finished MP4, and whether the user asked to open the editor for it (Stop & edit).
    var onFinished: ((URL, Bool) -> Void)?

    private var session: RecordingSession?
    private var panel: NSPanel?
    private var outline: NSWindow?
    private var timer: Timer?
    private var startDate = Date()
    private var pausedAt: Date?
    private var pausedTotal: TimeInterval = 0

    // MARK: Hotkey / menu entry points

    /// The record hotkey: starts a recording, or stops and uploads the one in progress.
    func toggle(_ mode: RecordMode) {
        switch phase {
        case .idle: start(mode)
        case .selecting:
            RegionSelector.current?.cancel()
            WindowSelector.current?.cancel()
        case .starting: break
        case .recording, .paused: stop(upload: true)
        }
    }

    func start(_ mode: RecordMode) {
        guard phase == .idle else { return }
        if !CGPreflightScreenCaptureAccess() {
            CGRequestScreenCaptureAccess()
            UploadManager.shared.report("Recording", "Screen Recording isn't allowed for Poltergeist. Turn it on in System Settings → Privacy & Security → Screen & System Audio Recording (or use Settings → Recordings → Permissions), then relaunch Poltergeist.")
            return
        }
        switch mode {
        case .fullscreen:
            let mouse = NSEvent.mouseLocation
            guard let screen = NSScreen.screens.first(where: { $0.frame.contains(mouse) }) ?? NSScreen.main else { return }
            begin(rect: screen.frame, screen: screen, showOutline: false)
        case .area:
            phase = .selecting
            RegionSelector.select { [weak self] rect, screen in
                guard let self else { return }
                guard let rect, let screen else { self.phase = .idle; return }
                self.begin(rect: rect, screen: screen, showOutline: true)
            }
        case .window:
            phase = .selecting
            WindowSelector.select { [weak self] picked, screen in
                guard let self else { return }
                guard let picked, let screen else { self.phase = .idle; return }
                self.begin(rect: picked.frame, screen: screen, showOutline: false, windowID: picked.id)
            }
        }
    }

    func togglePause() {
        switch phase {
        case .recording:
            session?.pause()
            pausedAt = Date()
            phase = .paused
        case .paused:
            session?.resume()
            if let p = pausedAt { pausedTotal += Date().timeIntervalSince(p) }
            pausedAt = nil
            phase = .recording
        default: break
        }
        tick()
    }

    /// Stops the recording. With `upload` the video is saved and handed to the uploader; otherwise it is thrown away.
    func stop(upload: Bool, edit: Bool = false) {
        guard isRecording, let session else { return }
        timer?.invalidate()
        closeUI()
        self.session = nil
        phase = .idle
        onTick?("")
        Task { @MainActor in
            if upload {
                if await session.stop() { onFinished?(session.outputURL, edit) }
                else { UploadManager.shared.report("Recording", "No video was captured. Nothing changed on screen, or recording failed to start.") }
            } else {
                await session.abort()
            }
        }
    }

    // MARK: Starting

    private func begin(rect: NSRect, screen: NSScreen, showOutline: Bool, windowID: CGWindowID? = nil) {
        phase = .starting
        let settings = Settings.shared
        let scale = screen.backingScaleFactor
        // H.264 needs even pixel dimensions.
        let w = max(2, Int(rect.width * scale) & ~1)
        let h = max(2, Int(rect.height * scale) & ~1)
        let sourceRect = CGRect(x: rect.minX - screen.frame.minX,
                                y: screen.frame.maxY - rect.maxY,
                                width: Double(w) / scale, height: Double(h) / scale)
        guard let num = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else {
            phase = .idle
            return
        }
        let displayID = CGDirectDisplayID(num.uint32Value)

        let fmt = DateFormatter()
        fmt.dateFormat = "yyyy-MM-dd HH.mm.ss"
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("Recording \(fmt.string(from: Date())).mp4")
        let s = RecordingSession(outputURL: url)
        s.onError = { [weak self] msg in
            DispatchQueue.main.async {
                guard let self, self.session === s else { return }
                self.timer?.invalidate()
                self.closeUI()
                self.session = nil
                self.phase = .idle
                self.onTick?("")
                UploadManager.shared.report("Recording", msg)
                Task { await s.abort() }
            }
        }
        session = s

        s.onWarning = { msg in UploadManager.shared.report("Recording", msg) }

        var micOn = settings.recordMic
        if micOn && AVCaptureDevice.authorizationStatus(for: .audio) != .authorized {
            micOn = false
            UploadManager.shared.report("Microphone", "Microphone access isn't allowed, so this recording has no mic. Allow it in Settings → Recordings → Permissions.")
        }
        let options = RecordingOptions(fps: settings.recordFPS, bitrateMbps: settings.recordBitrate,
                                       systemAudio: settings.recordSystemAudio, mic: micOn,
                                       micDeviceID: settings.micDeviceID)

        showUI(around: rect, on: screen, outline: showOutline)
        let target: RecordingTarget
        if let windowID {
            target = .window(windowID)   // only that window is captured, so our controls can't appear in it
        } else {
            let ids = [panel, outline].compactMap { $0 }.map { CGWindowID($0.windowNumber) }
            target = .region(displayID: displayID, sourceRect: sourceRect, excludedWindowIDs: ids)
        }

        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 150_000_000) // let our windows reach the window server
            do {
                try await s.start(target: target, pixelSize: (w, h), options: options)
                guard self.session === s else { await s.abort(); return }
                self.startDate = Date()
                self.pausedAt = nil
                self.pausedTotal = 0
                self.phase = .recording
                self.timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in self?.tick() }
                self.tick()
            } catch {
                self.closeUI()
                self.session = nil
                self.phase = .idle
                UploadManager.shared.report("Recording", error.localizedDescription)
            }
        }
    }

    private func tick() {
        let paused = pausedAt.map { Date().timeIntervalSince($0) } ?? 0
        elapsed = max(0, Date().timeIntervalSince(startDate) - pausedTotal - paused)
        let t = String(format: "%d:%02d", Int(elapsed) / 60, Int(elapsed) % 60)
        onTick?(phase == .paused ? " ❚❚ \(t)" : " \(t)")
    }

    // MARK: Controls UI

    private func showUI(around rect: NSRect, on screen: NSScreen, outline showOutline: Bool) {
        if showOutline {
            let o = NSWindow(contentRect: rect.insetBy(dx: -3, dy: -3), styleMask: .borderless, backing: .buffered, defer: false)
            o.isOpaque = false
            o.backgroundColor = .clear
            o.hasShadow = false
            o.ignoresMouseEvents = true
            o.level = .statusBar
            o.isReleasedWhenClosed = false
            o.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            o.contentView = OutlineView(frame: NSRect(origin: .zero, size: o.frame.size))
            o.orderFrontRegardless()
            outline = o
        }

        let size = NSSize(width: 500, height: 52)
        let host = NSHostingView(rootView: RecorderControls(recorder: self))
        host.frame = NSRect(origin: .zero, size: size)
        let p = FloatingPanel(contentRect: host.frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = true
        p.level = NSWindow.Level(rawValue: NSWindow.Level.statusBar.rawValue + 1)
        p.isMovableByWindowBackground = true
        p.isReleasedWhenClosed = false
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        p.contentView = host
        p.setFrameOrigin(Self.controlsOrigin(size: size, selection: rect, visible: screen.visibleFrame))
        p.orderFrontRegardless()
        panel = p
    }

    /// Under the selection if there's room, then above it, else along the bottom of the screen.
    /// It's excluded from capture, so even sitting inside the recorded area it never appears in the video.
    static func controlsOrigin(size: NSSize, selection: NSRect, visible: NSRect) -> NSPoint {
        let gap: CGFloat = 12
        var x = selection.midX - size.width / 2
        x = min(max(x, visible.minX + 8), visible.maxX - size.width - 8)
        if selection.minY - gap - size.height >= visible.minY {
            return NSPoint(x: x, y: selection.minY - gap - size.height)
        }
        if selection.maxY + gap + size.height <= visible.maxY {
            return NSPoint(x: x, y: selection.maxY + gap)
        }
        return NSPoint(x: x, y: visible.minY + 20)
    }

    private func closeUI() {
        panel?.orderOut(nil)
        outline?.orderOut(nil)
        panel = nil
        outline = nil
    }
}

private struct RecorderControls: View {
    @ObservedObject var recorder: ScreenRecorder

    var body: some View {
        HStack(spacing: 10) {
            HStack(spacing: 7) {
                Circle().fill(recorder.phase == .paused ? Theme.warning : Color(hex: 0xff4d4d)).frame(width: 9, height: 9)
                Text(time).font(.system(size: 14, weight: .semibold, design: .monospaced)).foregroundColor(Theme.text)
                    .frame(width: 52, alignment: .leading)
            }
            Button { recorder.togglePause() } label: {
                Label(recorder.phase == .paused ? "Resume" : "Pause", systemImage: recorder.phase == .paused ? "play.fill" : "pause.fill")
            }.buttonStyle(PillButtonStyle(prominent: false))
            Button { recorder.stop(upload: true, edit: true) } label: {
                Label("Stop & edit", systemImage: "scissors")
            }.buttonStyle(PillButtonStyle(prominent: false)).help("Stop and open the video editor")
            Button { recorder.stop(upload: true) } label: {
                Label("Stop", systemImage: "stop.fill")
            }.buttonStyle(PillButtonStyle())
            Button { recorder.stop(upload: false) } label: {
                Label("Discard", systemImage: "trash")
            }.buttonStyle(PillButtonStyle(prominent: false))
        }
        .labelStyle(CompactLabelStyle())
        .padding(.horizontal, 12)
        .frame(width: 500, height: 52)
        .background(Theme.card)
        .overlay(RoundedRectangle(cornerRadius: 26).stroke(Theme.border))
        .clipShape(RoundedRectangle(cornerRadius: 26))
        .preferredColorScheme(.dark)
        .help("Stop saves and uploads. Discard throws the recording away.")
    }

    private var time: String { String(format: "%d:%02d", Int(recorder.elapsed) / 60, Int(recorder.elapsed) % 60) }
}

private struct CompactLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 5) {
            configuration.icon.font(.system(size: 11))
            configuration.title.font(.system(size: 12, weight: .medium))
        }
    }
}
