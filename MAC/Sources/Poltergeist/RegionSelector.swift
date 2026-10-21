import AppKit

private final class OverlayWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

private final class SelectionView: NSView {
    var onFinish: ((NSRect?) -> Void)?
    private var start: NSPoint?
    private var current: NSPoint?

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .crosshair) }

    private var selection: NSRect? {
        guard let a = start, let b = current else { return nil }
        return NSRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(a.x - b.x), height: abs(a.y - b.y))
    }

    override func mouseDown(with event: NSEvent) {
        start = convert(event.locationInWindow, from: nil)
        current = start
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        current = convert(event.locationInWindow, from: nil)
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        current = convert(event.locationInWindow, from: nil)
        guard let r = selection, r.width >= 20, r.height >= 20, let window else { onFinish?(nil); return }
        onFinish?(window.convertToScreen(convert(r, to: nil)))
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { onFinish?(nil) } else { super.keyDown(with: event) }
    }

    override func draw(_ dirtyRect: NSRect) {
        let dim = NSBezierPath(rect: bounds)
        dim.windingRule = .evenOdd
        if let r = selection { dim.append(NSBezierPath(rect: r)) }
        NSColor.black.withAlphaComponent(selection == nil ? 0.2 : 0.4).setFill()
        dim.fill()

        guard let r = selection else {
            drawLabel("Drag to select an area  ·  Esc to cancel", at: NSPoint(x: bounds.midX, y: bounds.midY))
            return
        }
        NSColor(Theme.accent).setStroke()
        let border = NSBezierPath(rect: r)
        border.lineWidth = 2
        border.stroke()
        let scale = window?.backingScaleFactor ?? 1
        drawLabel("\(Int(r.width * scale)) × \(Int(r.height * scale))", at: NSPoint(x: r.midX, y: max(r.minY - 18, 14)))
    }

    private func drawLabel(_ text: String, at p: NSPoint) {
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 12, weight: .medium), .foregroundColor: NSColor.white,
        ]
        let str = NSAttributedString(string: text, attributes: attrs)
        let size = str.size()
        let box = NSRect(x: p.x - size.width / 2 - 8, y: p.y - size.height / 2 - 4, width: size.width + 16, height: size.height + 8)
        NSColor.black.withAlphaComponent(0.7).setFill()
        NSBezierPath(roundedRect: box, xRadius: 8, yRadius: 8).fill()
        str.draw(at: NSPoint(x: box.minX + 8, y: box.minY + 4))
    }
}

/// Full-screen drag-to-select overlay on every display. Calls back with a rect in screen coordinates, or nil if cancelled.
final class RegionSelector {
    static var current: RegionSelector?
    private var windows: [NSWindow] = []
    private var completion: ((NSRect?, NSScreen?) -> Void)?

    static func select(_ completion: @escaping (NSRect?, NSScreen?) -> Void) {
        let s = RegionSelector()
        current = s
        s.begin(completion)
    }

    func cancel() { finish(nil, nil) }

    private func begin(_ completion: @escaping (NSRect?, NSScreen?) -> Void) {
        self.completion = completion
        for screen in NSScreen.screens {
            let w = OverlayWindow(contentRect: screen.frame, styleMask: .borderless, backing: .buffered, defer: false, screen: screen)
            w.isOpaque = false
            w.backgroundColor = .clear
            w.level = .screenSaver
            w.hasShadow = false
            w.isReleasedWhenClosed = false
            w.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
            let v = SelectionView(frame: NSRect(origin: .zero, size: screen.frame.size))
            v.onFinish = { [weak self] rect in self?.finish(rect, screen) }
            w.contentView = v
            w.makeKeyAndOrderFront(nil)
            w.makeFirstResponder(v)
            w.invalidateCursorRects(for: v)
            windows.append(w)
        }
        NSApp.activate(ignoringOtherApps: true)
    }

    private func finish(_ rect: NSRect?, _ screen: NSScreen?) {
        guard let completion else { return }
        self.completion = nil
        windows.forEach { $0.orderOut(nil) }
        windows = []
        RegionSelector.current = nil
        // Let the overlay leave the screen before capture starts.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { completion(rect, screen) }
    }
}
