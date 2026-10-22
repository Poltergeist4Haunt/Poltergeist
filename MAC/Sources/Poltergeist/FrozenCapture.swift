import AppKit
import ScreenCaptureKit

private final class FreezeWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

/// One display's frozen picture, dimmed, with a drag-to-select rectangle that shows the picture at full brightness.
final class FreezeView: NSView {
    let image: CGImage
    var onFinish: ((CGImage?) -> Void)?
    private var start: NSPoint?
    private var current: NSPoint?

    static let dimAmount: CGFloat = 0.3

    init(image: CGImage, size: NSSize) {
        self.image = image
        super.init(frame: NSRect(origin: .zero, size: size))
    }
    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .crosshair) }

    /// Test hook: set the selection without the mouse.
    func setSelection(_ r: NSRect) {
        start = r.origin
        current = NSPoint(x: r.maxX, y: r.maxY)
        needsDisplay = true
    }

    var selection: NSRect? {
        guard let a = start, let b = current else { return nil }
        return NSRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(a.x - b.x), height: abs(a.y - b.y))
    }

    /// The selected part of the frozen picture, at full pixel resolution.
    func croppedImage(_ r: NSRect) -> CGImage? {
        let k = CGFloat(image.width) / bounds.width
        let px = CGRect(x: r.minX * k, y: r.minY * k, width: r.width * k, height: r.height * k).integral
        return image.cropping(to: px.intersection(CGRect(x: 0, y: 0, width: image.width, height: image.height)))
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
        guard let r = selection, r.width >= 4, r.height >= 4 else { onFinish?(nil); return }     // a plain click cancels
        onFinish?(croppedImage(r))
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { onFinish?(nil) } else { super.keyDown(with: event) }
    }

    override func draw(_ dirtyRect: NSRect) {
        NSGraphicsContext.current?.imageInterpolation = .high
        NSImage(cgImage: image, size: bounds.size).draw(in: bounds, from: .zero, operation: .copy, fraction: 1,
                                                        respectFlipped: true, hints: nil)
        // dim everything except the selection
        let dim = NSBezierPath(rect: bounds)
        if let r = selection { dim.append(NSBezierPath(rect: r)); dim.windingRule = .evenOdd }
        NSColor.black.withAlphaComponent(Self.dimAmount).setFill()
        dim.fill()

        guard let r = selection else {
            label(ActionHint.text.isEmpty ? "Drag to select an area  ·  Esc to cancel" : ActionHint.text, at: NSPoint(x: bounds.midX, y: bounds.midY))
            return
        }
        NSColor(Theme.accent).setStroke()
        let border = NSBezierPath(rect: r)
        border.lineWidth = 1.5
        border.stroke()
        let k = CGFloat(image.width) / bounds.width
        label("\(Int(r.width * k)) × \(Int(r.height * k))", at: NSPoint(x: r.midX, y: min(r.maxY + 18, bounds.maxY - 14)))
    }

    private func label(_ text: String, at p: NSPoint) {
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 12, weight: .medium), .foregroundColor: NSColor.white]
        let str = NSAttributedString(string: text, attributes: attrs)
        let size = str.size()
        let box = NSRect(x: p.x - size.width / 2 - 8, y: p.y - size.height / 2 - 4, width: size.width + 16, height: size.height + 8)
        NSColor.black.withAlphaComponent(0.7).setFill()
        NSBezierPath(roundedRect: box, xRadius: 8, yRadius: 8).fill()
        str.draw(at: NSPoint(x: box.minX + 8, y: box.minY + 4))
    }
}

/// Area screenshot with the screen frozen and slightly dimmed while you choose: every display is captured the moment
/// the hotkey is pressed, shown full-screen as a still, and the selection is cropped out of that still.
enum FrozenCapture {
    private static var windows: [NSWindow] = []

    /// nil means "couldn't freeze the screen", so the caller falls back to the system selector.
    @MainActor
    static func run() async -> CaptureResult? {
        guard #available(macOS 14.0, *) else { return nil }
        guard let shots = await snapshots(), !shots.isEmpty else { return nil }

        let selected: CGImage? = await withCheckedContinuation { cont in
            var finished = false
            func finish(_ image: CGImage?) {
                guard !finished else { return }
                finished = true
                windows.forEach { $0.orderOut(nil) }
                windows = []
                // let the dimmed overlay leave the screen before anything else happens
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) { cont.resume(returning: image) }
            }
            for (screen, image) in shots {
                let w = FreezeWindow(contentRect: screen.frame, styleMask: .borderless, backing: .buffered, defer: false, screen: screen)
                w.isOpaque = true
                w.backgroundColor = .black
                w.level = .screenSaver
                w.hasShadow = false
                w.isReleasedWhenClosed = false
                w.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
                let v = FreezeView(image: image, size: screen.frame.size)
                v.onFinish = { finish($0) }
                w.contentView = v
                w.makeKeyAndOrderFront(nil)
                w.makeFirstResponder(v)
                w.invalidateCursorRects(for: v)
                windows.append(w)
            }
            NSApp.activate(ignoringOtherApps: true)
        }

        guard let selected else { return .cancelled }
        let fmt = DateFormatter()
        fmt.dateFormat = "yyyy-MM-dd HH.mm.ss"
        let out = FileManager.default.temporaryDirectory.appendingPathComponent("Screenshot \(fmt.string(from: Date())).png")
        guard let png = NSBitmapImageRep(cgImage: selected).representation(using: .png, properties: [:]),
              (try? png.write(to: out)) != nil else { return .failed("Couldn't save the screenshot.") }
        return .file(out)
    }

    /// One still frame of a display. (Spelled with the completion handler so the right overload is used.)
    @available(macOS 14.0, *)
    static func captureImage(_ filter: SCContentFilter, _ configuration: SCStreamConfiguration) async -> CGImage? {
        await withCheckedContinuation { cont in
            SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration) { image, _ in
                cont.resume(returning: image)
            }
        }
    }

    @available(macOS 14.0, *)
    private static func snapshots() async -> [(NSScreen, CGImage)]? {
        guard let content = try? await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true) else { return nil }
        var out: [(NSScreen, CGImage)] = []
        for screen in NSScreen.screens {
            guard let num = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber,
                  let display = content.displays.first(where: { $0.displayID == num.uint32Value }) else { continue }
            let cfg = SCStreamConfiguration()
            cfg.width = Int(screen.frame.width * screen.backingScaleFactor)
            cfg.height = Int(screen.frame.height * screen.backingScaleFactor)
            cfg.showsCursor = false
            guard let image = await captureImage(SCContentFilter(display: display, excludingWindows: []), cfg) else { return nil }
            out.append((screen, image))
        }
        return out
    }
}
