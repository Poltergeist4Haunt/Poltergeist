import AppKit
import SwiftUI
import QuickLookThumbnailing

private final class ToastPanel: NSPanel {
    override var canBecomeKey: Bool { true }
}

/// Small preview that slides in at the bottom-right after an upload.
final class ToastController {
    static let shared = ToastController()
    private var panel: NSPanel?
    private var timer: Timer?

    static func thumbnail(for url: URL) async -> NSImage? {
        let req = QLThumbnailGenerator.Request(fileAt: url, size: CGSize(width: 120, height: 120),
                                               scale: 2, representationTypes: .thumbnail)
        return try? await QLThumbnailGenerator.shared.generateBestRepresentation(for: req).nsImage
    }

    func show(name: String, copyText: String, thumbnail: NSImage?) {
        dismiss(animated: false)
        let view = ToastView(
            name: name, thumbnail: thumbnail,
            onCopy: { [weak self] in UploadManager.copy(copyText); self?.dismiss() },
            onClose: { [weak self] in self?.dismiss() },
            onHover: { [weak self] inside in inside ? self?.timer?.invalidate() : self?.startTimer(2) }
        )
        let host = NSHostingView(rootView: view)
        let size = NSSize(width: 340, height: 92)
        host.frame = NSRect(origin: .zero, size: size)

        let p = ToastPanel(contentRect: host.frame, styleMask: [.borderless, .nonactivatingPanel],
                           backing: .buffered, defer: false)
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = true
        p.level = .statusBar
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        p.isReleasedWhenClosed = false
        p.contentView = host
        if let f = NSScreen.main?.visibleFrame {
            p.setFrameOrigin(NSPoint(x: f.maxX - size.width - 16, y: f.minY + 16))
        }
        p.alphaValue = 0
        p.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.2
            p.animator().alphaValue = 1
        }
        panel = p
        startTimer(6)
    }

    private func startTimer(_ seconds: TimeInterval) {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: seconds, repeats: false) { [weak self] _ in self?.dismiss() }
    }

    func dismiss(animated: Bool = true) {
        timer?.invalidate()
        guard let p = panel else { return }
        panel = nil
        guard animated else { p.orderOut(nil); return }
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.45
            ctx.timingFunction = CAMediaTimingFunction(name: .easeIn)
            p.animator().alphaValue = 0
        }, completionHandler: { p.orderOut(nil) })
    }
}

private struct ToastView: View {
    let name: String
    let thumbnail: NSImage?
    let onCopy: () -> Void
    let onClose: () -> Void
    let onHover: (Bool) -> Void

    var body: some View {
        HStack(spacing: 12) {
            Group {
                if let thumbnail { Image(nsImage: thumbnail).resizable().scaledToFill() }
                else { Image(systemName: "photo").foregroundColor(Theme.muted) }
            }
            .frame(width: 64, height: 64)
            .background(Color.white.opacity(0.06))
            .clipShape(RoundedRectangle(cornerRadius: 10))

            VStack(alignment: .leading, spacing: 8) {
                Text("Uploaded").font(.system(size: 11, weight: .medium)).foregroundColor(Theme.accent)
                Text(name).font(.system(size: 13, weight: .medium)).foregroundColor(Theme.text).lineLimit(1)
                Button("Copy link", action: onCopy).buttonStyle(PillButtonStyle())
            }
            Spacer(minLength: 0)
            Button(action: onClose) {
                Image(systemName: "xmark").font(.system(size: 11, weight: .bold)).foregroundColor(Theme.muted)
                    .frame(width: 24, height: 24).contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .frame(maxHeight: .infinity, alignment: .top)
        }
        .padding(14)
        .frame(width: 340, height: 92)
        .background(Theme.card)
        .overlay(RoundedRectangle(cornerRadius: 18).stroke(Theme.border))
        .clipShape(RoundedRectangle(cornerRadius: 18))
        .onHover(perform: onHover)
        .preferredColorScheme(.dark)
    }
}
