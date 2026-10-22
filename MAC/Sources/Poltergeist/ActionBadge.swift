import AppKit
import SwiftUI

/// A small pill near the top of the screen saying what Poltergeist is doing right now ("Taking a screenshot of an area",
/// "Recording full screen"), with an icon, so a wrong hotkey is noticed straight away.
final class ActionBadge {
    static let shared = ActionBadge()
    private var panel: NSPanel?
    private var token = 0

    func show(symbol: String, text: String, seconds: Double = 1.6, recording: Bool = false) {
        dismiss(animated: false)
        let host = NSHostingView(rootView: BadgeView(symbol: symbol, text: text, recording: recording))
        host.layoutSubtreeIfNeeded()
        let size = host.fittingSize
        host.frame = NSRect(origin: .zero, size: size)
        let p = NSPanel(contentRect: host.frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = false
        p.ignoresMouseEvents = true
        p.sharingType = .none                                    // never ends up in the screenshot or recording
        p.level = .statusBar
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient]
        p.isReleasedWhenClosed = false
        p.contentView = host
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { $0.frame.contains(mouse) } ?? NSScreen.main
        if let f = screen?.visibleFrame { p.setFrameOrigin(NSPoint(x: f.midX - size.width / 2, y: f.maxY - size.height - 6)) }
        p.alphaValue = 0
        p.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { $0.duration = 0.16; p.animator().alphaValue = 1 }
        panel = p
        token += 1
        let mine = token
        if seconds > 0 {
            DispatchQueue.main.asyncAfter(deadline: .now() + seconds) { [weak self] in if self?.token == mine { self?.dismiss(animated: true) } }
        }
    }

    func dismiss(animated: Bool) {
        guard let p = panel else { return }
        panel = nil
        guard animated else { p.orderOut(nil); return }
        NSAnimationContext.runAnimationGroup({ $0.duration = 0.26; p.animator().alphaValue = 0 }, completionHandler: { p.orderOut(nil) })
    }
}

private struct BadgeView: View {
    let symbol: String
    let text: String
    let recording: Bool

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: symbol)
                .font(.system(size: 13, weight: .semibold))
                .foregroundColor(recording ? Color(hex: 0xff453a) : Theme.accent)
                .frame(width: 30, height: 30)
                .background((recording ? Color(hex: 0xff453a) : Theme.accent).opacity(0.18))
                .clipShape(Circle())
            Text(text).font(.system(size: 13.5, weight: .medium)).foregroundColor(Theme.text)
        }
        .padding(.leading, 8).padding(.trailing, 18).padding(.vertical, 8)
        .background(Theme.card)
        .overlay(RoundedRectangle(cornerRadius: 24).stroke(Color.white.opacity(0.14)))
        .clipShape(RoundedRectangle(cornerRadius: 24))
        .padding(10)
    }
}

/// The hint shown in the screen-selection overlays; set just before one opens.
enum ActionHint {
    static var text = ""
}
