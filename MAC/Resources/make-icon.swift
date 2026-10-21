// Renders AppIcon.icns: the menu-bar ghost in Haunt pink on a dark rounded square.
// Run: swiftc make-icon.swift -o /tmp/make-icon && /tmp/make-icon   (writes AppIcon.icns next to this file)
import AppKit

func ghostPath() -> NSBezierPath {
    // Same shape as the menu-bar icon, in an 18x18 box.
    let p = NSBezierPath()
    p.windingRule = .evenOdd
    p.move(to: NSPoint(x: 3, y: 3))
    p.line(to: NSPoint(x: 3, y: 9))
    p.curve(to: NSPoint(x: 15, y: 9), controlPoint1: NSPoint(x: 3, y: 17), controlPoint2: NSPoint(x: 15, y: 17))
    p.line(to: NSPoint(x: 15, y: 3))
    // three evenly hanging lobes along the bottom
    p.curve(to: NSPoint(x: 11, y: 3), controlPoint1: NSPoint(x: 14.4, y: 0.3), controlPoint2: NSPoint(x: 11.6, y: 0.3))
    p.curve(to: NSPoint(x: 7, y: 3), controlPoint1: NSPoint(x: 10.4, y: 0.3), controlPoint2: NSPoint(x: 7.6, y: 0.3))
    p.curve(to: NSPoint(x: 3, y: 3), controlPoint1: NSPoint(x: 6.4, y: 0.3), controlPoint2: NSPoint(x: 3.6, y: 0.3))
    p.close()
    p.append(NSBezierPath(ovalIn: NSRect(x: 5.6, y: 8.6, width: 2.4, height: 3)))
    p.append(NSBezierPath(ovalIn: NSRect(x: 10, y: 8.6, width: 2.4, height: 3)))
    return p
}

func render(_ px: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8, samplesPerPixel: 4,
                               hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let s = CGFloat(px)

    // macOS icon grid: the artwork is ~80% of the canvas, with a soft shadow underneath.
    let tile = NSRect(x: s * 0.1, y: s * 0.1, width: s * 0.8, height: s * 0.8)
    let radius = tile.width * 0.2237
    let shadow = NSShadow()
    shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
    shadow.shadowBlurRadius = s * 0.02
    shadow.shadowOffset = NSSize(width: 0, height: -s * 0.012)
    NSGraphicsContext.saveGraphicsState()
    shadow.set()
    NSColor(srgbRed: 0.06, green: 0.06, blue: 0.07, alpha: 1).setFill()
    NSBezierPath(roundedRect: tile, xRadius: radius, yRadius: radius).fill()
    NSGraphicsContext.restoreGraphicsState()

    let clip = NSBezierPath(roundedRect: tile, xRadius: radius, yRadius: radius)
    NSGraphicsContext.saveGraphicsState()
    clip.addClip()
    NSGradient(colors: [NSColor(srgbRed: 0.13, green: 0.12, blue: 0.15, alpha: 1), NSColor(srgbRed: 0.04, green: 0.04, blue: 0.05, alpha: 1)])!
        .draw(in: tile, angle: -90)
    // faint pink glow behind the ghost
    NSGradient(colors: [NSColor(srgbRed: 0.95, green: 0.49, blue: 0.70, alpha: 0.22), NSColor(srgbRed: 0.95, green: 0.49, blue: 0.70, alpha: 0)])!
        .draw(fromCenter: NSPoint(x: s * 0.5, y: s * 0.52), radius: 0, toCenter: NSPoint(x: s * 0.5, y: s * 0.52), radius: s * 0.38, options: [])
    NSGraphicsContext.restoreGraphicsState()
    NSColor(white: 1, alpha: 0.08).setStroke()
    clip.lineWidth = max(1, s * 0.004)
    clip.stroke()

    // the ghost, centred, ~58% of the tile
    let size = tile.width * 0.58
    let k = size / 18
    let t = NSAffineTransform()
    t.translateX(by: s * 0.5 - size / 2, yBy: s * 0.5 - size / 2 + s * 0.012)
    t.scale(by: k)
    let ghost = ghostPath()
    ghost.transform(using: t as AffineTransform)
    NSGraphicsContext.saveGraphicsState()
    let glow = NSShadow()
    glow.shadowColor = NSColor(srgbRed: 0.95, green: 0.49, blue: 0.70, alpha: 0.55)
    glow.shadowBlurRadius = s * 0.035
    glow.set()
    NSColor(srgbRed: 0.95, green: 0.49, blue: 0.70, alpha: 1).setFill()   // #f27db3
    ghost.fill()
    NSGraphicsContext.restoreGraphicsState()

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

let here = URL(fileURLWithPath: CommandLine.arguments[0]).deletingLastPathComponent()
let dir = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : FileManager.default.currentDirectoryPath)
let set = dir.appendingPathComponent("AppIcon.iconset")
try? FileManager.default.removeItem(at: set)
try FileManager.default.createDirectory(at: set, withIntermediateDirectories: true)
for (name, px) in [("icon_16x16", 16), ("icon_16x16@2x", 32), ("icon_32x32", 32), ("icon_32x32@2x", 64), ("icon_128x128", 128),
                   ("icon_128x128@2x", 256), ("icon_256x256", 256), ("icon_256x256@2x", 512), ("icon_512x512", 512), ("icon_512x512@2x", 1024)] {
    try render(px).write(to: set.appendingPathComponent("\(name).png"))
}
let p = Process()
p.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
p.arguments = ["-c", "icns", set.path, "-o", dir.appendingPathComponent("AppIcon.icns").path]
try p.run(); p.waitUntilExit()
try? FileManager.default.removeItem(at: set)
print("wrote AppIcon.icns (exit \(p.terminationStatus))")
