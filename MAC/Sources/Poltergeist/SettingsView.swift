import SwiftUI
import AVFoundation
import Carbon.HIToolbox
import UniformTypeIdentifiers

enum SettingsTab: String, CaseIterable, Identifiable {
    case upload = "Upload", gallery = "Gallery", account = "Account", general = "General", recordings = "Recordings", effects = "Effects", embed = "Embed", domain = "Domain", stats = "Stats"
    var id: String { rawValue }
    var icon: String {
        switch self {
        case .upload: return "arrow.up.circle.fill"
        case .gallery: return "photo.on.rectangle"
        case .effects: return "wand.and.stars"
        case .stats: return "chart.bar.fill"
        case .account: return "key.fill"
        case .general: return "gearshape.fill"
        case .recordings: return "record.circle"
        case .embed: return "rectangle.on.rectangle"
        case .domain: return "globe"
        }
    }
}

struct SettingsView: View {
    @State private var tab: SettingsTab = Settings.shared.apiKey.isEmpty ? .account : .upload

    var body: some View {
        VStack(spacing: 0) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ForEach(SettingsTab.allCases) { t in
                        Button { tab = t } label: {
                            Label(t.rawValue, systemImage: t.icon)
                        }
                        .buttonStyle(TabButtonStyle(selected: tab == t))
                    }
                }
                .padding(.horizontal, 24)
            }
            .padding(.top, 38).padding(.bottom, 14)

            ScrollView {
                Group {
                    switch tab {
                    case .upload: UploadView()
                    case .gallery: GalleryView()
                    case .effects: EffectsView()
                    case .stats: StatsView()
                    case .account: AccountView()
                    case .general: GeneralView()
                    case .recordings: RecordingsView()
                    case .embed: EmbedView()
                    case .domain: DomainView()
                    }
                }
                .padding(.horizontal, 24).padding(.bottom, 24)
            }
        }
        .background(Theme.background)
        .preferredColorScheme(.dark)
        .tint(Theme.accent)
    }
}

struct AccountView: View {
    @ObservedObject private var s = Settings.shared
    @State private var reveal = false

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Card {
                Text("API key").font(.system(size: 17, weight: .semibold)).foregroundColor(Theme.text)
                Text("Poltergeist uploads to your Haunt Image Host with an API key.")
                    .font(.system(size: 13)).foregroundColor(Theme.muted)
                HStack {
                    Group {
                        if reveal { TextField("haunt_…", text: $s.apiKey) }
                        else { SecureField("haunt_…", text: $s.apiKey) }
                    }
                    .textFieldStyle(HauntFieldStyle())
                    Button(reveal ? "Hide" : "Show") { reveal.toggle() }
                        .buttonStyle(PillButtonStyle(prominent: false))
                }
                Text(s.apiKey.isEmpty ? "No key set. Uploads are disabled." : "Key saved in your macOS Keychain.")
                    .font(.system(size: 12)).foregroundColor(s.apiKey.isEmpty ? Theme.warning : Theme.muted)
            }
            Card {
                Text("Create a key").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
                ForEach(Array([
                    "Open your Haunt dashboard and create an API key.",
                    "Name it anything, e.g. “Poltergeist”.",
                    "Under Image Host, enable Upload and Read.",
                    "Paste the key above.",
                ].enumerated()), id: \.offset) { i, line in
                    Text("\(i + 1).  \(line)").font(.system(size: 13)).foregroundColor(Theme.muted)
                }
                Button("Open haunt.gg") { NSWorkspace.shared.open(URL(string: "https://haunt.gg")!) }
                    .buttonStyle(PillButtonStyle())
            }
        }
    }
}

struct GeneralView: View {
    @ObservedObject private var s = Settings.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Card {
                sectionTitle("After upload")
                Toggle(isOn: $s.copyLink) { row("Copy to clipboard", "Copy the result when an upload finishes.") }
                Toggle(isOn: $s.copyImage) { row("Copy the image instead of the link", "Photos go on the clipboard as an image; videos still copy the link.") }
                    .disabled(!s.copyLink).opacity(s.copyLink ? 1 : 0.5)
                pickerRow("Link format", "What gets copied when it's a link.") {
                    Picker("", selection: $s.linkFormat) {
                        ForEach(LinkFormat.allCases) { Text($0.title).tag($0) }
                    }
                }
                Toggle(isOn: $s.showToast) { row("Preview popup", "A small thumbnail with a Copy button in the corner.") }
                Toggle(isOn: $s.openInBrowser) { row("Open in browser", "Open the link when an upload finishes.") }
            }.toggleStyle(HauntToggleStyle())

