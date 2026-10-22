import AppKit
import Combine
import AVFoundation
import SwiftUI
import UserNotifications
import Carbon.HIToolbox

@main
enum Main {
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        app.run()
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var statusItem: NSStatusItem!
    private var settingsWindow: NSWindow?
    private let menu = NSMenu()
    private var cancellables = Set<AnyCancellable>()

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.image = Self.ghostIcon()
        statusItem.button?.imagePosition = .imageLeading
        if let button = statusItem.button {
            let drop = StatusDropView(frame: button.bounds)
            drop.autoresizingMask = [.width, .height]
            drop.onDrop = { [weak self] urls in urls.forEach { self?.upload($0) } }
            button.addSubview(drop)
        }
        // Red dot on the ghost while recording, amber while paused.
        ScreenRecorder.shared.$phase.receive(on: DispatchQueue.main).sink { [weak self] phase in
            switch phase {
            case .recording: self?.statusItem.button?.image = Self.ghostIcon(badge: NSColor(hex: 0xff3b30))
            case .paused: self?.statusItem.button?.image = Self.ghostIcon(badge: NSColor(hex: 0xffb020))
            default: self?.statusItem.button?.image = Self.ghostIcon()
            }
        }.store(in: &cancellables)
        menu.delegate = self
        statusItem.menu = menu

        if Bundle.main.bundleIdentifier != nil {
            UNUserNotificationCenter.current().requestAuthorization(options: [.alert]) { _, _ in }
        }

        if Settings.shared.historyClear == "quit" { HistoryStore.shared.clear() }
        HistoryStore.shared.pruneExpired()
        ScreenRecorder.shared.onTick = { [weak self] text in self?.statusItem.button?.title = text }
        ScreenRecorder.shared.onFinished = { [weak self] url, edit in self?.finishRecording(url, forceEdit: edit) }
        NSApp.servicesProvider = self
        UploadManager.shared.onStatus = { [weak self] text in self?.statusItem.button?.title = text }
        installEditMenu()
        Task { try? await Task.sleep(nanoseconds: 4_000_000_000); if !ScreenRecorder.shared.isRecording { await Updater.check(manual: false) } }
        Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { _ in Settings.shared.totalSeconds += 30 }   // for the Stats page
        registerHotKeys()
        NotificationCenter.default.addObserver(forName: .hotkeysChanged, object: nil, queue: .main) { [weak self] _ in
            self?.registerHotKeys()
        }

