import AppKit
import SwiftUI
import CoreImage
import CoreImage.CIFilterBuiltins

// MARK: - Model

enum EditorTool: CaseIterable {
    case arrow, curve, pen, marker, box, circle, text, blur
    var title: String {
        switch self {
        case .arrow: return "Arrow"
        case .curve: return "Curve"
        case .pen: return "Pen"
        case .marker: return "Marker"
        case .box: return "Box"
        case .circle: return "Circle"
        case .text: return "Text"
        case .blur: return "Blur"
        }
    }
    var icon: String {
        switch self {
        case .arrow: return "arrow.up.right"
        case .curve: return "point.topleft.down.curvedto.point.bottomright.up"
        case .pen: return "pencil.tip"
        case .marker: return "highlighter"
        case .box: return "square"
        case .circle: return "circle"
        case .text: return "textformat"
        case .blur: return "square.grid.3x3.fill"
        }
    }
    var help: String {
        switch self {
        case .curve: return "Draw a curved arrow by hand: drag the path, the arrowhead goes on the end"
        case .pen: return "Draw freehand"
        case .marker: return "Highlight with a wide, see-through marker"
        default: return title
        }
    }
    /// The kind of thing a freehand tool draws (nil for the others).
    var freehand: Annotation.Kind? {
        switch self {
        case .pen: return .pen
        case .marker: return .marker
        case .curve: return .curve
        default: return nil
        }
    }
}

enum HandleTarget { case body, p1, p2 }

struct Annotation: Identifiable {
    enum Kind { case arrow, box, circle, text, blur, media, zoom, pen, marker, curve }
    let id = UUID()
    var kind: Kind
    var p1: CGPoint          // image pixels, origin top-left
    var p2: CGPoint
    var text = ""
    var color: NSColor
    var level: Int           // 1…3
    var image: CGImage? = nil          // for .media: the picture to draw, fitted inside the rectangle p1–p2
    var opacity: CGFloat = 1           // fades in video
    var soft = false                   // for .blur: a smooth blur instead of pixels
    /// For freehand kinds (pen, marker, curve arrow): the drawn path, as points in 0…1 relative to the rectangle p1–p2,
    /// so moving, resizing and animating the rectangle moves, resizes and animates the drawing.
    var path: [CGPoint] = []

    /// A freehand drawing from raw points (image coordinates).
    static func freehand(_ kind: Kind, points: [CGPoint], color: NSColor, level: Int) -> Annotation? {
        guard points.count >= 2 else { return nil }
        var minX = points[0].x, maxX = minX, minY = points[0].y, maxY = minY
        for q in points { minX = min(minX, q.x); maxX = max(maxX, q.x); minY = min(minY, q.y); maxY = max(maxY, q.y) }
        let minSide: CGFloat = 8                                  // a straight stroke still needs a rectangle to live in
        if maxX - minX < minSide { let c = (minX + maxX) / 2; minX = c - minSide / 2; maxX = c + minSide / 2 }
        if maxY - minY < minSide { let c = (minY + maxY) / 2; minY = c - minSide / 2; maxY = c + minSide / 2 }
        let w = maxX - minX, h = maxY - minY
        return Annotation(kind: kind, p1: CGPoint(x: minX, y: minY), p2: CGPoint(x: maxX, y: maxY), color: color, level: level,
                          path: points.map { CGPoint(x: ($0.x - minX) / w, y: ($0.y - minY) / h) })
    }

    /// Moves the whole thing (.body) or one end/corner. Pictures keep their proportions when a corner is dragged.
    func moved(_ target: HandleTarget, by d: CGSize) -> Annotation {
        var c = self
        if target != .p2 { c.p1 = CGPoint(x: p1.x + d.width, y: p1.y + d.height) }
        if target != .p1 { c.p2 = CGPoint(x: p2.x + d.width, y: p2.y + d.height) }
        if kind == .media, target != .body, let img = image {
            Annotation.lockAspect(&c.p1, &c.p2, moving: target, aspect: CGFloat(img.width) / CGFloat(max(1, img.height)))
        }
        return c
    }

    static func lockAspect(_ p1: inout CGPoint, _ p2: inout CGPoint, moving: HandleTarget, aspect: CGFloat) {
        let fixed = moving == .p1 ? p2 : p1
        let m = moving == .p1 ? p1 : p2
        let dx = m.x - fixed.x, dy = m.y - fixed.y
        let w = max(abs(dx), abs(dy) * aspect, 4)
        let h = w / aspect
        let out = CGPoint(x: fixed.x + (dx < 0 ? -w : w), y: fixed.y + (dy < 0 ? -h : h))
        if moving == .p1 { p1 = out } else { p2 = out }
    }
}

final class EditorModel: ObservableObject {
    static let palette: [NSColor] = [
        NSColor(Theme.accent), NSColor(hex: 0xff4d4d), NSColor(hex: 0xffd60a),
        NSColor(hex: 0x30d158), .white, .black,
    ]