            Card {
                sectionTitle("Sounds and notifications")
                HStack { row("Capture sound", "When a screenshot is taken."); Spacer(); SoundPicker(value: $s.captureSound) }
                HStack { row("Success sound", "When an upload finishes."); Spacer(); SoundPicker(value: $s.successSound) }
                HStack { row("Error sound", "When something fails."); Spacer(); SoundPicker(value: $s.errorSound) }
                Toggle(isOn: $s.notifyOnSuccess) { row("Notify when an upload finishes", "") }
                Toggle(isOn: $s.notifyOnError) { row("Notify when something fails", "") }
            }.toggleStyle(HauntToggleStyle())

            Card {
                sectionTitle("Recent uploads")
                pickerRow("Clear automatically", "The menu-bar list of recent uploads.") {
                    Picker("", selection: $s.historyClear) {
                        Text("Never").tag("never")
                        Text("When Poltergeist quits").tag("quit")
                        Text("After 1 day").tag("day")
                        Text("After 7 days").tag("week")
                    }
                }
                HStack {
                    Spacer()
                    Button("Clear now") {
                        HistoryStore.shared.clear()
                        UploadManager.shared.clearFinished()
                    }.buttonStyle(PillButtonStyle(prominent: false))
                }
            }

            Card {
                sectionTitle("Privacy")
                Toggle(isOn: $s.stripMetadata) {
                    row("Strip photo metadata", "Removes location, camera and other EXIF data from JPEG, PNG, HEIC and TIFF before upload. GIFs, WebP, AVIF and videos are uploaded as-is.")
                }
            }.toggleStyle(HauntToggleStyle())

            Card {
                sectionTitle("Screenshots")
                Toggle(isOn: $s.freezeOnCapture) {
                    row("Freeze and dim the screen while selecting", "Takes a still of the screen when you press the hotkey, so what you select is exactly what was there.")
                }
                .toggleStyle(HauntToggleStyle())
                Toggle(isOn: $s.editBeforeUpload) {
                    row("Edit before uploading", "Opens a quick editor for arrows, boxes, text and blur. ⌃⇧2 always opens it, whatever this is set to.")
                }
                .toggleStyle(HauntToggleStyle())
                Toggle(isOn: $s.saveScreenshots) {
                    row("Save a copy of screenshots", "Keep each screenshot in a folder on your Mac. It's still uploaded and the link is still copied.")
                }
                .toggleStyle(HauntToggleStyle())
                HStack {
                    Text((s.saveFolder as NSString).abbreviatingWithTildeInPath)
                        .font(.system(size: 12)).foregroundColor(s.saveScreenshots ? Theme.text : Theme.muted)
                        .lineLimit(1).truncationMode(.middle)
                    Spacer()
                    Button("Choose folder…") { chooseFolder() }.buttonStyle(PillButtonStyle())
                }
            }

            Card {
                Toggle(isOn: $s.launchAtLogin) { row("Launch at login", "Start Poltergeist when you sign in.") }
            }.toggleStyle(HauntToggleStyle())

            Card {
                HStack {
                    Text("Hotkeys").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
                    Spacer()
                    Button("Reset to defaults") {
                        for a in HotKeyAction.allCases { s.hotkeys[a] = a.defaultBinding }
                        NotificationCenter.default.post(name: .hotkeysChanged, object: nil)
                    }.buttonStyle(PillButtonStyle(prominent: false))
                }
                Text("Click a shortcut, then press the new keys. Esc cancels, Delete clears it.")
                    .font(.system(size: 12)).foregroundColor(Theme.muted)
                ForEach(HotKeyAction.allCases) { a in
                    HStack {
                        Text(a.title).font(.system(size: 13)).foregroundColor(Theme.text)
                        Spacer()
                        HotKeyRecorder(action: a)
                    }
                }
            }
        }
    }

    private func sectionTitle(_ t: String) -> some View {
        Text(t).font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
    }

    private func row(_ title: String, _ sub: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title).font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
            if !sub.isEmpty { Text(sub).font(.system(size: 12)).foregroundColor(Theme.muted) }
        }
    }

    private func pickerRow<P: View>(_ title: String, _ sub: String, @ViewBuilder picker: () -> P) -> some View {
        HStack {
            row(title, sub)
            Spacer()
            picker().labelsHidden().pickerStyle(.menu).fixedSize()
        }
    }

    private func soundPicker(_ binding: Binding<String>) -> some View {
        Picker("", selection: Binding(get: { binding.wrappedValue }, set: { binding.wrappedValue = $0; s.play($0) })) {
            Text("None").tag(Sounds.none)
            Divider()
            ForEach(Sounds.names, id: \.self) { Text($0).tag($0) }
        }
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.prompt = "Choose"
        if panel.runModal() == .OK, let url = panel.url { s.saveFolder = url.path }
    }
}

