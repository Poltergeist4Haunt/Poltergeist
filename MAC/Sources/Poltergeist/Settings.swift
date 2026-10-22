import Foundation
import AppKit
import Security
import ServiceManagement
import Carbon.HIToolbox

extension Notification.Name {
    static let hotkeysChanged = Notification.Name("PoltergeistHotkeysChanged")
}

enum LinkFormat: String, CaseIterable, Identifiable {
    case url, direct, markdown, bbcode
    var id: String { rawValue }
    var title: String {
        switch self {
        case .url: return "URL"
        case .direct: return "Direct URL (with extension)"
        case .markdown: return "Markdown"
        case .bbcode: return "BBCode"
        }
    }

    func format(url: String, name: String) -> String {
        let direct = Self.withExtension(url, name: name)
        switch self {
        case .url: return url
        case .direct: return direct
        case .markdown: return "![\(name)](\(direct))"
        case .bbcode: return "[img]\(direct)[/img]"
        }
    }

    /// Haunt can hide the extension; add it back from the file name when the URL has none.
    private static func withExtension(_ url: String, name: String) -> String {
        let ext = (name as NSString).pathExtension
        guard !ext.isEmpty, let u = URL(string: url), u.pathExtension.isEmpty else { return url }
        return url + "." + ext
    }
}

enum Sounds {
    static let none = "None"
    static let names = ["Basso", "Blow", "Bottle", "Frog", "Funk", "Glass", "Hero", "Morse", "Ping", "Pop",
                        "Purr", "Sosumi", "Submarine", "Tink"]
    /// A sound can also be the full path of a file the user picked.
    static func isCustom(_ name: String) -> Bool { name.hasPrefix("/") }
}

enum HotKeyAction: String, CaseIterable, Identifiable {
    case area, fullscreen, window, editArea, recordArea, recordFullscreen, recordWindow
    var id: String { rawValue }
    var title: String {
        switch self {
        case .area: return "Capture area"
        case .fullscreen: return "Capture full screen"
        case .window: return "Capture window"
        case .editArea: return "Capture area & edit"
        case .recordArea: return "Record area"
        case .recordFullscreen: return "Record full screen"
        case .recordWindow: return "Record window"
        }
    }
    var defaultBinding: HotKeyBinding {
        let m = controlKey | shiftKey
        switch self {
        case .area: return HotKeyBinding(keyCode: kVK_ANSI_4, modifiers: m, label: "4")
        case .fullscreen: return HotKeyBinding(keyCode: kVK_ANSI_3, modifiers: m, label: "3")
        case .window: return HotKeyBinding(keyCode: kVK_ANSI_5, modifiers: m, label: "5")
        case .editArea: return HotKeyBinding(keyCode: kVK_ANSI_2, modifiers: m, label: "2")
        case .recordArea: return HotKeyBinding(keyCode: kVK_ANSI_6, modifiers: m, label: "6")
        case .recordFullscreen: return HotKeyBinding(keyCode: kVK_ANSI_7, modifiers: m, label: "7")
        case .recordWindow: return HotKeyBinding(keyCode: kVK_ANSI_8, modifiers: m, label: "8")
        }
    }
}

/// A global shortcut in Carbon terms. keyCode < 0 means "no shortcut".
struct HotKeyBinding: Codable, Equatable {
    var keyCode: Int
    var modifiers: Int
    var label: String

    static let none = HotKeyBinding(keyCode: -1, modifiers: 0, label: "")
    var isSet: Bool { keyCode >= 0 }

    var hint: String { isSet ? display : "" }

    var display: String {
        guard isSet else { return "Not set" }
        var s = ""
        if modifiers & controlKey != 0 { s += "⌃" }
        if modifiers & optionKey != 0 { s += "⌥" }
        if modifiers & shiftKey != 0 { s += "⇧" }
        if modifiers & cmdKey != 0 { s += "⌘" }
        return s + label
    }
}

enum Keychain {
    private static let service = "gg.haunt.poltergeist"
    private static let account = "api-key"

    static func get() -> String {
        let q: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess,
              let data = out as? Data else { return "" }
        return String(data: data, encoding: .utf8) ?? ""
    }

    static func set(_ value: String) {
        let base: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(base as CFDictionary)
        guard !value.isEmpty else { return }
        var add = base
        add[kSecValueData as String] = Data(value.utf8)
        SecItemAdd(add as CFDictionary, nil)
    }
}

