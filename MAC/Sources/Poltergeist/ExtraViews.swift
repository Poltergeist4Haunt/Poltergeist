import SwiftUI
import AVKit
import AVFoundation
import AppKit
import UniformTypeIdentifiers

// MARK: - Sound picker (system sounds, or any file you choose)

struct SoundPicker: View {
    @Binding var value: String
    private let chooseTag = "\u{1}choose"

    var body: some View {
        Picker("", selection: Binding(get: { value }, set: { new in
            if new == chooseTag { chooseFile(); return }
            value = new
            Settings.shared.play(new)
        })) {
            Text("None").tag(Sounds.none)
            Divider()
            ForEach(Sounds.names, id: \.self) { Text($0).tag($0) }
            if Sounds.isCustom(value) {
                Divider()
                Text("Your file: " + (value as NSString).lastPathComponent).tag(value)
            }
            Divider()
            Text("Choose a file…").tag(chooseTag)
        }
        .labelsHidden().pickerStyle(.menu).fixedSize()
    }

    private func chooseFile() {
        DispatchQueue.main.async {
            let panel = NSOpenPanel()
            panel.canChooseDirectories = false
            panel.allowedContentTypes = [.audio]
            panel.prompt = "Use sound"
            if panel.runModal() == .OK, let url = panel.url {
                value = url.path
                Settings.shared.play(url.path)
            }
        }
    }
}

// MARK: - Thumbnails for the gallery

@MainActor
enum Thumbs {
    private static var cache: [String: NSImage] = [:]
    private static let session: URLSession = {
        let c = URLSessionConfiguration.ephemeral
        c.httpAdditionalHeaders = [
            "User-Agent": "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15",
            "Accept": "image/avif,image/webp,image/png,image/*,*/*;q=0.8",
        ]
        c.timeoutIntervalForRequest = 20
        return URLSession(configuration: c)
    }()

    /// A picture, or one frame of a video. `small` keeps grid thumbnails light.
    static func load(_ urlString: String, video: Bool, small: Bool = true) async -> NSImage? {
        let key = urlString + (small ? "#s" : "#l")
        if let hit = cache[key] { return hit }
        guard let url = URL(string: urlString) else { return nil }
        var result: NSImage?
        if video {
            let gen = AVAssetImageGenerator(asset: AVURLAsset(url: url))
            gen.appliesPreferredTrackTransform = true
            gen.maximumSize = CGSize(width: small ? 360 : 1280, height: small ? 360 : 1280)
            if let cg = try? await gen.image(at: CMTime(seconds: 1, preferredTimescale: 600)).image {
                result = NSImage(cgImage: cg, size: .zero)
            }
        } else if let (data, _) = try? await session.data(from: url) {
            result = NSImage(data: data)
        }
        if let result { cache[key] = result }
        return result
    }
}

private struct RemoteThumb: View {
    let url: String
    let video: Bool
    var small = true
    @State private var image: NSImage?

    var body: some View {
        ZStack {
            Color.white.opacity(0.06)
            if let image {
                Image(nsImage: image).resizable().scaledToFill()
            } else {
                Image(systemName: video ? "video" : "photo").font(.system(size: 24)).foregroundColor(Theme.muted)
            }
        }
        .task(id: url) { image = await Thumbs.load(url, video: video, small: small) }
    }
}

// MARK: - Gallery

private struct GalleryEntry: Identifiable {
    let id: String
    let name: String
    let url: String            // the link people share
    let direct: String         // the file itself, with an extension
    let thumb: String
    let date: Date?
    let video: Bool
}

struct GalleryView: View {
    @ObservedObject private var settings = Settings.shared
    @ObservedObject private var history = HistoryStore.shared
    @State private var mode = "haunt"
    @State private var remote: [RemoteUpload]?
    @State private var error: String?
    @State private var loading = false
    @State private var preview: GalleryEntry?

    private func isVideoName(_ n: String) -> Bool { ["mp4", "webm", "mov", "mkv"].contains((n as NSString).pathExtension.lowercased()) }