struct DomainView: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Banner(text: "Choosing an upload domain isn't wired up yet. Haunt's public API docs don't list a domains endpoint, so for now pick your domain on the dashboard and Poltergeist will use it automatically.")
            Card {
                Text("Upload domain").font(.system(size: 17, weight: .semibold)).foregroundColor(Theme.text)
                Text("Links come back from Haunt already on your active domain (default i.haunt.gg).")
                    .font(.system(size: 13)).foregroundColor(Theme.muted)
                Button("Open dashboard") { NSWorkspace.shared.open(URL(string: "https://haunt.gg")!) }
                    .buttonStyle(PillButtonStyle())
            }
        }
    }
}

struct HotKeyRecorder: View {
    let action: HotKeyAction
    @ObservedObject private var s = Settings.shared
    @State private var recording = false
    @State private var monitor: Any?
    @State private var message: String?

    var body: some View {
        HStack(spacing: 10) {
            if let message { Text(message).font(.system(size: 11)).foregroundColor(Theme.warning) }
            Button { recording ? stop() : start() } label: {
                Text(recording ? "Press keys…" : s.binding(action).display)
                    .font(.system(size: 13, design: .monospaced))
                    .frame(minWidth: 96)
            }
            .buttonStyle(PillButtonStyle(prominent: recording))
        }
        .onDisappear { if recording { stop() } }
    }

    private func start() {
        message = nil
        recording = true
        HotKeys.shared.unregisterAll() // so the current shortcut can be re-pressed without firing a capture
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { e in
            handle(e)
            return nil
        }
    }

    private func stop() {
        recording = false
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        NotificationCenter.default.post(name: .hotkeysChanged, object: nil)
    }

    private func handle(_ e: NSEvent) {
        let code = Int(e.keyCode)
        let flags = e.modifierFlags.intersection(.deviceIndependentFlagsMask)
        var mods = 0
        if flags.contains(.control) { mods |= controlKey }
        if flags.contains(.option) { mods |= optionKey }
        if flags.contains(.shift) { mods |= shiftKey }
        if flags.contains(.command) { mods |= cmdKey }

        if code == kVK_Escape { stop(); return }
        if (code == kVK_Delete || code == kVK_ForwardDelete) && mods == 0 {
            s.hotkeys[action] = .none
            stop(); return
        }
        guard mods & (controlKey | optionKey | cmdKey) != 0 else {
            message = "Include ⌃, ⌥ or ⌘"
            return
        }
        let candidate = HotKeyBinding(keyCode: code, modifiers: mods, label: Self.label(for: e))
        if let other = HotKeyAction.allCases.first(where: {
            $0 != action && s.binding($0).keyCode == code && s.binding($0).modifiers == mods
        }) {
            message = "Already used by “\(other.title)”"
            return
        }
        s.hotkeys[action] = candidate
        stop()
    }

    private static let special: [Int: String] = [
        kVK_Return: "↩", kVK_Tab: "⇥", kVK_Space: "Space",
        kVK_LeftArrow: "←", kVK_RightArrow: "→", kVK_DownArrow: "↓", kVK_UpArrow: "↑",
        kVK_F1: "F1", kVK_F2: "F2", kVK_F3: "F3", kVK_F4: "F4", kVK_F5: "F5", kVK_F6: "F6",
        kVK_F7: "F7", kVK_F8: "F8", kVK_F9: "F9", kVK_F10: "F10", kVK_F11: "F11", kVK_F12: "F12",
    ]