final class Settings: ObservableObject {
    static let shared = Settings()
    private let d = UserDefaults.standard

    @Published var apiKey: String { didSet { Keychain.set(apiKey) } }
    /// "never", "quit", "day" or "week"
    @Published var historyClear: String { didSet { d.set(historyClear, forKey: "historyClear"); HistoryStore.shared.pruneExpired() } }
    @Published var successSound: String { didSet { d.set(successSound, forKey: "successSound") } }
    @Published var errorSound: String { didSet { d.set(errorSound, forKey: "errorSound") } }
    /// Played when a screenshot is taken. A system sound, "None", or the path of your own file.
    @Published var captureSound: String { didSet { d.set(captureSound, forKey: "captureSound") } }

    // Image effects, applied to every screenshot (like ShareX's image effects)
    @Published var fxOn: Bool { didSet { d.set(fxOn, forKey: "fxOn") } }
    @Published var fxRound: Bool { didSet { d.set(fxRound, forKey: "fxRound") } }
    @Published var fxRadius: Int { didSet { d.set(fxRadius, forKey: "fxRadius") } }
    @Published var fxBorder: Int { didSet { d.set(fxBorder, forKey: "fxBorder") } }
    @Published var fxBorderColor: String { didSet { d.set(fxBorderColor, forKey: "fxBorderColor") } }
    @Published var fxShadow: Bool { didSet { d.set(fxShadow, forKey: "fxShadow") } }
    @Published var fxShadowSize: Int { didSet { d.set(fxShadowSize, forKey: "fxShadowSize") } }

    // Usage numbers for the Stats page
    @Published var totalSeconds: Double { didSet { d.set(totalSeconds, forKey: "totalSeconds") } }
    @Published var bytesUploaded: Int { didSet { d.set(bytesUploaded, forKey: "bytesUploaded") } }
    @Published var totalUploads: Int { didSet { d.set(totalUploads, forKey: "totalUploads") } }
    @Published var totalScreenshots: Int { didSet { d.set(totalScreenshots, forKey: "totalScreenshots") } }
    @Published var totalRecordings: Int { didSet { d.set(totalRecordings, forKey: "totalRecordings") } }
    var firstRun: Date
    @Published var notifyOnSuccess: Bool { didSet { d.set(notifyOnSuccess, forKey: "notifyOnSuccess") } }
    @Published var notifyOnError: Bool { didSet { d.set(notifyOnError, forKey: "notifyOnError") } }
    @Published var showToast: Bool { didSet { d.set(showToast, forKey: "showToast") } }
    @Published var copyImage: Bool { didSet { d.set(copyImage, forKey: "copyImage") } }
    @Published var linkFormat: LinkFormat { didSet { d.set(linkFormat.rawValue, forKey: "linkFormat") } }
    @Published var stripMetadata: Bool { didSet { d.set(stripMetadata, forKey: "stripMetadata") } }
    @Published var editBeforeUpload: Bool { didSet { d.set(editBeforeUpload, forKey: "editBeforeUpload") } }
    @Published var trimBeforeUpload: Bool { didSet { d.set(trimBeforeUpload, forKey: "trimBeforeUpload") } }
    @Published var recordSystemAudio: Bool { didSet { d.set(recordSystemAudio, forKey: "recordSystemAudio") } }
    @Published var recordMic: Bool { didSet { d.set(recordMic, forKey: "recordMic") } }
    @Published var micDeviceID: String { didSet { d.set(micDeviceID, forKey: "micDeviceID") } }
    @Published var recordBitrate: Int { didSet { d.set(recordBitrate, forKey: "recordBitrate") } }
    @Published var recordFPS: Int { didSet { d.set(recordFPS, forKey: "recordFPS") } }
    @Published var keepRecordings: Bool { didSet { d.set(keepRecordings, forKey: "keepRecordings") } }
    @Published var recordFolder: String { didSet { d.set(recordFolder, forKey: "recordFolder") } }
    @Published var freezeOnCapture: Bool { didSet { d.set(freezeOnCapture, forKey: "freezeOnCapture") } }
    @Published var saveScreenshots: Bool { didSet { d.set(saveScreenshots, forKey: "saveScreenshots") } }
    @Published var saveFolder: String { didSet { d.set(saveFolder, forKey: "saveFolder") } }

    private var customSound: NSSound?                       // kept alive until it has finished playing

