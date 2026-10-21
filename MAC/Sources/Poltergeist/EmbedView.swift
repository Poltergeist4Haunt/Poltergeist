import SwiftUI

private let userVars: [(String, String)] = [
    ("{user.id}", "User ID"), ("{user.name}", "Username"), ("{user.uploads}", "Total uploads"),
    ("{user.uploads.today}", "Uploads today"), ("{user.storage}", "Used storage"),
]
private let fileVars: [(String, String)] = [
    ("{file.id}", "File ID"), ("{file.name}", "File name"), ("{file.originalName}", "Original name"),
    ("{file.size}", "File size"), ("{file.uploadAt}", "Upload date"),
    ("{file.url}", "Upload page"), ("{file.rawUrl}", "Direct link"),
]

struct EmbedView: View {
    @State private var cfg = EmbedConfig.load()
    @State private var mode = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Banner(text: "Embed settings are saved on this Mac only. Haunt's public API docs don't expose an embed endpoint yet, so changes here won't appear on shared links until that's wired up.")

            Picker("", selection: $mode) {
                Text("OpenGraph").tag(0)
                Text("Discord").tag(1)
            }
            .pickerStyle(.segmented).labelsHidden()

            Card {
                Text("Available variables").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
                HStack(alignment: .top, spacing: 24) {
                    chips("User", userVars)
                    chips("File", fileVars)
                }
            }

