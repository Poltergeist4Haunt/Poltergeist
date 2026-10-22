import AppKit
import SwiftUI

/// Optional touch-ups for a screenshot: rounded corners, a border and a drop shadow.
enum ImageEffects {
    static var wanted: Bool {
        let s = Settings.shared
        return s.fxOn && (s.fxRound || s.fxBorder > 0 || s.fxShadow)
    }

    /// Draws the effects around `image` (pixel size in, pixel size + margin out). Pass `settings` so a preview can use its own.
    static func apply(to image: CGImage, settings s: Settings = .shared) -> CGImage? {
        let w = image.width, h = image.height
        let radius = s.fxRound ? CGFloat(min(s.fxRadius, min(w, h) / 2)) : 0
        let pad = s.fxShadow ? max(8, s.fxShadowSize * 2) : 0
        let outW = w + pad * 2, outH = h + pad * 2
        guard let ctx = CGContext(data: nil, width: outW, height: outH, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.interpolationQuality = .high
        let rect = CGRect(x: pad, y: pad, width: w, height: h)          // CoreGraphics: origin bottom-left
        let path = CGPath(roundedRect: rect, cornerWidth: radius, cornerHeight: radius, transform: nil)

        if s.fxShadow {
            ctx.saveGState()
            ctx.setShadow(offset: CGSize(width: 0, height: -CGFloat(s.fxShadowSize) / 3), blur: CGFloat(s.fxShadowSize),
                          color: CGColor(gray: 0, alpha: 0.6))
            ctx.addPath(path)
            ctx.setFillColor(CGColor(gray: 0, alpha: 1))
            ctx.fillPath()
            ctx.restoreGState()
        }

        ctx.saveGState()
        ctx.addPath(path)
        ctx.clip()
        ctx.draw(image, in: rect)

        ctx.restoreGState()

        if s.fxBorder > 0 {
            let bw = CGFloat(s.fxBorder)
            let color = NSColor(Color(hexString: s.fxBorderColor) ?? Theme.accent).cgColor
            let inner = rect.insetBy(dx: bw / 2, dy: bw / 2)
            ctx.addPath(CGPath(roundedRect: inner, cornerWidth: max(0, radius - bw / 2), cornerHeight: max(0, radius - bw / 2), transform: nil))
            ctx.setStrokeColor(color)
            ctx.setLineWidth(bw)
            ctx.strokePath()
        }
        return ctx.makeImage()
    }

    /// Rewrites a PNG file with the effects applied. Returns false if nothing was changed.
    @discardableResult
    static func applyToFile(_ url: URL) -> Bool {
        guard wanted, let src = NSImage(contentsOf: url)?.cgImage(forProposedRect: nil, context: nil, hints: nil),
              let out = apply(to: src) else { return false }
        let rep = NSBitmapImageRep(cgImage: out)
        guard let png = rep.representation(using: .png, properties: [:]) else { return false }
        return (try? png.write(to: url)) != nil
    }
}