    let image: CGImage
    let renderer: AnnotationRenderer
    @Published var tool: EditorTool? = .arrow       // nil = select / move
    @Published var colorIndex = 0            // -1 = the custom colour
    @Published var customColor = NSColor(hex: 0x8a5cff)
    @Published var level = 2
    @Published var blurSoft = false                  // style for new blur boxes
    @Published var annotations: [Annotation] = []
    @Published var selection: UUID?
    private var undoStack: [[Annotation]] = []

    init(image: CGImage) {
        self.image = image
        renderer = AnnotationRenderer(image: image)
    }

    var color: NSColor { colorIndex >= 0 ? Self.palette[colorIndex] : customColor }
    /// Call once before a change so it can be undone.
    func beginChange() {
        undoStack.append(annotations)
        if undoStack.count > 60 { undoStack.removeFirst() }
    }
    var canUndo: Bool { !undoStack.isEmpty }
    func undo() {
        guard let last = undoStack.popLast() else { return }
        annotations = last
        if !annotations.contains(where: { $0.id == selection }) { selection = nil }
    }
    func clear() { beginChange(); annotations.removeAll(); selection = nil }

    func add(_ a: Annotation) { beginChange(); annotations.append(a) }

    func replace(_ a: Annotation) {
        if let i = annotations.firstIndex(where: { $0.id == a.id }) { annotations[i] = a }
    }

    var selected: Annotation? { annotations.first { $0.id == selection } }

    func deleteSelected() {
        guard let id = selection else { return }
        beginChange()
        annotations.removeAll { $0.id == id }
        selection = nil
    }

    /// Puts a picture on the screenshot, at most 40% of its size, centred on `center` (default: the middle). Then
    /// switches to Select so it can be moved and resized straight away. GIFs and videos contribute their first frame.
    func addMedia(_ clip: MediaClip, at center: CGPoint? = nil) {
        guard let img = clip.frame(at: 0) else { return }
        let s = renderer.size
        let k = min(1, s.width * 0.4 / CGFloat(img.width), s.height * 0.4 / CGFloat(img.height))
        let w = CGFloat(img.width) * k, h = CGFloat(img.height) * k
        let c = center ?? CGPoint(x: s.width / 2, y: s.height / 2)
        let x = min(max(c.x - w / 2, 0), max(0, s.width - w)), y = min(max(c.y - h / 2, 0), max(0, s.height - h))
        let a = Annotation(kind: .media, p1: CGPoint(x: x, y: y), p2: CGPoint(x: x + w, y: y + h),
                           color: .white, level: 2, image: img)
        add(a)
        selection = a.id
        tool = nil
    }

    /// The edited screenshot at the original pixel size, in the original colour space so colours don't shift.
    func renderPNG() -> Data? {
        let w = image.width, h = image.height
        let space = image.colorSpace ?? CGColorSpace(name: CGColorSpace.sRGB)!
        guard let cg = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0, space: space,
                                 bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        cg.translateBy(x: 0, y: CGFloat(h))
        cg.scaleBy(x: 1, y: -1)                       // we draw in top-left coordinates
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: cg, flipped: true)
        NSGraphicsContext.current?.imageInterpolation = .high
        renderer.drawAll(annotations, draft: nil)
        NSGraphicsContext.restoreGraphicsState()
        guard let out = cg.makeImage() else { return nil }
        return NSBitmapImageRep(cgImage: out).representation(using: .png, properties: [:])
    }
}

extension NSColor {
    convenience init(hex: UInt32) {
        self.init(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
                  blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
    }
}

// MARK: - Drawing (shared by the live canvas and the final render)

/// Draws in image-pixel space with a top-left origin; callers set up the transform.
final class AnnotationRenderer {
    let image: CGImage?
    let size: CGSize
    private var blurCache: [UUID: (key: String, image: CGImage)] = [:]
    private let ci = CIContext()

    init(image: CGImage) {
        self.image = image
        size = CGSize(width: image.width, height: image.height)
    }

    /// For video: shapes and text are drawn over each frame; blur is applied to the frame itself.
    init(size: CGSize) {
        image = nil
        self.size = size
    }

    /// Scales strokes and text with the screenshot so they look the same on 1x and Retina captures.
    var unit: CGFloat { max(1, size.width / 700) }