    private static func label(for e: NSEvent) -> String {
        special[Int(e.keyCode)] ?? (e.charactersIgnoringModifiers ?? "?").uppercased()
    }
}

struct UploadView: View {
    @ObservedObject private var manager = UploadManager.shared
    @ObservedObject private var settings = Settings.shared
    @State private var targeted = false

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            if settings.apiKey.isEmpty {
                Banner(text: "Add your API key in the Account tab before uploading.")
            }

            VStack(spacing: 10) {
                Image(systemName: "arrow.up.doc.fill").font(.system(size: 30)).foregroundColor(Theme.accent)
                Text(targeted ? "Release to upload" : "Drop files here")
                    .font(.system(size: 16, weight: .semibold)).foregroundColor(Theme.text)
                Text("Images and videos Haunt supports: PNG, JPEG, WebP, GIF, AVIF, MP4, WebM, MOV")
                    .font(.system(size: 12)).foregroundColor(Theme.muted)
                Button("Choose files…") { choose() }.buttonStyle(PillButtonStyle())
            }
            .frame(maxWidth: .infinity).padding(.vertical, 34)
            .background(targeted ? Theme.accentSoft : Theme.card)
            .overlay(
                RoundedRectangle(cornerRadius: 18)
                    .strokeBorder(Theme.accent.opacity(targeted ? 0.9 : 0.35), style: StrokeStyle(lineWidth: 1.5, dash: [7, 5]))
            )
            .clipShape(RoundedRectangle(cornerRadius: 18))
            .contentShape(RoundedRectangle(cornerRadius: 18))
            .animation(.easeOut(duration: 0.1), value: targeted)
            .onDrop(of: [.fileURL], isTargeted: $targeted) { providers in
                for p in providers {
                    p.loadItem(forTypeIdentifier: UTType.fileURL.identifier, options: nil) { item, _ in
                        if let data = item as? Data, let url = URL(dataRepresentation: data, relativeTo: nil) {
                            UploadManager.shared.enqueue(url)
                        }
                    }
                }
                return true
            }

            if !manager.jobs.isEmpty {
                Card {
                    HStack {
                        Text("Uploads").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
                        Spacer()
                        Button("Clear finished") { manager.clearFinished() }
                            .buttonStyle(PillButtonStyle(prominent: false))
                    }
                    ForEach(manager.jobs) { job in JobRow(job: job) }
                }
            }
        }
    }

    private func choose() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        if panel.runModal() == .OK { panel.urls.forEach { UploadManager.shared.enqueue($0) } }
    }
}

private struct JobRow: View {
    let job: UploadJob

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(job.name).font(.system(size: 13, weight: .medium)).foregroundColor(Theme.text).lineLimit(1)
                Spacer()
                switch job.state {
                case .uploading:
                    Text(job.isWaitingForServer ? "Waiting for Haunt…" : "\(Int(job.progress * 100))%")
                        .font(.system(size: 12, design: job.isWaitingForServer ? .default : .monospaced)).foregroundColor(Theme.muted)
                    if job.isUpload {
                        Button("Cancel") { UploadManager.shared.cancel(job.id) }.buttonStyle(PillButtonStyle(prominent: false))
                    }
                case .done(let url):
                    Button("Copy link") { UploadManager.copy(url) }.buttonStyle(PillButtonStyle())
                    Button("Open") { if let u = URL(string: url) { NSWorkspace.shared.open(u) } }
                        .buttonStyle(PillButtonStyle(prominent: false))
                case .failed:
                    if job.isUpload {
                        Button { UploadManager.shared.retry(job.id) } label: { Label("Retry", systemImage: "arrow.clockwise") }
                            .buttonStyle(PillButtonStyle())
                    }
                }
            }
            switch job.state {
            case .uploading:
                ProgressView(value: job.progress).tint(Theme.accent)
            case .done(let url):
                Text(url).font(.system(size: 12)).foregroundColor(Theme.muted).textSelection(.enabled).lineLimit(1)
            case .failed(let msg):
                Text(msg).font(.system(size: 12)).foregroundColor(Color(hex: 0xff6b6b))
            }
        }
        .padding(.vertical, 4)
    }
}

struct MicDevice: Identifiable, Hashable {
    let id: String
    let name: String