    private var entries: [GalleryEntry] {
        if mode == "pc" {
            return history.items.map {
                let direct = LinkFormat.direct.format(url: $0.url, name: $0.name)
                return GalleryEntry(id: $0.id.uuidString, name: $0.name, url: $0.url, direct: direct, thumb: direct, date: $0.date, video: isVideoName($0.name))
            }
        }
        return (remote ?? []).map {
            let direct = LinkFormat.direct.format(url: $0.url, name: $0.name)
            return GalleryEntry(id: $0.id, name: $0.name, url: $0.url, direct: direct, thumb: $0.thumbURL ?? direct, date: $0.created, video: $0.isVideo)
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 6) {
                Button("Haunt gallery") { mode = "haunt" }.buttonStyle(TabButtonStyle(selected: mode == "haunt"))
                Button("This Mac") { mode = "pc" }.buttonStyle(TabButtonStyle(selected: mode == "pc"))
                Spacer()
                if mode == "haunt" {
                    Button("Refresh") { remote = nil; error = nil; Task { await load() } }.buttonStyle(PillButtonStyle(prominent: false))
                } else if !history.items.isEmpty {
                    Button("Clear list") { history.clear() }.buttonStyle(PillButtonStyle(prominent: false))
                }
            }
            content
        }
        .task { if mode == "haunt" && remote == nil && error == nil { await load() } }
        .onChange(of: mode) { m in if m == "haunt" && remote == nil && error == nil { Task { await load() } } }
        .sheet(item: $preview) { PreviewSheet(entry: $0) }
    }

    @ViewBuilder private var content: some View {
        if mode == "haunt" && settings.apiKey.isEmpty {
            Banner(text: "Add your API key in the Account tab to see your Haunt gallery.")
        } else if mode == "haunt" && loading && remote == nil {
            Text("Loading your gallery…").font(.system(size: 13)).foregroundColor(Theme.muted)
        } else if mode == "haunt", let error {
            Banner(text: error)
            Button("Try again") { self.error = nil; Task { await load() } }.buttonStyle(PillButtonStyle())
        } else if entries.isEmpty {
            Card {
                Text(mode == "haunt" ? "Nothing on your image host yet." : "Nothing here yet").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
                Text("Everything you upload shows up here with a preview.").font(.system(size: 12)).foregroundColor(Theme.muted)
            }
        } else {
            Text("\(entries.count) file\(entries.count == 1 ? "" : "s")" + (mode == "haunt" ? " on your image host" : " uploaded from this Mac"))
                .font(.system(size: 13)).foregroundColor(Theme.muted)
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 176), spacing: 12, alignment: .top)], spacing: 12) {
                ForEach(entries) { tile($0) }
            }
        }
    }

    private func tile(_ e: GalleryEntry) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            RemoteThumb(url: e.thumb, video: e.video)
                .frame(height: 112).frame(maxWidth: .infinity)
                .clipShape(RoundedRectangle(cornerRadius: 10))
                .overlay(alignment: .center) {
                    if e.video { Image(systemName: "play.circle.fill").font(.system(size: 26)).foregroundColor(.white.opacity(0.85)).shadow(radius: 4) }
                }
                .contentShape(Rectangle())
                .onTapGesture { preview = e }
                .help("Click for a bigger preview")
            Text(e.name).font(.system(size: 12, weight: .medium)).foregroundColor(Theme.text).lineLimit(1).truncationMode(.middle)
            Text(e.date.map { $0.formatted(date: .abbreviated, time: .shortened) } ?? " ").font(.system(size: 11)).foregroundColor(Theme.muted)
            HStack(spacing: 6) {
                Button("Copy link") { UploadManager.copy(settings.linkFormat.format(url: e.url, name: e.name)) }.buttonStyle(PillButtonStyle())
                Button("Open") { if let u = URL(string: e.url) { NSWorkspace.shared.open(u) } }.buttonStyle(PillButtonStyle(prominent: false))
            }
            .font(.system(size: 11))
        }
        .padding(10)
        .background(Theme.card)
        .overlay(RoundedRectangle(cornerRadius: 16).stroke(Theme.border))
        .clipShape(RoundedRectangle(cornerRadius: 16))
    }

    private func load() async {
        guard !loading, !settings.apiKey.isEmpty else { return }
        loading = true
        defer { loading = false }
        do { remote = try await HauntAPI.listUploads() }
        catch { self.error = error.localizedDescription }
    }
}

/// A bigger look at one picture or video.
private struct PreviewSheet: View {
    let entry: GalleryEntry
    @Environment(\.dismiss) private var dismiss
    @State private var image: NSImage?
    @State private var player: AVPlayer?

    var body: some View {
        VStack(spacing: 0) {
            ZStack {
                Color.black
                if entry.video {
                    if let player { VideoPlayer(player: player) } else { ProgressView() }
                } else if let image {
                    Image(nsImage: image).resizable().scaledToFit().padding(12)
                } else { ProgressView() }
            }
            HStack(spacing: 10) {
                Text(entry.name).font(.system(size: 13, weight: .medium)).foregroundColor(Theme.text).lineLimit(1)
                Spacer()
                Button("Copy link") { UploadManager.copy(Settings.shared.linkFormat.format(url: entry.url, name: entry.name)) }.buttonStyle(PillButtonStyle())
                Button("Open in browser") { if let u = URL(string: entry.url) { NSWorkspace.shared.open(u) } }.buttonStyle(PillButtonStyle(prominent: false))
                Button("Close") { dismiss() }.buttonStyle(PillButtonStyle(prominent: false)).keyboardShortcut(.cancelAction)
            }
            .padding(14).background(Theme.card)
        }
        .frame(minWidth: 760, minHeight: 540)
        .task {
            if entry.video, let u = URL(string: entry.direct) { let p = AVPlayer(url: u); player = p; p.play() }
            else { image = await Thumbs.load(entry.direct, video: false, small: false) }
        }
        .onDisappear { player?.pause() }
        .preferredColorScheme(.dark)
    }
}

// MARK: - Effects