    func drawAll(_ list: [Annotation], draft: Annotation?) {
        if let image {
            NSImage(cgImage: image, size: size).draw(in: NSRect(origin: .zero, size: size), from: .zero,
                                                     operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
        }
        for a in list { draw(a) }
        if let draft { draw(draft) }
    }

    func draw(_ a: Annotation) {
        guard a.opacity < 0.999 else { drawOpaque(a); return }
        guard a.opacity > 0.001, let cg = NSGraphicsContext.current?.cgContext else { return }
        cg.saveGState()
        cg.setAlpha(a.opacity)
        cg.beginTransparencyLayer(auxiliaryInfo: nil)        // fade the whole object as one, so overlapping parts don't double up
        drawOpaque(a)
        cg.endTransparencyLayer()
        cg.restoreGState()
    }

    private func drawOpaque(_ a: Annotation) {
        let w = unit * CGFloat(1 + a.level)
        a.color.setStroke()
        a.color.setFill()
        switch a.kind {
        case .arrow: drawArrow(a, width: w)
        case .box:
            let p = NSBezierPath(rect: Self.rect(a.p1, a.p2))
            p.lineWidth = w
            p.lineJoinStyle = .round
            p.stroke()
        case .circle:
            // Oval inscribed in the dragged rectangle, same as the Box tool; hold the drag square for a perfect circle.
            let p = NSBezierPath(ovalIn: Self.rect(a.p1, a.p2))
            p.lineWidth = w
            p.stroke()
        case .text: drawText(a)
        case .blur: drawBlur(a)
        case .media: drawMedia(a)
        case .zoom: break                                   // a camera move, not something drawn
        case .pen, .marker, .curve: drawFreehand(a)
        }
    }

    func strokeWidth(for a: Annotation) -> CGFloat {
        a.kind == .marker ? unit * CGFloat(5 + 4 * a.level) : unit * CGFloat(1 + a.level)
    }

    static func mappedPath(_ a: Annotation) -> [CGPoint] {
        let box = rect(a.p1, a.p2)
        return a.path.map { CGPoint(x: box.minX + $0.x * box.width, y: box.minY + $0.y * box.height) }
    }

    /// Is `p` within `tolerance` of the drawn line?
    static func nearPath(_ a: Annotation, _ p: CGPoint, tolerance: CGFloat) -> Bool {
        let pts = mappedPath(a)
        guard pts.count >= 2 else { return false }
        for i in 1..<pts.count {
            let a0 = pts[i - 1], b0 = pts[i]
            let dx = b0.x - a0.x, dy = b0.y - a0.y
            let len2 = dx * dx + dy * dy
            let f = len2 > 0 ? min(1, max(0, ((p.x - a0.x) * dx + (p.y - a0.y) * dy) / len2)) : 0
            if hypot(p.x - (a0.x + f * dx), p.y - (a0.y + f * dy)) <= tolerance { return true }
        }
        return false
    }

    /// Pen, marker and curve arrow: a smooth line through the drawn points. The marker is wide and see-through (drawn as one
    /// shape, so crossing itself doesn't get darker); the curve arrow ends in an arrowhead.
    private func drawFreehand(_ a: Annotation) {
        let pts = Self.mappedPath(a)
        guard pts.count >= 2 else { return }
        let w = strokeWidth(for: a)
        let line = NSBezierPath()
        line.move(to: pts[0])
        if pts.count == 2 {
            line.line(to: pts[1])
        } else {
            // quadratic curves through the midpoints, converted to cubics
            var cur = pts[0]
            for i in 1..<(pts.count - 1) {
                let mid = CGPoint(x: (pts[i].x + pts[i + 1].x) / 2, y: (pts[i].y + pts[i + 1].y) / 2)
                let c1 = CGPoint(x: cur.x + 2 / 3 * (pts[i].x - cur.x), y: cur.y + 2 / 3 * (pts[i].y - cur.y))
                let c2 = CGPoint(x: mid.x + 2 / 3 * (pts[i].x - mid.x), y: mid.y + 2 / 3 * (pts[i].y - mid.y))
                line.curve(to: mid, controlPoint1: c1, controlPoint2: c2)
                cur = mid
            }
            line.line(to: pts[pts.count - 1])
        }
        line.lineWidth = w
        line.lineCapStyle = a.kind == .marker ? .butt : .round
        line.lineJoinStyle = .round
        (a.kind == .marker ? a.color.withAlphaComponent(0.4) : a.color).setStroke()
        line.stroke()

        if a.kind == .curve, let end = pts.last {
            // the arrowhead points along the last stretch of the line
            let headLen = w * 4 + 6 * unit
            var from = pts[pts.count - 2]
            for q in pts.reversed().dropFirst() where hypot(q.x - end.x, q.y - end.y) >= headLen * 0.8 { from = q; break }
            let dx = end.x - from.x, dy = end.y - from.y
            let len = hypot(dx, dy)
            guard len > 0.5 else { return }
            let ux = dx / len, uy = dy / len
            let base = CGPoint(x: end.x - ux * headLen, y: end.y - uy * headLen)
            let head = NSBezierPath()
            head.move(to: end)
            head.line(to: CGPoint(x: base.x - uy * headLen * 0.5, y: base.y + ux * headLen * 0.5))
            head.line(to: CGPoint(x: base.x + uy * headLen * 0.5, y: base.y - ux * headLen * 0.5))
            head.close()
            a.color.setFill()
            head.fill()
        }
    }

    /// Aspect-fit inside the rectangle, so resizing never stretches the picture.
    private func drawMedia(_ a: Annotation) {
        guard let img = a.image else { return }
        let box = Self.rect(a.p1, a.p2)
        guard box.width > 1, box.height > 1 else { return }
        let aspect = CGFloat(img.width) / CGFloat(max(1, img.height))
        var fit = box
        if box.width / box.height > aspect { fit.size.width = box.height * aspect; fit.origin.x += (box.width - fit.width) / 2 }
        else { fit.size.height = box.width / aspect; fit.origin.y += (box.height - fit.height) / 2 }
        NSImage(cgImage: img, size: NSSize(width: img.width, height: img.height))
            .draw(in: fit, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: [.interpolation: NSImageInterpolation.high.rawValue])
    }

    private func drawArrow(_ a: Annotation, width w: CGFloat) {
        let dx = a.p2.x - a.p1.x, dy = a.p2.y - a.p1.y
        let len = hypot(dx, dy)
        guard len > 1 else { return }
        let ux = dx / len, uy = dy / len
        let headLen = min(len, w * 4 + 6 * unit)
        let headHalf = headLen * 0.5
        let base = CGPoint(x: a.p2.x - ux * headLen, y: a.p2.y - uy * headLen)

        let line = NSBezierPath()
        line.move(to: a.p1)
        line.line(to: CGPoint(x: base.x + ux * headLen * 0.3, y: base.y + uy * headLen * 0.3))
        line.lineWidth = w
        line.lineCapStyle = .round
        line.stroke()

        let head = NSBezierPath()
        head.move(to: a.p2)
        head.line(to: CGPoint(x: base.x - uy * headHalf, y: base.y + ux * headHalf))
        head.line(to: CGPoint(x: base.x + uy * headHalf, y: base.y - ux * headHalf))
        head.close()
        head.fill()
    }

    func font(level: Int) -> NSFont { .systemFont(ofSize: unit * CGFloat(6 + 4 * level), weight: .bold) }

    private func drawText(_ a: Annotation) {
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.55)
        shadow.shadowBlurRadius = unit * 2
        shadow.shadowOffset = NSSize(width: 0, height: -unit)
        NSAttributedString(string: a.text, attributes: [
            .font: font(level: a.level), .foregroundColor: a.color, .shadow: shadow,
        ]).draw(at: a.p1)
    }

