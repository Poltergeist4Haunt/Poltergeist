import AppKit
import Carbon.HIToolbox

enum CaptureMode {
    case area, window, fullscreen

    var arguments: [String] {
        switch self {
        case .area: return ["-i", "-x"]
        case .window: return ["-i", "-w", "-x"]
        case .fullscreen: return ["-x"]
        }
    }
}

enum CaptureResult {
    case file(URL)
    case cancelled
    case failed(String)
}

enum Capture {
    static func run(_ mode: CaptureMode) async -> CaptureResult {
        if !CGPreflightScreenCaptureAccess() {
            CGRequestScreenCaptureAccess()
            return .failed("Screen Recording isn't allowed for Poltergeist. Turn it on in System Settings → Privacy & Security → Screen & System Audio Recording, then relaunch Poltergeist.")
        }

        // Area capture: freeze and dim the screen while selecting (falls back to the system selector if that isn't possible).
        if mode == .area, Settings.shared.freezeOnCapture, let result = await FrozenCapture.run() { return result }

        let fmt = DateFormatter()
        fmt.dateFormat = "yyyy-MM-dd HH.mm.ss"
        let out = FileManager.default.temporaryDirectory
            .appendingPathComponent("Screenshot \(fmt.string(from: Date())).png")

        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        p.arguments = mode.arguments + [out.path]
        let started = Date()
        let status: Int32 = await withCheckedContinuation { c in
            p.terminationHandler = { c.resume(returning: $0.terminationStatus) }
            do { try p.run() } catch { c.resume(returning: -1) }
        }
        if FileManager.default.fileExists(atPath: out.path) { return .file(out) }
        // Pressing Esc (or clicking away) in the selector makes screencapture exit with status 1 and no file:
        // that's the user cancelling, not an error. Permission problems were already caught above.
        if mode != .fullscreen && (status == 0 || status == 1) { return .cancelled }
        return .failed("The screenshot tool exited with status \(status) and produced no image.")
    }
}

/// Global hotkeys via Carbon (no Accessibility permission needed).
final class HotKeys {
    static let shared = HotKeys()
    private var actions: [UInt32: () -> Void] = [:]
    private var refs: [EventHotKeyRef] = []
    private var installed = false
    private var nextID: UInt32 = 1

    func unregisterAll() {
        refs.forEach { UnregisterEventHotKey($0) }
        refs = []
        actions = [:]
    }

    func register(keyCode: Int, modifiers: Int, action: @escaping () -> Void) {
        install()
        let id = nextID
        nextID += 1
        actions[id] = action
        var ref: EventHotKeyRef?
        let hkID = EventHotKeyID(signature: OSType(0x504C5447), id: id) // 'PLTG'
        let status = RegisterEventHotKey(UInt32(keyCode), UInt32(modifiers), hkID, GetApplicationEventTarget(), 0, &ref)
        if status == noErr, let ref { refs.append(ref) } else {
            actions[id] = nil
            NSLog("Poltergeist: RegisterEventHotKey failed (\(status)) for key \(keyCode) mods \(modifiers)")
        }
    }

    private func install() {
        guard !installed else { return }
        installed = true
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            var hk = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                              nil, MemoryLayout<EventHotKeyID>.size, nil, &hk)
            DispatchQueue.main.async { HotKeys.shared.actions[hk.id]?() }
            return noErr
        }, 1, &spec, nil, nil)
    }
}
