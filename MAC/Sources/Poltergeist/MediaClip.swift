import AppKit
import AVFoundation
import ImageIO
import UniformTypeIdentifiers

/// An image, GIF or video the user brought in to place on a screenshot or recording.
final class MediaClip {
    enum Kind { case image, gif, video }

    let kind: Kind
    let name: String
    let url: URL?
    let pixelSize: CGSize
    /// Seconds. 0 for a still image; one loop for a GIF; the whole clip for a video.
    let duration: Double
    let hasAudio: Bool

    private let still: CGImage?
    private let gifFrames: [(image: CGImage, start: Double)]
    private let asset: AVURLAsset?
    private let generator: AVAssetImageGenerator?
    private let lock = NSLock()
    private var cachedVideoFrame: (time: Double, image: CGImage)?
    private var fetching = false

    var aspect: CGFloat { pixelSize.height > 0 ? pixelSize.width / pixelSize.height : 1 }
    var isAnimated: Bool { kind != .image }

    // MARK: Creation

    init(image: CGImage, name: String, url: URL? = nil) {
        kind = .image
        self.name = name
        self.url = url
        pixelSize = CGSize(width: image.width, height: image.height)
        duration = 0
        hasAudio = false
        still = image
        gifFrames = []
        asset = nil
        generator = nil
    }

    private init(gif frames: [(image: CGImage, start: Double)], total: Double, name: String, url: URL?) {
        kind = .gif
        self.name = name
        self.url = url
        pixelSize = CGSize(width: frames[0].image.width, height: frames[0].image.height)
        duration = total
        hasAudio = false
        still = nil
        gifFrames = frames
        asset = nil
        generator = nil
    }

    private init(video asset: AVURLAsset, size: CGSize, duration: Double, hasAudio: Bool, name: String, url: URL) {
        kind = .video
        self.name = name
        self.url = url
        pixelSize = size
        self.duration = duration
        self.hasAudio = hasAudio
        still = nil
        gifFrames = []
        self.asset = asset
        let g = AVAssetImageGenerator(asset: asset)
        g.appliesPreferredTrackTransform = true
        g.maximumSize = CGSize(width: 960, height: 960)         // overlays don't need full resolution
        g.requestedTimeToleranceBefore = CMTime(value: 1, timescale: 30)
        g.requestedTimeToleranceAfter = CMTime(value: 1, timescale: 30)
        generator = g
    }

    /// Loads a file: picture, GIF or video. Returns nil if it isn't something we can read.
    static func load(url: URL, allowVideo: Bool = true) async -> MediaClip? {
        let name = url.lastPathComponent
        guard let type = UTType(filenameExtension: url.pathExtension.lowercased()) else { return nil }
        if type.conforms(to: .gif) { return loadGIF(url: url, name: name) }
        if type.conforms(to: .image) { return loadImage(url: url, name: name, maxSide: 2400) }
        if allowVideo, type.conforms(to: .movie) || type.conforms(to: .video) || type.conforms(to: .mpeg4Movie) {
            return await loadVideo(url: url, name: name)
        }
        return nil
    }