    /// Pixelates the screenshot underneath the region. Pixelation (not a soft blur) so text can't be recovered.
    /// Strength for the S / M / L sizes.
    static func blurStrength(level: Int) -> CGFloat { [0.6, 1.0, 1.6][min(max(level, 1), 3) - 1] }

    /// Pixelated (hides text for sure) or softly blurred, both scaled to the picture size so they look the same on a small
    /// screenshot and a huge photo.
    private func drawBlur(_ a: Annotation) {
        guard let image else { return }   // video frames are blurred in the export pipeline instead
        let r = Self.rect(a.p1, a.p2).intersection(CGRect(origin: .zero, size: size)).integral
        guard r.width >= 2, r.height >= 2 else { return }
        // The patch depends on where the box is, so the cache key includes the position as well as the style.
        let key = "\(r.minX),\(r.minY),\(r.width),\(r.height),\(a.soft),\(a.level)"
        var rendered: CGImage? = blurCache[a.id]?.key == key ? blurCache[a.id]?.image : nil
        if rendered == nil {
            let src = CIImage(cgImage: image)
            let ciRect = CGRect(x: r.minX, y: size.height - r.maxY, width: r.width, height: r.height)
            let k = Self.blurStrength(level: a.level)
            let out: CIImage?
            if a.soft {
                out = src.clampedToExtent().applyingGaussianBlur(sigma: Double(unit * 5 * k)).cropped(to: ciRect)
            } else {
                let f = CIFilter.pixellate()
                f.inputImage = src.clampedToExtent()
                f.scale = Float(max(8, unit * 6 * k))
                f.center = CGPoint(x: ciRect.minX, y: ciRect.minY)
                out = f.outputImage?.cropped(to: ciRect)
            }
            if let out { rendered = ci.createCGImage(out, from: ciRect) }
            if let rendered { blurCache[a.id] = (key, rendered) }
        }
        if let rendered {
            NSImage(cgImage: rendered, size: r.size).draw(in: r, from: .zero, operation: .copy, fraction: 1,
                                                          respectFlipped: true, hints: nil)
        }
    }

    static func rect(_ a: CGPoint, _ b: CGPoint) -> CGRect {
        CGRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(a.x - b.x), height: abs(a.y - b.y))
    }
}

// MARK: - Canvas

final class CanvasView: NSView, NSTextFieldDelegate {
    let model: EditorModel
    private var draft: Annotation?
    private var textField: NSTextField?
    private var textPoint = CGPoint.zero
    private var editingID: UUID?            // the existing text being edited (hidden meanwhile); nil for brand-new text
    private var editingColor: NSColor?
    private var editingLevel = 2

    init(model: EditorModel) {
        self.model = model
        super.init(frame: .zero)
        registerForDraggedTypes([.fileURL, .tiff, .png])
    }
    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func viewDidMoveToWindow() { DispatchQueue.main.async { [weak self] in self?.window?.makeFirstResponder(self) } }

