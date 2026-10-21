import SwiftUI

extension Color {
    init(hex: UInt32, opacity: Double = 1) {
        self.init(.sRGB,
                  red: Double((hex >> 16) & 0xFF) / 255,
                  green: Double((hex >> 8) & 0xFF) / 255,
                  blue: Double(hex & 0xFF) / 255,
                  opacity: opacity)
    }

    init?(hexString: String) {
        var s = hexString.trimmingCharacters(in: .whitespaces)
        if s.hasPrefix("#") { s.removeFirst() }
        guard s.count == 6, let v = UInt32(s, radix: 16) else { return nil }
        self.init(hex: v)
    }
}

/// Haunt's dark palette.
enum Theme {
    static let background = Color(hex: 0x090909)
    static let card = Color(hex: 0x0f0f0f)
    static let field = Color(hex: 0x0b0b0b)
    static let border = Color.white.opacity(0.08)
    static let accent = Color(hex: 0xf27db3)
    static let accentSoft = Color(hex: 0xf27db3, opacity: 0.14)
    static let text = Color(hex: 0xf2f2f2)
    static let muted = Color(hex: 0x8a8a8a)
    static let warning = Color(hex: 0xf5a623)
}

struct Card<Content: View>: View {
    @ViewBuilder var content: Content
    var body: some View {
        VStack(alignment: .leading, spacing: 12) { content }
            .padding(18)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Theme.card)
            .overlay(RoundedRectangle(cornerRadius: 18).stroke(Theme.border))
            .clipShape(RoundedRectangle(cornerRadius: 18))
    }
}

struct FieldLabel: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        Text(text).font(.system(size: 13, weight: .medium)).foregroundColor(Theme.text)
    }
}

struct HauntFieldStyle: TextFieldStyle {
    func _body(configuration: TextField<Self._Label>) -> some View {
        configuration
            .textFieldStyle(.plain)
            .font(.system(size: 13))
            .foregroundColor(Theme.text)
            .padding(.horizontal, 14).padding(.vertical, 10)
            .background(Theme.field)
            .overlay(Capsule().stroke(Theme.border))
            .clipShape(Capsule())
    }
}

struct HauntToggleStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack {
            configuration.label
            Spacer()
            ZStack(alignment: configuration.isOn ? .trailing : .leading) {
                Capsule().fill(configuration.isOn ? Theme.accent : Color.white.opacity(0.12))
                    .frame(width: 42, height: 24)
                Circle().fill(configuration.isOn ? Color.black : Color.white)
                    .frame(width: 18, height: 18).padding(3)
            }
        }
        // The whole row is the hit target, not just the small switch.
        .contentShape(Rectangle())
        .onTapGesture { withAnimation(.easeOut(duration: 0.1)) { configuration.isOn.toggle() } }
        .accessibilityAddTraits(.isButton)
    }
}

struct PillButtonStyle: ButtonStyle {
    var prominent = true
    func makeBody(configuration: Configuration) -> some View {
        PillBody(configuration: configuration, prominent: prominent)
    }

    private struct PillBody: View {
        let configuration: Configuration
        let prominent: Bool
        @State private var hovering = false

        var body: some View {
            configuration.label
                .lineLimit(1)
                .fixedSize(horizontal: true, vertical: false)
                .font(.system(size: 13, weight: .medium))
                .foregroundColor(prominent ? Theme.accent : Theme.text)
                .padding(.horizontal, 16).padding(.vertical, 8)
                .background(prominent ? Theme.accentSoft : Color.white.opacity(0.05))
                .background(Color.white.opacity(hovering ? 0.06 : 0))
                .overlay(Capsule().stroke(prominent ? Theme.accent.opacity(hovering ? 0.7 : 0.4) : Theme.border))
                .clipShape(Capsule())
                .contentShape(Capsule())
                .scaleEffect(configuration.isPressed ? 0.96 : 1)
                .animation(.easeOut(duration: 0.08), value: configuration.isPressed)
                .onHover { hovering = $0 }
        }
    }
}

struct TabButtonStyle: ButtonStyle {
    let selected: Bool
    func makeBody(configuration: Configuration) -> some View {
        TabBody(configuration: configuration, selected: selected)
    }

    private struct TabBody: View {
        let configuration: Configuration
        let selected: Bool
        @State private var hovering = false

        var body: some View {
            configuration.label
                .lineLimit(1)
                .fixedSize(horizontal: true, vertical: false)
                .font(.system(size: 13, weight: .medium))
                .foregroundColor(selected ? Theme.accent : (hovering ? Theme.text : Theme.muted))
                .padding(.horizontal, 14).padding(.vertical, 8)
                .background(selected ? Theme.accentSoft : Color.white.opacity(hovering ? 0.05 : 0))
                .overlay(Capsule().stroke(selected ? Theme.accent.opacity(0.4) : Color.clear))
                .clipShape(Capsule())
                .contentShape(Capsule())
                .scaleEffect(configuration.isPressed ? 0.96 : 1)
                .animation(.easeOut(duration: 0.08), value: configuration.isPressed)
                .onHover { hovering = $0 }
        }
    }
}

struct Banner: View {
    let text: String
    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundColor(Theme.warning)
            Text(text).font(.system(size: 12)).foregroundColor(Theme.warning)
            Spacer(minLength: 0)
        }
        .padding(12)
        .background(Theme.warning.opacity(0.08))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(Theme.warning.opacity(0.35)))
        .clipShape(RoundedRectangle(cornerRadius: 12))
    }
}

/// A pill with a main action and a ▾ menu of alternatives, e.g. "Upload" with "Copy to clipboard" behind the arrow.
struct SplitActionButton: View {
    struct Choice: Identifiable {
        let id = UUID()
        let title: String
        let icon: String
        let action: () -> Void
        var shortcut: KeyboardShortcut? = nil
    }

    let title: String
    let primary: () -> Void
    /// Shown only on the main button, so the menu items don't inherit it.
    var primaryShortcut: KeyboardShortcut? = nil
    let choices: [Choice]
    @State private var hovering = false

    @ViewBuilder private var primaryButton: some View {
        let button = Button(action: primary) {
            Text(title).lineLimit(1).fixedSize()
                .padding(.leading, 16).padding(.trailing, 12).padding(.vertical, 8)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        if let primaryShortcut { button.keyboardShortcut(primaryShortcut) } else { button }
    }

    var body: some View {
        HStack(spacing: 0) {
            primaryButton

            Rectangle().fill(Theme.accent.opacity(0.4)).frame(width: 1, height: 20)

            Menu {
                ForEach(choices) { c in
                    if let shortcut = c.shortcut {
                        Button { c.action() } label: { Label(c.title, systemImage: c.icon) }.keyboardShortcut(shortcut)
                    } else {
                        Button { c.action() } label: { Label(c.title, systemImage: c.icon) }
                    }
                }
            } label: {
                Image(systemName: "chevron.down").font(.system(size: 10, weight: .bold))
                    .padding(.horizontal, 11).padding(.vertical, 11)
                    .contentShape(Rectangle())
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .help("More ways to finish")
        }
        .font(.system(size: 13, weight: .medium))
        .foregroundColor(Theme.accent)
        .background(Theme.accentSoft)
        .background(Color.white.opacity(hovering ? 0.06 : 0))
        .overlay(Capsule().stroke(Theme.accent.opacity(hovering ? 0.7 : 0.4)))
        .clipShape(Capsule())
        .onHover { hovering = $0 }
    }
}