    private static func loadImage(url: URL, name: String, maxSide: Int) -> MediaClip? {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil), let img = thumbnail(src, 0, maxSide) else { return nil }
        return MediaClip(image: img, name: name, url: url)
    }

    /// Upright (EXIF orientation applied), capped in size so huge photos don't eat memory.
    private static func thumbnail(_ src: CGImageSource, _ index: Int, _ maxSide: Int) -> CGImage? {
        CGImageSourceCreateThumbnailAtIndex(src, index, [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxSide,
        ] as CFDictionary)
    }

    private static func loadGIF(url: URL, name: String) -> MediaClip? {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        let count = CGImageSourceGetCount(src)
        guard count > 0 else { return nil }
        if count == 1 { return loadImage(url: url, name: name, maxSide: 2400) }

        // Long GIFs are thinned out so decoded frames stay within memory (~250 MB).
        let first = thumbnail(src, 0, 640)
        let bytesPerFrame = max(1, (first?.width ?? 640) * (first?.height ?? 360) * 4)
        let budgetFrames = max(24, 250_000_000 / bytesPerFrame)
        let step = max(1, Int((Double(count) / Double(budgetFrames)).rounded(.up)))

        var frames: [(CGImage, Double)] = []
        var t = 0.0
        var i = 0
        while i < count {
            var delay = 0.0
            for j in i..<min(count, i + step) { delay += gifDelay(src, j) }
            if let img = thumbnail(src, i, 640) { frames.append((img, t)) }
            t += delay
            i += step
        }
        guard !frames.isEmpty, t > 0 else { return nil }
        return MediaClip(gif: frames.map { (image: $0.0, start: $0.1) }, total: t, name: name, url: url)
    }

    private static func gifDelay(_ src: CGImageSource, _ index: Int) -> Double {
        let props = CGImageSourceCopyPropertiesAtIndex(src, index, nil) as? [CFString: Any]
        let gif = props?[kCGImagePropertyGIFDictionary] as? [CFString: Any]
        let d = (gif?[kCGImagePropertyGIFUnclampedDelayTime] as? Double) ?? (gif?[kCGImagePropertyGIFDelayTime] as? Double) ?? 0.1
        return d < 0.02 ? 0.1 : d       // browsers treat 0–1 cs delays as 100 ms
    }

    private static func loadVideo(url: URL, name: String) async -> MediaClip? {
        let asset = AVURLAsset(url: url)
        guard let track = try? await asset.loadTracks(withMediaType: .video).first,
              let size = try? await track.load(.naturalSize),
              let transform = try? await track.load(.preferredTransform),
              let dur = try? await asset.load(.duration).seconds, dur.isFinite, dur > 0 else { return nil }
        let upright = size.applying(transform)
        let hasAudio = ((try? await asset.loadTracks(withMediaType: .audio))?.isEmpty == false)
        return MediaClip(video: asset, size: CGSize(width: abs(upright.width), height: abs(upright.height)),
                         duration: dur, hasAudio: hasAudio, name: name, url: url)
    }

    // MARK: Frames

    /// The picture at `t` seconds after the clip started. GIFs loop; videos hold their last frame.
    func frame(at t: Double) -> CGImage? {
        switch kind {
        case .image: return still
        case .gif:
            guard duration > 0 else { return gifFrames.first?.image }
            let local = t.truncatingRemainder(dividingBy: duration)
            let pos = local < 0 ? local + duration : local
            var lo = 0, hi = gifFrames.count - 1
            while lo < hi {                                  // last frame starting at or before `pos`
                let mid = (lo + hi + 1) / 2
                if gifFrames[mid].start <= pos { lo = mid } else { hi = mid - 1 }
            }
            return gifFrames[lo].image
        case .video:
            guard let generator else { return nil }
            let clamped = min(max(0, t), max(0, duration - 0.05))
            lock.lock(); defer { lock.unlock() }
            if let c = cachedVideoFrame, abs(c.time - clamped) < 1.0 / 60 { return c.image }
            guard let img = try? generator.copyCGImage(at: CMTime(seconds: clamped, preferredTimescale: 600), actualTime: nil) else {
                return cachedVideoFrame?.image
            }
            cachedVideoFrame = (clamped, img)
            return img
        }
    }

    /// For live previews: whatever frame is ready now. Video frames are decoded in the background; `ready` is called
    /// on the main queue when a newer one is available.
    func previewFrame(at t: Double, ready: @escaping () -> Void) -> CGImage? {
        guard kind == .video else { return frame(at: t) }
        lock.lock()
        let cached = cachedVideoFrame
        let busy = fetching
        if !busy, cached == nil || abs((cached?.time ?? 0) - min(max(0, t), duration)) > 1.0 / 30 { fetching = true }
        let start = !busy && fetching
        lock.unlock()
        if start {
            DispatchQueue.global(qos: .userInitiated).async { [weak self] in
                guard let self else { return }
                _ = self.frame(at: t)
                self.lock.lock(); self.fetching = false; self.lock.unlock()
                DispatchQueue.main.async(execute: ready)
            }
        }
        return cached?.image
    }

    /// The video's audio, for mixing into an export or playing in the preview.
    var audioAsset: AVURLAsset? { kind == .video && hasAudio ? asset : nil }
}

// MARK: - Getting media in: paste, file picker

enum MediaImport {
    static func choose(allowVideo: Bool) -> [URL] {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.allowedContentTypes = allowVideo ? [.image, .gif, .movie, .video] : [.image]
        panel.message = allowVideo ? "Choose images, GIFs or videos" : "Choose images"
        return panel.runModal() == .OK ? panel.urls : []
    }

    static func hasContent(_ pb: NSPasteboard) -> Bool {
        pb.canReadObject(forClasses: [NSImage.self, NSURL.self], options: nil)
    }

    /// Files first (so a copied GIF or video keeps its animation), then a plain copied picture (screenshots, browser images).
    static func clips(from pb: NSPasteboard, allowVideo: Bool) async -> [MediaClip] {
        if let urls = pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty {
            var out: [MediaClip] = []
            for u in urls { if let c = await MediaClip.load(url: u, allowVideo: allowVideo) { out.append(c) } }
            if !out.isEmpty { return out }
        }
        if let img = (pb.readObjects(forClasses: [NSImage.self], options: nil) as? [NSImage])?.first,
           let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) {
            return [MediaClip(image: cg, name: "Pasted image")]
        }
        return []
    }
}