    // Image fits the view, centred.
    private var scale: CGFloat {
        let s = model.renderer.size
        return min(bounds.width / s.width, bounds.height / s.height)
    }
    private var origin: CGPoint {
        let s = model.renderer.size, k = scale
        return CGPoint(x: (bounds.width - s.width * k) / 2, y: (bounds.height - s.height * k) / 2)
    }
    private func toImage(_ p: NSPoint) -> CGPoint {
        let o = origin, k = scale, s = model.renderer.size
        return CGPoint(x: min(max((p.x - o.x) / k, 0), s.width), y: min(max((p.y - o.y) / k, 0), s.height))
    }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: model.tool == nil ? .arrow : (model.tool == .text ? .iBeam : .crosshair))
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor(Theme.background).setFill()
        bounds.fill()
        let o = origin, k = scale, s = model.renderer.size
        NSGraphicsContext.saveGraphicsState()
        NSBezierPath(rect: CGRect(x: o.x, y: o.y, width: s.width * k, height: s.height * k)).addClip()
        let t = NSAffineTransform()
        t.translateX(by: o.x, yBy: o.y)
        t.scale(by: k)
        t.concat()
        NSGraphicsContext.current?.imageInterpolation = .high
        model.renderer.drawAll(model.annotations.filter { $0.id != editingID }, draft: draft)
        NSGraphicsContext.restoreGraphicsState()
        if let a = model.selected, a.id != editingID { drawSelection(a) }
    }

    // MARK: Selection

    private func toView(_ p: CGPoint) -> CGPoint {
        let o = origin, k = scale
        return CGPoint(x: o.x + p.x * k, y: o.y + p.y * k)
    }

    private func bounds(of a: Annotation) -> CGRect {
        if a.kind == .text {
            let size = NSAttributedString(string: a.text, attributes: [.font: model.renderer.font(level: a.level)]).size()
            return CGRect(origin: a.p1, size: size)
        }
        return AnnotationRenderer.rect(a.p1, a.p2)
    }

    private func drawSelection(_ a: Annotation) {
        let b = bounds(of: a)
        let tl = toView(CGPoint(x: b.minX, y: b.minY)), br = toView(CGPoint(x: b.maxX, y: b.maxY))
        let box = NSBezierPath(rect: NSRect(x: tl.x, y: tl.y, width: br.x - tl.x, height: br.y - tl.y).insetBy(dx: -3, dy: -3))
        NSColor(Theme.accent).setStroke()
        box.lineWidth = 1.5
        box.setLineDash([5, 3], count: 2, phase: 0)
        box.stroke()
        guard a.kind != .text else { return }
        for p in [a.p1, a.p2] {
            let v = toView(p)
            let h = NSBezierPath(rect: NSRect(x: v.x - 4.5, y: v.y - 4.5, width: 9, height: 9))
            NSColor.white.setFill()
            h.fill()
            NSColor(Theme.accent).setStroke()
            h.lineWidth = 1.5
            h.stroke()
        }
    }

    private func hits(_ a: Annotation, _ p: CGPoint, tol: CGFloat) -> Bool {
        if !a.path.isEmpty { return AnnotationRenderer.nearPath(a, p, tolerance: tol + model.renderer.strokeWidth(for: a) / 2) }
        if a.kind == .arrow {
            let dx = a.p2.x - a.p1.x, dy = a.p2.y - a.p1.y
            let len2 = dx * dx + dy * dy
            let f = len2 > 0 ? min(1, max(0, ((p.x - a.p1.x) * dx + (p.y - a.p1.y) * dy) / len2)) : 0
            return hypot(p.x - (a.p1.x + f * dx), p.y - (a.p1.y + f * dy)) <= tol + model.renderer.unit * CGFloat(1 + a.level)
        }
        return bounds(of: a).insetBy(dx: -tol, dy: -tol).contains(p)
    }

    private var freePoints: [CGPoint] = []
    private var freeKind: Annotation.Kind?
    private var dragOriginal: Annotation?
    private var dragTarget: HandleTarget = .body
    private var dragStart = CGPoint.zero
    private var dragBegan = false

    override func mouseDown(with event: NSEvent) {
        let viewPoint = convert(event.locationInWindow, from: nil)
        window?.makeFirstResponder(self)
        commitText()
        let p = toImage(viewPoint)

        // Double-click on text edits it, whatever tool is active.
        if event.clickCount == 2, let a = model.annotations.last(where: { $0.kind == .text && hits($0, p, tol: 6 / scale) }) {
            model.selection = a.id
            beginText(at: a.p1, viewPoint: toView(a.p1), existing: a)
            return
        }

        guard let tool = model.tool else {                    // Select: handles of the selection first, then anything under the cursor
            let tol = 9 / scale
            if let a = model.selected, a.kind != .text {
                if hypot(p.x - a.p1.x, p.y - a.p1.y) <= tol { startDrag(a, .p1, p); return }
                if hypot(p.x - a.p2.x, p.y - a.p2.y) <= tol { startDrag(a, .p2, p); return }
            }
            if let a = model.annotations.last(where: { hits($0, p, tol: tol) }) { startDrag(a, .body, p) }
            else { model.selection = nil }
            needsDisplay = true
            return
        }
        model.selection = nil
        if let k = tool.freehand {                                   // pen, marker, curve arrow: collect the dragged path
            freeKind = k
            freePoints = [p]
            return
        }
        switch tool {
        case .pen, .marker, .curve: break
        case .text: beginText(at: p, viewPoint: viewPoint)
        case .arrow: draft = Annotation(kind: .arrow, p1: p, p2: p, color: model.color, level: model.level)
        case .box: draft = Annotation(kind: .box, p1: p, p2: p, color: model.color, level: model.level)
        case .circle: draft = Annotation(kind: .circle, p1: p, p2: p, color: model.color, level: model.level)
        case .blur: draft = Annotation(kind: .blur, p1: p, p2: p, color: .black, level: model.level, soft: model.blurSoft)
        }
    }

    private func startDrag(_ a: Annotation, _ target: HandleTarget, _ p: CGPoint) {
        model.selection = a.id
        dragOriginal = a
        dragTarget = target
        dragStart = p
        dragBegan = false
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        let p = toImage(convert(event.locationInWindow, from: nil))
        if let k = freeKind {
            if let last = freePoints.last, hypot(p.x - last.x, p.y - last.y) >= max(1, model.renderer.unit * 0.8) {
                freePoints.append(p)
                draft = Annotation.freehand(k, points: freePoints, color: model.color, level: model.level)
            }
            needsDisplay = true
            return
        }
        if draft != nil {
            draft?.p2 = p
            needsDisplay = true
            return
        }
        guard let original = dragOriginal else { return }
        let delta = CGSize(width: p.x - dragStart.x, height: p.y - dragStart.y)
        if !dragBegan {
            guard hypot(delta.width, delta.height) * scale > 2 else { return }      // ignore wobble while clicking
            model.beginChange()
            dragBegan = true
        }
        model.replace(original.moved(dragTarget, by: delta))                         // always from the mouse-down state
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        dragOriginal = nil
        if let k = freeKind {
            freeKind = nil
            let p = toImage(convert(event.locationInWindow, from: nil))
            if freePoints.last != p { freePoints.append(p) }
            draft = nil
            if let a = Annotation.freehand(k, points: freePoints, color: model.color, level: model.level),
               freePoints.count >= 3 || hypot(a.p2.x - a.p1.x, a.p2.y - a.p1.y) > 6 { model.add(a) }    // a click leaves nothing
            freePoints = []
            needsDisplay = true
            return
        }
        guard var d = draft else { return }
        d.p2 = toImage(convert(event.locationInWindow, from: nil))
        draft = nil
        let big = hypot(d.p2.x - d.p1.x, d.p2.y - d.p1.y) > 4
        if big { model.add(d) }                       // a stray click doesn't leave an invisible mark
        needsDisplay = true
    }

    // MARK: Keyboard, paste, drop

    override func keyDown(with event: NSEvent) {
        switch event.keyCode {
        case 51, 117: model.deleteSelected()          // delete
        case 53: model.selection = nil; needsDisplay = true
        default: super.keyDown(with: event)
        }
    }

    /// ⌘V (through the Edit menu) puts a copied picture, or a copied image file, on the screenshot.
    @objc func paste(_ sender: Any?) { importMedia(from: .general, at: nil) }

    func importMedia(from pb: NSPasteboard, at point: CGPoint?) {
        Task { @MainActor in
            for clip in await MediaImport.clips(from: pb, allowVideo: false) { model.addMedia(clip, at: point) }
            needsDisplay = true
        }
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation { .copy }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        importMedia(from: sender.draggingPasteboard, at: toImage(convert(sender.draggingLocation, from: nil)))
        return true
    }

    // MARK: Text

    private func beginText(at p: CGPoint, viewPoint: NSPoint, existing: Annotation? = nil) {
        textPoint = p
        editingID = existing?.id
        editingColor = existing?.color
        editingLevel = existing?.level ?? model.level
        let size = model.renderer.font(level: editingLevel).pointSize * scale
        let field = NSTextField(frame: NSRect(x: viewPoint.x, y: viewPoint.y, width: max(160, bounds.maxX - viewPoint.x - 8),
                                              height: size * 1.5))
        field.font = .systemFont(ofSize: size, weight: .bold)
        field.textColor = existing?.color ?? model.color
        field.stringValue = existing?.text ?? ""
        field.drawsBackground = false
        field.isBordered = false
        field.focusRingType = .none
        field.placeholderString = "Type, then press Return"
        field.delegate = self
        addSubview(field)
        window?.makeFirstResponder(field)
        textField = field
    }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
        if selector == #selector(NSResponder.insertNewline(_:)) { commitText(); return true }
        if selector == #selector(NSResponder.cancelOperation(_:)) { textField?.stringValue = ""; commitText(); return true }
        return false
    }

    func controlTextDidEndEditing(_ obj: Notification) { commitText() }

    func commitText() {
        guard let field = textField else { return }
        textField = nil
        let text = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        field.removeFromSuperview()
        if let id = editingID {                                   // editing existing text: change it, or remove it if emptied
            if let a = model.annotations.first(where: { $0.id == id }), a.text != text {
                if text.isEmpty { model.selection = id; model.deleteSelected() }
                else { model.beginChange(); var c = a; c.text = text; model.replace(c) }
            }
            editingID = nil
        } else if !text.isEmpty {
            model.add(Annotation(kind: .text, p1: textPoint, p2: textPoint, text: text,
                                 color: model.color, level: model.level))
        }
        window?.makeFirstResponder(self)
        needsDisplay = true
    }
}