    func play(_ name: String) {
        guard name != Sounds.none, !name.isEmpty else { return }
        if Sounds.isCustom(name) {
            guard FileManager.default.fileExists(atPath: name) else { return }
            customSound = NSSound(contentsOfFile: name, byReference: true)
            customSound?.play()
            return
        }
        NSSound(named: name)?.play()
    }
    @Published var copyLink: Bool { didSet { d.set(copyLink, forKey: "copyLink") } }
    @Published var openInBrowser: Bool { didSet { d.set(openInBrowser, forKey: "openInBrowser") } }
    @Published var launchAtLogin: Bool {
        didSet {
            d.set(launchAtLogin, forKey: "launchAtLogin")
            do {
                if launchAtLogin { try SMAppService.mainApp.register() }
                else { try SMAppService.mainApp.unregister() }
            } catch { NSLog("Launch at login failed: \(error)") }
        }
    }

    @Published var hotkeys: [HotKeyAction: HotKeyBinding] {
        didSet {
            let raw = Dictionary(uniqueKeysWithValues: hotkeys.map { ($0.key.rawValue, $0.value) })
            if let data = try? JSONEncoder().encode(raw) { d.set(data, forKey: "hotkeys") }
        }
    }

    func binding(_ a: HotKeyAction) -> HotKeyBinding { hotkeys[a] ?? a.defaultBinding }

    private init() {
        var keys: [HotKeyAction: HotKeyBinding] = [:]
        let saved = (UserDefaults.standard.data(forKey: "hotkeys"))
            .flatMap { try? JSONDecoder().decode([String: HotKeyBinding].self, from: $0) } ?? [:]
        for a in HotKeyAction.allCases { keys[a] = saved[a.rawValue] ?? a.defaultBinding }
        hotkeys = keys
        apiKey = Keychain.get()
        let soundsOn = d.object(forKey: "playSounds") as? Bool ?? true // legacy single toggle
        historyClear = d.string(forKey: "historyClear") ?? "never"
        successSound = d.string(forKey: "successSound") ?? (soundsOn ? "Glass" : Sounds.none)
        errorSound = d.string(forKey: "errorSound") ?? (soundsOn ? "Basso" : Sounds.none)
        captureSound = d.string(forKey: "captureSound") ?? Sounds.none
        fxOn = d.object(forKey: "fxOn") as? Bool ?? false
        fxRound = d.object(forKey: "fxRound") as? Bool ?? false
        fxRadius = d.object(forKey: "fxRadius") as? Int ?? 14
        fxBorder = d.object(forKey: "fxBorder") as? Int ?? 0
        fxBorderColor = d.string(forKey: "fxBorderColor") ?? "#f27db3"
        fxShadow = d.object(forKey: "fxShadow") as? Bool ?? false
        fxShadowSize = d.object(forKey: "fxShadowSize") as? Int ?? 18
        totalSeconds = d.object(forKey: "totalSeconds") as? Double ?? 0
        bytesUploaded = d.object(forKey: "bytesUploaded") as? Int ?? 0
        totalUploads = d.object(forKey: "totalUploads") as? Int ?? 0
        totalScreenshots = d.object(forKey: "totalScreenshots") as? Int ?? 0
        totalRecordings = d.object(forKey: "totalRecordings") as? Int ?? 0
        if let first = d.object(forKey: "firstRun") as? Date { firstRun = first } else { firstRun = Date(); d.set(firstRun, forKey: "firstRun") }
        notifyOnSuccess = d.object(forKey: "notifyOnSuccess") as? Bool ?? true
        notifyOnError = d.object(forKey: "notifyOnError") as? Bool ?? true
        showToast = d.object(forKey: "showToast") as? Bool ?? true
        copyImage = d.object(forKey: "copyImage") as? Bool ?? false
        linkFormat = LinkFormat(rawValue: d.string(forKey: "linkFormat") ?? "") ?? .url
        stripMetadata = d.object(forKey: "stripMetadata") as? Bool ?? true
        editBeforeUpload = d.object(forKey: "editBeforeUpload") as? Bool ?? false
        trimBeforeUpload = d.object(forKey: "trimBeforeUpload") as? Bool ?? false
        recordSystemAudio = d.object(forKey: "recordSystemAudio") as? Bool ?? false
        recordMic = d.object(forKey: "recordMic") as? Bool ?? false
        micDeviceID = d.string(forKey: "micDeviceID") ?? ""
        recordBitrate = d.object(forKey: "recordBitrate") as? Int ?? 8
        recordFPS = d.object(forKey: "recordFPS") as? Int ?? 30
        keepRecordings = d.object(forKey: "keepRecordings") as? Bool ?? true
        recordFolder = d.string(forKey: "recordFolder") ?? (NSHomeDirectory() as NSString).appendingPathComponent("Movies/Poltergeist")
        freezeOnCapture = d.object(forKey: "freezeOnCapture") as? Bool ?? true
        saveScreenshots = d.object(forKey: "saveScreenshots") as? Bool ?? false
        saveFolder = d.string(forKey: "saveFolder") ?? (NSHomeDirectory() as NSString).appendingPathComponent("Pictures/Poltergeist")
        copyLink = d.object(forKey: "copyLink") as? Bool ?? true
        openInBrowser = d.object(forKey: "openInBrowser") as? Bool ?? false
        launchAtLogin = d.object(forKey: "launchAtLogin") as? Bool ?? false
    }
}