    static func all() -> [MicDevice] {
        var types: [AVCaptureDevice.DeviceType] = [.builtInMicrophone, .externalUnknown]
        if #available(macOS 14.0, *) { types = [.microphone, .external] }
        let found = AVCaptureDevice.DiscoverySession(deviceTypes: types, mediaType: .audio, position: .unspecified).devices
        return found.map { MicDevice(id: $0.uniqueID, name: $0.localizedName) }.sorted { $0.name < $1.name }
    }
}

private enum PermissionState { case allowed, notAsked, denied }

private func openPrivacyPane(_ pane: String) {
    if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)") { NSWorkspace.shared.open(url) }
}

struct RecordingsView: View {
    @ObservedObject private var s = Settings.shared
    @State private var mics = MicDevice.all()
    @State private var refresh = 0
    private let bitrates = [2, 4, 8, 12, 20, 35]
    private let framerates = [15, 24, 30, 60]

    private var screenAllowed: Bool { CGPreflightScreenCaptureAccess() }
    private var micState: PermissionState {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: return .allowed
        case .notDetermined: return .notAsked
        default: return .denied
        }
    }

    var body: some View {
        let _ = refresh   // re-read permission state when the timer ticks
        VStack(alignment: .leading, spacing: 16) {
            permissionsCard
            audioCard
            qualityCard
            trimCard
            backupCard
            helpCard
        }
        .onReceive(Timer.publish(every: 1, on: .main, in: .common).autoconnect()) { _ in refresh += 1 }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            mics = MicDevice.all()
            refresh += 1
        }
    }

    // MARK: Permissions

    private var permissionsCard: some View {
        Card {
            Text("Permissions").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
            Text("Allow these now so nothing interrupts you mid-recording.").font(.system(size: 12)).foregroundColor(Theme.muted)

            permissionRow("Screen recording", "Needed for screenshots, screen recordings and desktop audio.",
                          allowed: screenAllowed, allowTitle: "Allow") {
                CGRequestScreenCaptureAccess()
            } openSettings: { openPrivacyPane("Privacy_ScreenCapture") }

            permissionRow("Microphone", "Only used when “Record microphone” is on.",
                          allowed: micState == .allowed, allowTitle: micState == .denied ? "" : "Allow") {
                AVCaptureDevice.requestAccess(for: .audio) { _ in DispatchQueue.main.async { refresh += 1 } }
            } openSettings: { openPrivacyPane("Privacy_Microphone") }

            Text("macOS only picks up a newly granted Screen Recording permission after Poltergeist restarts.")
                .font(.system(size: 11)).foregroundColor(Theme.muted)
        }
    }

    private func permissionRow(_ title: String, _ sub: String, allowed: Bool, allowTitle: String,
                               request: @escaping () -> Void, openSettings: @escaping () -> Void) -> some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 8) {
                    Text(title).font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
                    Text(allowed ? "Allowed" : "Not allowed")
                        .font(.system(size: 11, weight: .medium))
                        .foregroundColor(allowed ? Color(hex: 0x4ade80) : Theme.warning)
                        .padding(.horizontal, 8).padding(.vertical, 2)
                        .background((allowed ? Color(hex: 0x4ade80) : Theme.warning).opacity(0.12))
                        .clipShape(Capsule())
                }
                Text(sub).font(.system(size: 12)).foregroundColor(Theme.muted)
            }
            Spacer()
            if !allowed {
                if !allowTitle.isEmpty { Button(allowTitle, action: request).buttonStyle(PillButtonStyle()) }
                Button("Open Settings", action: openSettings).buttonStyle(PillButtonStyle(prominent: false))
            }
        }
    }

    // MARK: Audio

    private var audioCard: some View {
        Card {
            Text("Audio").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
            Toggle(isOn: $s.recordSystemAudio) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Record desktop audio").font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
                    Text("Everything your Mac plays, whichever output you're using: speakers, headphones or Bluetooth. Poltergeist's own sounds are left out.")
                        .font(.system(size: 12)).foregroundColor(Theme.muted)
                }
            }.toggleStyle(HauntToggleStyle())

            Toggle(isOn: $s.recordMic) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Record microphone").font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
                    Text("Mixed in with the desktop audio. It pauses and resumes together with the recording.")
                        .font(.system(size: 12)).foregroundColor(Theme.muted)
                }
            }.toggleStyle(HauntToggleStyle())

            HStack {
                Text("Microphone").font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
                Spacer()
                Picker("", selection: $s.micDeviceID) {
                    Text("System default").tag("")
                    if !s.micDeviceID.isEmpty && !mics.contains(where: { $0.id == s.micDeviceID }) {
                        Text("Unavailable device").tag(s.micDeviceID)
                    }
                    ForEach(mics) { Text($0.name).tag($0.id) }
                }
                .labelsHidden().pickerStyle(.menu).fixedSize()
                Button { mics = MicDevice.all() } label: { Image(systemName: "arrow.clockwise") }
                    .buttonStyle(PillButtonStyle(prominent: false))
                    .help("Refresh the list of microphones")
            }
            .disabled(!s.recordMic).opacity(s.recordMic ? 1 : 0.5)
        }
    }

    // MARK: Quality / backup / help

    private var qualityCard: some View {
        Card {
            Text("Quality").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
                pickerRow("Bitrate", "Higher looks sharper but makes bigger files.") {
                    Picker("", selection: $s.recordBitrate) {
                        ForEach(bitrates, id: \.self) { Text("\($0) Mbps").tag($0) }
                    }
                }
                pickerRow("Frame rate", "60 fps is smoother for games and scrolling.") {
                    Picker("", selection: $s.recordFPS) {
                        ForEach(framerates, id: \.self) { Text("\($0) fps").tag($0) }
                    }
                }
                Text("About \(s.recordBitrate * 60 / 8) MB per minute. Haunt's per-file upload limit is 75 MB on Free, 150 MB on Premium and 250 MB on the Image Host plan, so long recordings at a high bitrate may be refused.")
                    .font(.system(size: 12)).foregroundColor(Theme.muted)
        }
    }

    private var trimCard: some View {
        Card {
            Text("Editing").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
            Toggle(isOn: $s.trimBeforeUpload) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Edit recordings before uploading").font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
                    Text("After you stop, an editor opens: trim the ends, cut out sections, and add arrows, boxes, text or blur that can move over time. Plain trimming is instant; anything else re-encodes at your bitrate above.")
                        .font(.system(size: 12)).foregroundColor(Theme.muted)
                }
            }.toggleStyle(HauntToggleStyle())
        }
    }

    private var backupCard: some View {
            Card {
                Text("Backup").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
                Toggle(isOn: $s.keepRecordings) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Keep a copy of recordings").font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
                        Text("Saved to this folder as well as uploaded. Turn it off to delete the local file after uploading.")
                            .font(.system(size: 12)).foregroundColor(Theme.muted)
                    }
                }.toggleStyle(HauntToggleStyle())
                HStack {
                    Text((s.recordFolder as NSString).abbreviatingWithTildeInPath)
                        .font(.system(size: 12)).foregroundColor(s.keepRecordings ? Theme.text : Theme.muted)
                        .lineLimit(1).truncationMode(.middle)
                    Spacer()
                    Button("Choose folder…") { chooseFolder() }.buttonStyle(PillButtonStyle())
                }
            }
    }

    private var helpCard: some View {
            Card {
                Text("How it works").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
                ForEach([
                    "Press a record hotkey (change them in General → Hotkeys). Area: drag over the region. Window: click the window. Full screen: starts straight away on the screen your mouse is on.",
                    "Controls sit under the selection, or above it, or along the bottom of the screen if the area is too big. They are never part of the video.",
                    "Stop saves and uploads. Discard throws the recording away. Press the hotkey again to stop and upload.",
                    "Recordings are MP4 (H.264 video, AAC audio). Desktop audio and the mic are mixed into one track.",
                ], id: \.self) { line in
                    Text("•  \(line)").font(.system(size: 12)).foregroundColor(Theme.muted)
                }
            }
    }

    private func pickerRow<P: View>(_ title: String, _ sub: String, @ViewBuilder picker: () -> P) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
                Text(sub).font(.system(size: 12)).foregroundColor(Theme.muted)
            }
            Spacer()
            picker().labelsHidden().pickerStyle(.menu).fixedSize()
        }
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.prompt = "Choose"
        if panel.runModal() == .OK, let url = panel.url { s.recordFolder = url.path }
    }
}