struct CanvasRepresentable: NSViewRepresentable {
    @ObservedObject var model: EditorModel

    func makeNSView(context: Context) -> CanvasView { CanvasView(model: model) }

    func updateNSView(_ view: CanvasView, context: Context) {
        _ = model.annotations.count     // redraw whenever annotations, selection or tool change
        _ = model.selection
        _ = model.tool
        view.window?.invalidateCursorRects(for: view)
        view.needsDisplay = true
    }
}

// MARK: - Window

struct EditorView: View {
    @ObservedObject var model: EditorModel
    let onUpload: () -> Void
    let onCopy: () -> Void
    let onCancel: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            // Labels when there's room, icons only when the window is narrow.
            ViewThatFits(in: .horizontal) {
                toolbar(compact: false)
                toolbar(compact: true)
            }
            .padding(.horizontal, 14).padding(.vertical, 10)
            .background(Theme.card)
            .overlay(Rectangle().fill(Theme.border).frame(height: 1), alignment: .bottom)

            CanvasRepresentable(model: model)
        }
        .background(Theme.background)
        .preferredColorScheme(.dark)
    }

    /// Size / strength: changes the selected item if there is one, and is also the default for the next thing drawn.
    private var levelBinding: Binding<Int> {
        Binding(get: { model.selected?.level ?? model.level }, set: { v in
            model.level = v
            if var a = model.selected, a.level != v { model.beginChange(); a.level = v; model.replace(a) }
        })
    }

    private var blurStyleBinding: Binding<Bool> {
        Binding(get: { model.selected?.kind == .blur ? (model.selected?.soft ?? false) : model.blurSoft }, set: { v in
            model.blurSoft = v
            if var a = model.selected, a.kind == .blur, a.soft != v { model.beginChange(); a.soft = v; model.replace(a) }
        })
    }

    private func importImages() {
        let urls = MediaImport.choose(allowVideo: false)
        Task { @MainActor in
            for u in urls { if let clip = await MediaClip.load(url: u, allowVideo: false) { model.addMedia(clip) } }
        }
    }

    private func toolbar(compact: Bool) -> some View {
        HStack(spacing: 8) {
            Button { model.tool = nil } label: {
                if compact { Image(systemName: "cursorarrow") } else { Label("Select", systemImage: "cursorarrow") }
            }.buttonStyle(PillButtonStyle(prominent: model.tool == nil)).help("Select, move and resize")
            ForEach(EditorTool.allCases, id: \.self) { t in
                Button { model.tool = t } label: {
                    if compact { Image(systemName: t.icon) } else { Label(t.title, systemImage: t.icon) }
                }
                .buttonStyle(PillButtonStyle(prominent: model.tool == t))
                .help(t.help)
            }
            Button { importImages() } label: {
                if compact { Image(systemName: "photo.badge.plus") } else { Label("Image", systemImage: "photo.badge.plus") }
            }
            .buttonStyle(PillButtonStyle(prominent: false))
            .help("Add an image from a file. You can also paste one with ⌘V or drop it onto the screenshot.")
            Divider().frame(height: 22)
            ForEach(Array(EditorModel.palette.enumerated()), id: \.offset) { i, c in
                Button { model.colorIndex = i } label: {
                    Circle().fill(Color(nsColor: c)).frame(width: 20, height: 20)
                        .overlay(Circle().stroke(model.colorIndex == i ? Theme.accent : Theme.border, lineWidth: model.colorIndex == i ? 2.5 : 1))
                        .padding(3).contentShape(Circle())
                }.buttonStyle(.plain)
            }
            // Any colour: the swatch re-selects the custom colour, the well next to it opens the colour picker.
            HStack(spacing: 2) {
                Button { model.colorIndex = -1 } label: {
                    Circle().fill(Color(nsColor: model.customColor)).frame(width: 20, height: 20)
                        .overlay(Circle().stroke(model.colorIndex == -1 ? Theme.accent : Theme.border,
                                                 lineWidth: model.colorIndex == -1 ? 2.5 : 1))
                        .padding(3).contentShape(Circle())
                }
                .buttonStyle(.plain)
                .help("Use your custom colour")
                ColorPicker("", selection: Binding(
                    get: { Color(nsColor: model.customColor) },
                    set: { model.customColor = NSColor($0).usingColorSpace(.sRGB) ?? model.customColor; model.colorIndex = -1 }
                ), supportsOpacity: false)
                .labelsHidden()
                .help("Choose any colour")
            }
            Picker("", selection: levelBinding) {
                Text("S").tag(1); Text("M").tag(2); Text("L").tag(3)
            }
            .pickerStyle(.segmented).labelsHidden().frame(width: 96)
            .help("Size, or blur strength")
            if model.tool == .blur || model.selected?.kind == .blur {
                Picker("", selection: blurStyleBinding) { Text("Pixels").tag(false); Text("Soft").tag(true) }
                    .pickerStyle(.segmented).labelsHidden().frame(width: 120)
                    .help("Pixelate, or blur softly. Soft blur looks better on large images.")
            }
            Divider().frame(height: 22)
            Button { model.undo() } label: { Image(systemName: "arrow.uturn.backward") }
                .buttonStyle(PillButtonStyle(prominent: false))
                .keyboardShortcut("z", modifiers: .command)
                .disabled(!model.canUndo)
                .help("Undo")
            Spacer(minLength: 8)
            Button("Cancel", action: onCancel).buttonStyle(PillButtonStyle(prominent: false))
                .keyboardShortcut(.cancelAction)
            SplitActionButton(title: "Upload", primary: onUpload, primaryShortcut: KeyboardShortcut("u", modifiers: .command), choices: [
                .init(title: "Copy to clipboard", icon: "doc.on.clipboard", action: onCopy,
                      shortcut: KeyboardShortcut("c", modifiers: [.command, .shift])),
            ])
        }
    }
}