        if Settings.shared.apiKey.isEmpty { openSettings() }
    }

    /// Accessory apps have no menu bar, so ⌘C/⌘V/⌘A/⌘Z never reach text fields without this.
    private func installEditMenu() {
        let main = NSMenu()
        let editItem = NSMenuItem()
        main.addItem(editItem)
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        let redo = edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        NSApp.mainMenu = main
    }

    /// Finder → right-click → Services → "Upload with Poltergeist" (declared in Info.plist).
    @objc func uploadFiles(_ pboard: NSPasteboard, userData: String, error: AutoreleasingUnsafeMutablePointer<NSString>) {
        let urls = pboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
        urls.forEach { upload($0) }
    }

    private func registerHotKeys() {
        HotKeys.shared.unregisterAll()
        for action in HotKeyAction.allCases {
            let b = Settings.shared.binding(action)
            guard b.isSet else { continue }
            HotKeys.shared.register(keyCode: b.keyCode, modifiers: b.modifiers) { [weak self] in
                switch action {
                case .area: self?.capture(.area)
                case .editArea: self?.capture(.area, edit: true)
                case .fullscreen: self?.capture(.fullscreen)
                case .window: self?.capture(.window)
                case .recordArea: ScreenRecorder.shared.toggle(.area)
                case .recordFullscreen: ScreenRecorder.shared.toggle(.fullscreen)
                case .recordWindow: ScreenRecorder.shared.toggle(.window)
                }
            }
        }
    }

    // MARK: Menu

    func applicationWillTerminate(_ notification: Notification) {
        if Settings.shared.historyClear == "quit" { HistoryStore.shared.clear() }
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        HistoryStore.shared.pruneExpired()
        menu.removeAllItems()
        let hk = Settings.shared
        add("Capture Area\t\(hk.binding(.area).hint)", #selector(captureArea))
        add("Capture Area & Edit…\t\(hk.binding(.editArea).hint)", #selector(captureAreaEdit))
        add("Capture Full Screen\t\(hk.binding(.fullscreen).hint)", #selector(captureFull))
        add("Capture Window\t\(hk.binding(.window).hint)", #selector(captureWindow))
        menu.addItem(.separator())
        let rec = ScreenRecorder.shared
        if rec.isRecording {
            add("Stop Recording & Upload\t\(hk.binding(.recordArea).hint)", #selector(stopRecording))
            add(rec.phase == .paused ? "Resume Recording" : "Pause Recording", #selector(pauseRecording))
            add("Discard Recording", #selector(discardRecording))
        } else {
            add("Record Area\t\(hk.binding(.recordArea).hint)", #selector(recordArea))
            add("Record Window\t\(hk.binding(.recordWindow).hint)", #selector(recordWindow))
            add("Record Full Screen\t\(hk.binding(.recordFullscreen).hint)", #selector(recordFull))
        }
        menu.addItem(.separator())
        add("Upload File…", #selector(pickFile))
        add("Upload Clipboard", #selector(uploadClipboard))
        if UploadManager.shared.hasActiveUploads { add("Cancel Uploads", #selector(cancelUploads)) }
        menu.addItem(.separator())

        let history = HistoryStore.shared.items
        let header = NSMenuItem(title: history.isEmpty ? "No uploads yet" : "Recent uploads", action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        for item in history.prefix(10) {
            let mi = NSMenuItem(title: item.name, action: #selector(copyHistory(_:)), keyEquivalent: "")
            mi.target = self
            mi.representedObject = ["url": item.url, "name": item.name]
            mi.toolTip = item.url
            menu.addItem(mi)
        }
        if !history.isEmpty { add("Clear History", #selector(clearHistory)) }

        menu.addItem(.separator())
        add("Settings…", #selector(openSettingsAction), key: ",")
        add("Quit Poltergeist", #selector(quit), key: "q")
    }

    private func add(_ title: String, _ sel: Selector, key: String = "") {
        // Tab-separated hint is shown as plain text; hotkeys are global, not menu key equivalents.
        let parts = title.components(separatedBy: "\t")
        let item = NSMenuItem(title: parts[0], action: sel, keyEquivalent: key)
        item.target = self
        if parts.count > 1, !parts[1].isEmpty {
            item.attributedTitle = NSAttributedString(string: parts[0] + "   " + parts[1],
                attributes: [.font: NSFont.menuFont(ofSize: 0)])
        }
        menu.addItem(item)
    }

    @objc private func recordArea() { ScreenRecorder.shared.start(.area) }
    @objc private func recordWindow() { ScreenRecorder.shared.start(.window) }
    @objc private func recordFull() { ScreenRecorder.shared.start(.fullscreen) }
    @objc private func stopRecording() { ScreenRecorder.shared.stop(upload: true) }
    @objc private func pauseRecording() { ScreenRecorder.shared.togglePause() }
    @objc private func discardRecording() { ScreenRecorder.shared.stop(upload: false) }
    @objc private func captureArea() { capture(.area) }
    @objc private func captureAreaEdit() { capture(.area, edit: true) }
    @objc private func captureFull() { capture(.fullscreen) }
    @objc private func captureWindow() { capture(.window) }
    @objc private func openSettingsAction() { openSettings() }
    @objc private func quit() { NSApp.terminate(nil) }
    @objc private func cancelUploads() { UploadManager.shared.cancelAll() }
    @objc private func clearHistory() {
        HistoryStore.shared.clear()
        UploadManager.shared.clearFinished()
    }
    @objc private func copyHistory(_ sender: NSMenuItem) {
        guard let info = sender.representedObject as? [String: String], let url = info["url"] else { return }
        copy(Settings.shared.linkFormat.format(url: url, name: info["name"] ?? ""))
    }

    @objc private func pickFile() {
        // A settings window left open on another Space would drag us there when the app activates.
        if let w = settingsWindow, !w.isOnActiveSpace { w.orderOut(nil) }
        NSApp.activate(ignoringOtherApps: true)
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        if panel.runModal() == .OK { panel.urls.forEach { upload($0) } }
    }

    @objc private func uploadClipboard() {
        let pb = NSPasteboard.general
        if let urls = pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL],
           !urls.isEmpty {
            urls.forEach { upload($0) }
        } else if let tiff = pb.data(forType: .tiff), let rep = NSBitmapImageRep(data: tiff),
                  let png = rep.representation(using: .png, properties: [:]) {
            let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("Clipboard \(Int(Date().timeIntervalSince1970)).png")
            try? png.write(to: tmp)
            upload(tmp, deleteAfter: true)
        } else {
            notify("Nothing to upload", "The clipboard has no image or file.")
        }
    }

    // MARK: Capture / upload

    private func capture(_ mode: CaptureMode, edit: Bool = false) {
        let editing = edit || Settings.shared.editBeforeUpload
        switch mode {
        case .area:
            ActionHint.text = editing ? "✏️  Screenshot + editor  ·  drag to select  ·  Esc to cancel" : "📷  Taking a screenshot  ·  drag to select  ·  Esc to cancel"
        case .window:
            ActionBadge.shared.show(symbol: editing ? "pencil.and.outline" : "macwindow", text: editing ? "Screenshot of a window, then editing" : "Taking a screenshot of a window", seconds: 2.4)
        case .fullscreen:
            ActionBadge.shared.show(symbol: editing ? "pencil.and.outline" : "camera.fill", text: editing ? "Full-screen screenshot, then editing" : "Taking a full-screen screenshot", seconds: 1.6)
        }
        Task { @MainActor in
            defer { ActionHint.text = "" }
            switch await Capture.run(mode) {
            case .file(let url):
                let st = Settings.shared
                st.play(st.captureSound)
                st.totalScreenshots += 1
                ImageEffects.applyToFile(url)
                if edit || Settings.shared.editBeforeUpload {
                    ImageEditor.edit(url) { [weak self] result in
                        switch result {
                        case .upload(let final):
                            if final != url { try? FileManager.default.removeItem(at: url) }   // the edited copy replaces the capture
                            self?.deliverScreenshot(final)
                        case .copy(let final):
                            if final != url { try? FileManager.default.removeItem(at: url) }
                            self?.copyScreenshot(final)
                        case .cancel:
                            try? FileManager.default.removeItem(at: url)
                        }
                    }
                } else {
                    deliverScreenshot(url)
                }
            case .cancelled: break
            case .failed(let why):
                Settings.shared.play(Settings.shared.errorSound)
                if Settings.shared.notifyOnError { notify("Screenshot failed", why) }
                UploadManager.shared.report("Screenshot", why)
                NSLog("Poltergeist capture failed: \(why)")
            }
        }
    }

    /// "Copy to clipboard" from the screenshot editor: the picture goes on the clipboard (still saved locally if that's on).
    private func copyScreenshot(_ url: URL) {
        let s = Settings.shared
        let kept = saveCopy(of: url)                                   // moves it to the screenshot folder when saving is on
        let file = kept ?? url
        guard let image = NSImage(contentsOf: file) else {
            UploadManager.shared.report("Copy", "Couldn't read the image to copy it.")
            return
        }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.writeObjects([image])
        if kept == nil { try? FileManager.default.removeItem(at: url) }
        s.play(s.successSound)
        if s.notifyOnSuccess { notify("Copied to clipboard", file.lastPathComponent) }
    }

    private func deliverScreenshot(_ url: URL) {
        if let saved = saveCopy(of: url) { upload(saved) }   // upload the saved file itself
        else { upload(url, deleteAfter: true) }
    }

    /// Keeps a local copy of a screenshot in the user's chosen folder. Returns the saved file, or nil if saving is off or failed.
    private func saveCopy(of temp: URL) -> URL? {
        let s = Settings.shared
        guard s.saveScreenshots else { return nil }
        return FileSaver.save(temp, into: s.saveFolder, label: "Save screenshot")
    }

    /// A finished recording: optionally edit it (trim, cut, annotate), keep the backup copy if enabled, then upload.
    private func finishRecording(_ temp: URL, forceEdit: Bool = false) {
        let s = Settings.shared
        s.totalRecordings += 1
        Task { @MainActor in
            let duration = await VideoTrimmer.duration(of: temp)
            // Without the editor the recording still has to go out as ONE audio track (browsers play only the first).
            let roles = await AudioTracks.load(from: AVURLAsset(url: temp)).map { $0.role }
            guard s.trimBeforeUpload || forceEdit, duration >= 1 else {
                var plain = VideoProject(duration: max(duration, 0.1))
                plain.audioRoles = roles
                renderAndDeliver(original: temp, project: plain)
                return
            }
            let folder = s.keepRecordings ? (s.recordFolder as NSString).abbreviatingWithTildeInPath : nil
            VideoEditor.show(temp, duration: duration, backupFolder: folder) { [weak self] result in
                guard let self else { return }
                switch result {
                case .skip:
                    self.deliverRecording(original: temp, upload: nil)
                case .upload(let project):
                    self.renderAndDeliver(original: temp, project: project)
                case .copy(let project):
                    self.renderAndDeliver(original: temp, project: project, copy: true)
                }
            }
        }
    }

    private func renderAndDeliver(original: URL, project: VideoProject, copy: Bool = false) {
        let verb = copy ? "copied" : "uploaded"
        Task { @MainActor in
            if project.needsRender {
                // Annotations and cuts mean re-encoding; show progress in the Uploads list.
                let task = UploadManager.shared.beginTask("Rendering \(original.lastPathComponent)")
                do {
                    let rendered = try await VideoExporter.export(source: original, project: project,
                                                                  bitrateMbps: Settings.shared.recordBitrate) { p in
                        UploadManager.shared.taskProgress(task, p)
                    }
                    UploadManager.shared.endTask(task)
                    deliverRecording(original: original, upload: rendered, copy: copy)
                } catch {
                    UploadManager.shared.endTask(task, error: "Couldn't render: \(error.localizedDescription). The full recording was \(verb) instead.")
                    deliverRecording(original: original, upload: original, copy: copy)
                }
            } else if project.needsAudioMix {
                // Several audio tracks and/or mutes: mix the audio into one track, copy the video as-is.
                let task = UploadManager.shared.beginTask("Preparing \(original.lastPathComponent)")
                do {
                    let mixed = try await VideoExporter.remuxWithMixedAudio(source: original, project: project) { p in
                        UploadManager.shared.taskProgress(task, p)
                    }
                    UploadManager.shared.endTask(task)
                    deliverRecording(original: original, upload: mixed, copy: copy)
                } catch {
                    UploadManager.shared.endTask(task, error: "Couldn't mix the audio: \(error.localizedDescription). The original was \(verb) instead.")
                    deliverRecording(original: original, upload: original, copy: copy)
                }
            } else if project.isTrimmed {
                if let trimmed = await VideoTrimmer.export(original, start: project.trimStart, end: project.trimEnd) {
                    deliverRecording(original: original, upload: trimmed, copy: copy)
                } else {
                    UploadManager.shared.report("Trim", "Couldn't trim the recording, so the full video was \(verb) instead.")
                    deliverRecording(original: original, upload: original, copy: copy)
                }
            } else {
                deliverRecording(original: original, upload: original, copy: copy)
            }
        }
    }

    /// `original` is the full recording; `uploadURL` is what to send (the same file, a trimmed copy, or nil for nothing).
    /// With backups on, the full original always ends up in the backup folder.
    private func deliverRecording(original: URL, upload uploadURL: URL?, copy: Bool = false) {
        let s = Settings.shared
        let backedUp = s.keepRecordings ? FileSaver.save(original, into: s.recordFolder, label: "Save recording") : nil
        guard let uploadURL else {
            if backedUp == nil { try? FileManager.default.removeItem(at: original) }   // "don't upload" with no backup: nothing is kept
            return
        }
        if copy {                                                    // clipboard instead of upload
            if uploadURL == original {
                copyVideoToClipboard(backedUp ?? original)
            } else {
                copyVideoToClipboard(uploadURL)
                if backedUp == nil { try? FileManager.default.removeItem(at: original) }
            }
            return
        }
        if uploadURL == original {
            if let backedUp { upload(backedUp) } else { upload(original, deleteAfter: true) }
        } else {
            upload(uploadURL, deleteAfter: true)
            if backedUp == nil { try? FileManager.default.removeItem(at: original) }
        }
    }

    /// Puts the video FILE on the clipboard, so ⌘V in Finder, Discord, Slack and so on pastes the video. A temporary file is
    /// moved somewhere that sticks around; a file already in the backup folder is used where it is.
    private func copyVideoToClipboard(_ file: URL) {
        let fm = FileManager.default
        var target = file
        let temp = fm.temporaryDirectory.path
        if file.path.hasPrefix(temp) {
            let dir = fm.temporaryDirectory.appendingPathComponent("Poltergeist Clipboard", isDirectory: true)
            try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
            var dest = dir.appendingPathComponent(file.lastPathComponent)
            var n = 2
            while fm.fileExists(atPath: dest.path) {
                dest = dir.appendingPathComponent("\(file.deletingPathExtension().lastPathComponent) \(n).\(file.pathExtension)")
                n += 1
            }
            if (try? fm.moveItem(at: file, to: dest)) != nil { target = dest }
        }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.writeObjects([target as NSURL])
        let s = Settings.shared
        s.play(s.successSound)
        if s.notifyOnSuccess { notify("Video copied to clipboard", "Paste it anywhere you'd attach a file.") }
    }

    private func upload(_ file: URL, deleteAfter: Bool = false) {
        UploadManager.shared.enqueue(file, deleteAfter: deleteAfter)
    }

    private func copy(_ s: String) { UploadManager.copy(s) }
    private func notify(_ title: String, _ body: String) { UploadManager.notify(title, body) }

    // MARK: Settings window

    private func openSettings() {
        if settingsWindow == nil {
            let w = NSWindow(contentViewController: NSHostingController(rootView: SettingsView()))
            w.title = "Poltergeist"
            w.styleMask = [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView]
            w.titlebarAppearsTransparent = true
            w.appearance = NSAppearance(named: .darkAqua)
            w.backgroundColor = NSColor(Theme.background)
            w.setContentSize(NSSize(width: 968, height: 720))
            w.minSize = NSSize(width: 640, height: 480)
            w.isReleasedWhenClosed = false
            w.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
            w.center()
            settingsWindow = w
        }
        // Order the window front first so it moves to this Space, then activate.
        settingsWindow?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    // MARK: Icon

    /// The ghost as a template icon; with a badge it's drawn in the menu-bar text colour plus a coloured dot.
    static func ghostIcon(badge: NSColor? = nil) -> NSImage {
        let img = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { _ in
            let p = NSBezierPath()
            p.windingRule = .evenOdd
            // body: dome on top, three evenly hanging lobes along the bottom
            p.move(to: NSPoint(x: 3, y: 3))
            p.line(to: NSPoint(x: 3, y: 9))
            p.curve(to: NSPoint(x: 15, y: 9), controlPoint1: NSPoint(x: 3, y: 17), controlPoint2: NSPoint(x: 15, y: 17))
            p.line(to: NSPoint(x: 15, y: 3))
            // three evenly hanging lobes along the bottom
            p.curve(to: NSPoint(x: 11, y: 3), controlPoint1: NSPoint(x: 14.4, y: 0.3), controlPoint2: NSPoint(x: 11.6, y: 0.3))
            p.curve(to: NSPoint(x: 7, y: 3), controlPoint1: NSPoint(x: 10.4, y: 0.3), controlPoint2: NSPoint(x: 7.6, y: 0.3))
            p.curve(to: NSPoint(x: 3, y: 3), controlPoint1: NSPoint(x: 6.4, y: 0.3), controlPoint2: NSPoint(x: 3.6, y: 0.3))
            p.close()
            p.append(NSBezierPath(ovalIn: NSRect(x: 5.6, y: 8.6, width: 2.4, height: 3)))
            p.append(NSBezierPath(ovalIn: NSRect(x: 10, y: 8.6, width: 2.4, height: 3)))
            (badge == nil ? NSColor.black : NSColor.labelColor).setFill()
            p.fill()
            if let badge {
                let dot = NSRect(x: 11.5, y: 11.5, width: 6.5, height: 6.5)
                NSColor.black.withAlphaComponent(0.35).setFill()
                NSBezierPath(ovalIn: dot.insetBy(dx: -1, dy: -1)).fill()   // thin halo so the dot reads on any menu bar
                badge.setFill()
                NSBezierPath(ovalIn: dot).fill()
            }
            return true
        }
        img.isTemplate = badge == nil
        return img
    }
}