struct HistoryItem: Codable, Identifiable {
    var id = UUID()
    var url: String
    var name: String
    var date: Date
}

final class HistoryStore: ObservableObject {
    static let shared = HistoryStore()
    @Published private(set) var items: [HistoryItem] = []
    private let key = "history"

    private init() {
        if let data = UserDefaults.standard.data(forKey: key),
           let decoded = try? JSONDecoder().decode([HistoryItem].self, from: data) {
            items = decoded
        }
    }

    func add(url: String, name: String) {
        items.insert(HistoryItem(url: url, name: name, date: Date()), at: 0)
        items = Array(items.prefix(100))
        save()
    }

    func clear() { items = []; save() }

    /// Drops entries older than the auto-clear setting ("day" / "week"). "quit" is handled at launch and on quit.
    func pruneExpired() {
        let days: Double
        switch Settings.shared.historyClear {
        case "day": days = 1
        case "week": days = 7
        default: return
        }
        let cutoff = Date().addingTimeInterval(-days * 86400)
        let kept = items.filter { $0.date >= cutoff }
        if kept.count != items.count { items = kept; save() }
    }

    private func save() {
        if let data = try? JSONEncoder().encode(items) { UserDefaults.standard.set(data, forKey: key) }
    }
}

/// Embed customisation, mirroring the Haunt dashboard's editor.
/// Stored locally only for now: no documented endpoint to sync it to the account.
struct EmbedConfig: Codable {
    var authorName = ""
    var authorURL = ""
    var providerName = "Image Host"
    var providerURL = "https://i.haunt.gg"
    var title = "{file.name}"
    var color = "#f27db3"
    var description = "{file.size}"
    var showTimestamp = true

    var discordText = "## {file.name}\n-# {file.size} • uploaded by {user.name}"
    var discordAccent = true
    var discordSpoiler = false
    var button1Label = "Open"
    var button1Link = "{file.url}"
    var button2Label = "Original file"
    var button2Link = "{file.rawUrl}"

    static func load() -> EmbedConfig {
        guard let data = UserDefaults.standard.data(forKey: "embedConfig"),
              let c = try? JSONDecoder().decode(EmbedConfig.self, from: data) else { return EmbedConfig() }
        return c
    }

    func save() {
        if let data = try? JSONEncoder().encode(self) { UserDefaults.standard.set(data, forKey: "embedConfig") }
    }
}

enum Template {
    static let sample: [String: String] = [
        "user.id": "1", "user.name": "haunt", "user.uploads": "128", "user.uploads.today": "3",
        "user.storage": "412 MB",
        "file.id": "a1b2c3d4", "file.name": "a1b2c3d4.png", "file.originalName": "Screenshot.png",
        "file.size": "793 KB", "file.uploadAt": "08/20/2026, 06:00 AM",
        "file.url": "https://i.haunt.gg/a1b2c3d4", "file.rawUrl": "https://i.haunt.gg/a1b2c3d4.png",
    ]

    static func render(_ s: String, vars: [String: String] = sample) -> String {
        var out = s
        // longest keys first so {user.uploads.today} wins over {user.uploads}
        for (k, v) in vars.sorted(by: { $0.key.count > $1.key.count }) {
            out = out.replacingOccurrences(of: "{\(k)}", with: v)
        }
        return out
    }
}