enum EditorResult {
    case upload(URL)
    case copy(URL)          // put the edited image on the clipboard instead of uploading
    case cancel
}

/// Opens the quick editor for a screenshot. The completion receives the edited file, or .cancel.
final class ImageEditor: NSObject, NSWindowDelegate {
    private static var open: [ImageEditor] = []
    private var window: NSWindow?
    private var completion: ((EditorResult) -> Void)?
    private let source: URL
    private let model: EditorModel

    static func edit(_ url: URL, completion: @escaping (EditorResult) -> Void) {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil),
              let image = CGImageSourceCreateImageAtIndex(src, 0, nil) else {
            completion(.upload(url))     // can't open it for editing: upload as-is
            return
        }
        let e = ImageEditor(source: url, model: EditorModel(image: image), completion: completion)
        open.append(e)
        e.show()
    }

    private init(source: URL, model: EditorModel, completion: @escaping (EditorResult) -> Void) {
        self.source = source
        self.model = model
        self.completion = completion
    }

    private func show() {
        let view = EditorView(model: model, onUpload: { [weak self] in self?.finish(.upload) },
                              onCopy: { [weak self] in self?.finish(.copy) },
                              onCancel: { [weak self] in self?.finish(.cancel) })
        let w = NSWindow(contentViewController: NSHostingController(rootView: view))
        w.title = "Edit screenshot"
        w.styleMask = [.titled, .closable, .resizable, .miniaturizable]
        w.appearance = NSAppearance(named: .darkAqua)
        w.backgroundColor = NSColor(Theme.background)
        w.isReleasedWhenClosed = false
        w.delegate = self
        w.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]

        // Fit the screenshot on screen (at most ~80%), never smaller than the toolbar needs.
        let screen = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1280, height: 800)
        let pts = NSSize(width: CGFloat(model.image.width), height: CGFloat(model.image.height))
        let k = min(1, (screen.width * 0.8) / pts.width, (screen.height * 0.8 - 52) / pts.height)
        w.setContentSize(NSSize(width: max(900, pts.width * k), height: pts.height * k + 52))
        w.contentMinSize = NSSize(width: 640, height: 380)
        w.center()
        window = w
        w.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private enum Action { case upload, copy, cancel }

    private func finish(_ action: Action) {
        guard let completion else { return }
        self.completion = nil
        var result = EditorResult.cancel
        if action != .cancel {
            // The edited picture (or the untouched original if nothing was drawn).
            var file = source
            if !model.annotations.isEmpty, let png = model.renderPNG() {
                let dir = FileManager.default.temporaryDirectory.appendingPathComponent("edited-\(UUID().uuidString)")
                let out = dir.appendingPathComponent(source.deletingPathExtension().lastPathComponent + ".png")
                if (try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)) != nil,
                   (try? png.write(to: out)) != nil { file = out }
            }
            result = action == .copy ? .copy(file) : .upload(file)
        }
        window?.delegate = nil
        window?.close()
        window = nil
        Self.open.removeAll { $0 === self }
        completion(result)
    }

    func windowWillClose(_ notification: Notification) { finish(.cancel) }   // closing = cancel
}