struct EffectsView: View {
    @ObservedObject private var s = Settings.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Card {
                Text("Image effects").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
                Text("Touch-ups added to every screenshot you take, before it's edited or uploaded.").font(.system(size: 12)).foregroundColor(Theme.muted)
                Toggle(isOn: $s.fxOn) { Text("Use image effects").font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text) }
            }.toggleStyle(HauntToggleStyle())

            Card {
                Toggle(isOn: $s.fxRound) { Text("Rounded corners").font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text) }
                slider("Corner size", value: $s.fxRadius, range: 4...60)
                slider("Border", value: $s.fxBorder, range: 0...16, note: "0 for none")
                HStack {
                    Text("Border colour").font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
                    Spacer()
                    Circle().fill(Color(hexString: s.fxBorderColor) ?? Theme.accent).frame(width: 24, height: 24)
                    TextField("#f27db3", text: $s.fxBorderColor).textFieldStyle(HauntFieldStyle()).frame(width: 130)
                }
                Toggle(isOn: $s.fxShadow) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Drop shadow").font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
                        Text("Adds a soft transparent margin around the shot.").font(.system(size: 12)).foregroundColor(Theme.muted)
                    }
                }
                slider("Shadow size", value: $s.fxShadowSize, range: 6...50)
            }.toggleStyle(HauntToggleStyle())

            Card {
                Text("Preview").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
                HStack {
                    Spacer()
                    Image(nsImage: preview).resizable().scaledToFit().frame(maxHeight: 220)
                    Spacer()
                }
                .padding(18).background(Color(hex: 0x141416)).clipShape(RoundedRectangle(cornerRadius: 12))
            }
        }
    }

    private func slider(_ title: String, value: Binding<Int>, range: ClosedRange<Int>, note: String? = nil) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
                if let note { Text(note).font(.system(size: 12)).foregroundColor(Theme.muted) }
            }
            Spacer()
            Slider(value: Binding(get: { Double(value.wrappedValue) }, set: { value.wrappedValue = Int($0.rounded()) }),
                   in: Double(range.lowerBound)...Double(range.upperBound)).frame(width: 170)
            Text("\(value.wrappedValue) px").font(.system(size: 12, design: .monospaced)).foregroundColor(Theme.muted).frame(width: 52, alignment: .trailing)
        }
    }

    /// A made-up screenshot with the current effects on it.
    private var preview: NSImage {
        let w = 420, h = 240
        let base = NSImage(size: NSSize(width: w, height: h), flipped: false) { r in
            NSGradient(colors: [NSColor(hex: 0x282438), NSColor(hex: 0x121622)])?.draw(in: r, angle: -35)
            NSColor.white.withAlphaComponent(0.09).setFill()
            for rect in [NSRect(x: 24, y: 120, width: 180, height: 96), NSRect(x: 222, y: 172, width: 174, height: 44),
                         NSRect(x: 222, y: 24, width: 174, height: 136), NSRect(x: 24, y: 24, width: 180, height: 82)] {
                NSBezierPath(roundedRect: rect, xRadius: 10, yRadius: 10).fill()
            }
            NSColor(hex: 0xf27db3).setFill()
            NSBezierPath(ovalIn: NSRect(x: 44, y: 150, width: 32, height: 32)).fill()
            return true
        }
        guard let cg = base.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return base }
        if s.fxOn, let out = ImageEffects.apply(to: cg) { return NSImage(cgImage: out, size: NSSize(width: out.width, height: out.height)) }
        return base
    }
}

// MARK: - Stats

struct StatsView: View {
    @ObservedObject private var s = Settings.shared

    private var time: String {
        let t = Int(s.totalSeconds)
        return t >= 3600 ? "\(t / 3600)h \((t % 3600) / 60)m" : "\(t / 60)m"
    }
    private var data: String {
        s.bytesUploaded >= 1_000_000_000 ? String(format: "%.1f GB", Double(s.bytesUploaded) / 1e9) : String(format: "%.1f MB", Double(s.bytesUploaded) / 1e6)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 190), spacing: 12)], spacing: 12) {
                stat("Time with Poltergeist open", time)
                stat("Uploads", "\(s.totalUploads)")
                stat("Screenshots taken", "\(s.totalScreenshots)")
                stat("Recordings", "\(s.totalRecordings)")
                stat("Data uploaded", data)
                stat("Using it since", s.firstRun.formatted(date: .abbreviated, time: .omitted))
            }
            Text("Counted on this Mac only. Time adds up while Poltergeist runs in the menu bar.")
                .font(.system(size: 12)).foregroundColor(Theme.muted)
        }
    }

    private func stat(_ label: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(value).font(.system(size: 26, weight: .semibold)).foregroundColor(Theme.text)
            Text(label).font(.system(size: 12.5)).foregroundColor(Theme.muted)
        }
        .padding(20).frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.card)
        .overlay(RoundedRectangle(cornerRadius: 18).stroke(Theme.border))
        .clipShape(RoundedRectangle(cornerRadius: 18))
    }
}