            if mode == 0 { openGraph } else { discord }
        }
        .onChange(of: cfg.authorName) { _ in cfg.save() }
        .onChange(of: cfg.authorURL) { _ in cfg.save() }
        .onChange(of: cfg.providerName) { _ in cfg.save() }
        .onChange(of: cfg.providerURL) { _ in cfg.save() }
        .onChange(of: cfg.title) { _ in cfg.save() }
        .onChange(of: cfg.color) { _ in cfg.save() }
        .onChange(of: cfg.description) { _ in cfg.save() }
        .onChange(of: cfg.showTimestamp) { _ in cfg.save() }
        .onChange(of: cfg.discordText) { _ in cfg.save() }
        .onChange(of: cfg.discordAccent) { _ in cfg.save() }
        .onChange(of: cfg.discordSpoiler) { _ in cfg.save() }
        .onChange(of: cfg.button1Label) { _ in cfg.save() }
        .onChange(of: cfg.button1Link) { _ in cfg.save() }
        .onChange(of: cfg.button2Label) { _ in cfg.save() }
        .onChange(of: cfg.button2Link) { _ in cfg.save() }
    }

    // MARK: OpenGraph

    private var openGraph: some View {
        VStack(alignment: .leading, spacing: 16) {
            Card {
                HStack(spacing: 12) {
                    field("Author name", $cfg.authorName, "Optional")
                    field("Author URL", $cfg.authorURL, "Optional")
                }
                providerRow
                HStack(spacing: 12) {
                    field("Embed title", $cfg.title, "{file.name}")
                    VStack(alignment: .leading, spacing: 6) {
                        FieldLabel("Embed color")
                        HStack(spacing: 8) {
                            Circle().fill(Color(hexString: cfg.color) ?? Theme.accent).frame(width: 28, height: 28)
                            TextField("#f27db3", text: $cfg.color).textFieldStyle(HauntFieldStyle())
                        }
                    }
                }
                VStack(alignment: .leading, spacing: 6) {
                    FieldLabel("Embed description")
                    TextEditor(text: $cfg.description)
                        .font(.system(size: 13)).scrollContentBackground(.hidden)
                        .frame(height: 70).padding(8)
                        .background(Theme.field)
                        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Theme.border))
                        .clipShape(RoundedRectangle(cornerRadius: 14))
                }
                Toggle(isOn: $cfg.showTimestamp) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Show timestamp").font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
                        Text("Append an upload timestamp to the embed.").font(.system(size: 12)).foregroundColor(Theme.muted)
                    }
                }.toggleStyle(HauntToggleStyle())
            }

            Card {
                Text("Preview").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
                HStack(spacing: 0) {
                    Rectangle().fill(Color(hexString: cfg.color) ?? Theme.accent).frame(width: 4)
                    VStack(alignment: .leading, spacing: 6) {
                        if !cfg.authorName.isEmpty { Text(Template.render(cfg.authorName)).font(.system(size: 12, weight: .semibold)) }
                        Text(Template.render(cfg.title)).font(.system(size: 15, weight: .semibold))
                            .foregroundColor(Color(hex: 0x00a8fc))
                        Text(Template.render(cfg.description)).font(.system(size: 13)).foregroundColor(Theme.text.opacity(0.85))
                        RoundedRectangle(cornerRadius: 6).fill(Color.white.opacity(0.06)).frame(height: 120)
                            .overlay(Image(systemName: "photo").foregroundColor(Theme.muted))
                        if cfg.showTimestamp {
                            Text(Template.sample["file.uploadAt"] ?? "").font(.system(size: 11)).foregroundColor(Theme.muted)
                        }
                    }
                    .foregroundColor(Theme.text)
                    .padding(12)
                    Spacer(minLength: 0)
                }
                .background(Color(hex: 0x2b2d31))
                .clipShape(RoundedRectangle(cornerRadius: 6))
            }
        }
    }

    // MARK: Discord

    private var discord: some View {
        VStack(alignment: .leading, spacing: 16) {
            Card {
                Toggle(isOn: $cfg.discordAccent) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Accent color").font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
                        Text("A colored bar along the side of the embed.").font(.system(size: 12)).foregroundColor(Theme.muted)
                    }
                }
                if cfg.discordAccent {
                    HStack(spacing: 8) {
                        Circle().fill(Color(hexString: cfg.color) ?? Theme.accent).frame(width: 28, height: 28)
                        TextField("#f27db3", text: $cfg.color).textFieldStyle(HauntFieldStyle())
                    }
                }
                Toggle(isOn: $cfg.discordSpoiler) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Hide behind a spoiler").font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
                        Text("Readers have to click the embed to reveal it.").font(.system(size: 12)).foregroundColor(Theme.muted)
                    }
                }
            }.toggleStyle(HauntToggleStyle())

            Card {
                FieldLabel("Text block")
                TextEditor(text: $cfg.discordText)
                    .font(.system(size: 13, design: .monospaced)).scrollContentBackground(.hidden)
                    .frame(height: 80).padding(8)
                    .background(Theme.field)
                    .overlay(RoundedRectangle(cornerRadius: 14).stroke(Theme.border))
                    .clipShape(RoundedRectangle(cornerRadius: 14))
                Text("Discord markdown works here: headings, **bold**, *italic*, lists, links, code and -# small text.")
                    .font(.system(size: 12)).foregroundColor(Theme.muted)
            }

            Card {
                FieldLabel("Link buttons")
                HStack(spacing: 12) {
                    field("Button 1 label", $cfg.button1Label, "Open")
                    field("Button 1 link", $cfg.button1Link, "{file.url}")
                }
                HStack(spacing: 12) {
                    field("Button 2 label", $cfg.button2Label, "Original file")
                    field("Button 2 link", $cfg.button2Link, "{file.rawUrl}")
                }
            }

            Card {
                Text("Preview").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
                HStack(spacing: 0) {
                    if cfg.discordAccent { Rectangle().fill(Color(hexString: cfg.color) ?? Theme.accent).frame(width: 4) }
                    VStack(alignment: .leading, spacing: 8) {
                        Text(Template.render(cfg.discordText)).font(.system(size: 13))
                        RoundedRectangle(cornerRadius: 6).fill(Color.white.opacity(0.06)).frame(height: 120)
                            .overlay(Image(systemName: cfg.discordSpoiler ? "eye.slash" : "photo").foregroundColor(Theme.muted))
                        HStack {
                            pill(cfg.button1Label); pill(cfg.button2Label)
                        }
                    }
                    .foregroundColor(Theme.text)
                    .padding(12)
                    Spacer(minLength: 0)
                }
                .background(Color(hex: 0x2b2d31))
                .clipShape(RoundedRectangle(cornerRadius: 6))
            }
        }
    }

    // MARK: Provider

    private static let providers: [(name: String, url: String)] = [
        ("Image Host", "https://i.haunt.gg"),
        ("Haunt", "https://haunt.gg"),
    ]
    private static let customTag = "Custom"

    private var providerSelection: Binding<String> {
        Binding(
            get: { Self.providers.contains { $0.name == cfg.providerName } ? cfg.providerName : Self.customTag },
            set: { choice in
                if let p = Self.providers.first(where: { $0.name == choice }) {
                    cfg.providerName = p.name
                    cfg.providerURL = p.url
                } else {
                    cfg.providerName = ""
                    cfg.providerURL = ""
                }
            }
        )
    }

    private var providerRow: some View {
        HStack(alignment: .top, spacing: 12) {
            VStack(alignment: .leading, spacing: 6) {
                FieldLabel("Provider")
                Picker("", selection: providerSelection) {
                    ForEach(Self.providers, id: \.name) { Text($0.name).tag($0.name) }
                    Divider()
                    Text(Self.customTag).tag(Self.customTag)
                }
                .labelsHidden()
                .frame(maxWidth: .infinity, alignment: .leading)
                if providerSelection.wrappedValue == Self.customTag {
                    TextField("Provider name", text: $cfg.providerName).textFieldStyle(HauntFieldStyle())
                }
            }
            VStack(alignment: .leading, spacing: 6) {
                FieldLabel("Provider URL")
                TextField("https://…", text: $cfg.providerURL)
                    .textFieldStyle(HauntFieldStyle())
                    .disabled(providerSelection.wrappedValue != Self.customTag)
                    .opacity(providerSelection.wrappedValue != Self.customTag ? 0.6 : 1)
            }
        }
    }

    // MARK: Pieces

    private func pill(_ text: String) -> some View {
        Text(text + " ↗").font(.system(size: 12, weight: .medium))
            .padding(.horizontal, 10).padding(.vertical, 5)
            .background(Color.white.opacity(0.1)).clipShape(RoundedRectangle(cornerRadius: 6))
    }

    private func field(_ label: String, _ binding: Binding<String>, _ placeholder: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            FieldLabel(label)
            TextField(placeholder, text: binding).textFieldStyle(HauntFieldStyle())
        }
    }

    private func chips(_ title: String, _ vars: [(String, String)]) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.system(size: 12)).foregroundColor(Theme.muted)
            ForEach(vars, id: \.0) { v in
                HStack(spacing: 8) {
                    Text(v.0).font(.system(size: 12, design: .monospaced)).foregroundColor(Theme.text)
                        .padding(.horizontal, 7).padding(.vertical, 2)
                        .background(Color.white.opacity(0.08)).clipShape(RoundedRectangle(cornerRadius: 5))
                    Text(v.1).font(.system(size: 12)).foregroundColor(Theme.muted)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}
