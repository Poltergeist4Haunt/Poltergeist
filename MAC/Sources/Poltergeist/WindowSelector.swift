import AppKit

struct PickedWindow {
    let id: CGWindowID
    let frame: NSRect        // Cocoa screen coordinates (origin bottom-left)
    let name: String
}

private final class PickOverlayWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

private final class PickView: NSView {
    weak var owner: WindowSelector?
    let screen: NSScreen

    init(screen: NSScreen) {
        self.screen = screen
        super.init(frame: NSRect(origin: .zero, size: screen.frame.size))
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                                       owner: self, userInfo: nil))
    }
    required init?(coder: NSCoder) { fatalError() }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseMoved(with event: NSEvent) { owner?.update() }
    override func mouseEntered(with event: NSEvent) { owner?.update() }
    override func mouseDown(with event: NSEvent) { owner?.update(); owner?.confirm() }
    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { owner?.cancel() } else { super.keyDown(with: event) }
    }

    override func draw(_ dirtyRect: NSRect) {
        // Nearly invisible, but not fully transparent, so the overlay still receives the mouse.
        NSColor.black.withAlphaComponent(0.02).setFill()
        bounds.fill()

        guard let w = owner?.hovered else {
            label("Click a window to record it  ·  Esc to cancel", at: NSPoint(x: bounds.midX, y: bounds.midY))
            return
        }
        let r = w.frame.offsetBy(dx: -screen.frame.minX, dy: -screen.frame.minY)
        guard r.intersects(bounds) else { return }
        NSColor(Theme.accent).withAlphaComponent(0.22).setFill()
        r.fill()
        NSColor(Theme.accent).setStroke()
        let border = NSBezierPath(rect: r.insetBy(dx: 1.5, dy: 1.5))
        border.lineWidth = 3
        border.stroke()
        label(w.name, at: NSPoint(x: r.midX, y: r.midY))
    }

    private func label(_ text: String, at p: NSPoint) {
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 13, weight: .medium), .foregroundColor: NSColor.white,
        ]
        let str = NSAttributedString(string: text, attributes: attrs)
        let size = str.size()
        let box = NSRect(x: p.x - size.width / 2 - 10, y: p.y - size.height / 2 - 5, width: size.width + 20, height: size.height + 10)
        NSColor.black.withAlphaComponent(0.75).setFill()
        NSBezierPath(roundedRect: box, xRadius: 9, yRadius: 9).fill()
        str.draw(at: NSPoint(x: box.minX + 10, y: box.minY + 5))
    }
}

/// "Click a window" picker: highlights the window under the cursor on every display.
final class WindowSelector {
    static var current: WindowSelector?
    fileprivate var hovered: PickedWindow?
    private var windows: [NSWindow] = []
    private var views: [PickView] = []
    private var completion: ((PickedWindow?, NSScreen?) -> Void)?

    static func select(_ completion: @escaping (PickedWindow?, NSScreen?) -> Void) {
        let s = WindowSelector()
        current = s
        s.begin(completion)
    }

    func cancel() { finish(nil) }

    fileprivate func confirm() { if let hovered { finish(hovered) } }

    private func begin(_ completion: @escaping (PickedWindow?, NSScreen?) -> Void) {
        self.completion = completion
        for screen in NSScreen.screens {
            let w = PickOverlayWindow(contentRect: screen.frame, styleMask: .borderless, backing: .buffered, defer: false, screen: screen)
            w.isOpaque = false
            w.backgroundColor = .clear
            w.level = .screenSaver
            w.hasShadow = false
            w.isReleasedWhenClosed = false
            w.acceptsMouseMovedEvents = true
            w.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
            let v = PickView(screen: screen)
            v.owner = self
            w.contentView = v
            w.makeKeyAndOrderFront(nil)
            w.makeFirstResponder(v)
            windows.append(w)
            views.append(v)
        }
        NSApp.activate(ignoringOtherApps: true)
        update()
    }

    fileprivate func update() {
        let found = Self.windowUnderCursor()
        if found?.id != hovered?.id {
            hovered = found
            views.forEach { $0.needsDisplay = true }
        }
    }

    private func finish(_ picked: PickedWindow?) {
        guard let completion else { return }
        self.completion = nil
        windows.forEach { $0.orderOut(nil) }
        windows = []
        views = []
        WindowSelector.current = nil
        let screen = picked.flatMap { Self.screen(for: $0.frame) }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { completion(picked, screen) }
    }

    static func screen(for frame: NSRect) -> NSScreen? {
        NSScreen.screens.max { a, b in
            let ia = a.frame.intersection(frame), ib = b.frame.intersection(frame)
            return ia.width * ia.height < ib.width * ib.height
        }
    }

    /// Topmost normal window under the mouse, including Poltergeist's own (its overlays and controls sit on higher
    /// layers and are skipped), but not the menu bar, Dock or desktop.
    static func windowUnderCursor() -> PickedWindow? {
        guard let primary = NSScreen.screens.first else { return nil }
        let mouse = NSEvent.mouseLocation
        let cg = CGPoint(x: mouse.x, y: primary.frame.height - mouse.y)   // CG space: origin top-left of the primary display
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return nil }
        for info in list {   // front to back
            guard (info[kCGWindowLayer as String] as? Int) == 0,
                  (info[kCGWindowAlpha as String] as? Double ?? 1) > 0.01,
                  let number = info[kCGWindowNumber as String] as? Int,
                  let boundsDict = info[kCGWindowBounds as String] as? NSDictionary,
                  let b = CGRect(dictionaryRepresentation: boundsDict),
                  b.width >= 40, b.height >= 30, b.contains(cg) else { continue }
            let owner = info[kCGWindowOwnerName as String] as? String ?? ""
            let title = info[kCGWindowName as String] as? String ?? ""
            let name = title.isEmpty ? owner : "\(owner) — \(title)"
            let frame = NSRect(x: b.minX, y: primary.frame.height - b.maxY, width: b.width, height: b.height)
            return PickedWindow(id: CGWindowID(number), frame: frame, name: name)
        }
        return nil
    }
}
