import AppKit
import AVFoundation
import CoreImage

/// Cuts a time range out of a video without re-encoding (fast, no quality loss).
enum VideoTrimmer {
    /// Length of the picture. Older recordings can have audio running a few frames past the last video frame;
    /// using the video's end keeps the editor from showing black there.
    static func duration(of url: URL) async -> Double {
        let asset = AVURLAsset(url: url)
        let d = (try? await asset.load(.duration).seconds) ?? 0
        guard d.isFinite else { return 0 }
        if let track = try? await asset.loadTracks(withMediaType: .video).first,
           let range = try? await track.load(.timeRange), range.end.seconds.isFinite, range.end.seconds > 0 {
            return min(d, range.end.seconds)
        }
        return d
    }

    /// Returns a trimmed copy (same file name, in a new temp folder), or nil if the export failed.
    static func export(_ url: URL, start: Double, end: Double) async -> URL? {
        let asset = AVURLAsset(url: url)
        guard let session = AVAssetExportSession(asset: asset, presetName: AVAssetExportPresetPassthrough) else { return nil }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("trim-\(UUID().uuidString)")
        guard (try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)) != nil else { return nil }
        let out = dir.appendingPathComponent(url.lastPathComponent)
        session.outputURL = out
        session.outputFileType = .mp4
        session.shouldOptimizeForNetworkUse = true
        session.timeRange = CMTimeRange(start: CMTime(seconds: start, preferredTimescale: 600),
                                        end: CMTime(seconds: end, preferredTimescale: 600))
        await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in session.exportAsynchronously { c.resume() } }
        if session.status == .completed { return out }
        try? FileManager.default.removeItem(at: dir)
        return nil
    }
}

enum AudioTracks {
    /// The audio tracks of a file with the role recorded in each one's language tag.
    static func load(from asset: AVAsset) async -> [(track: AVAssetTrack, role: AudioRole)] {
        let tracks = (try? await asset.loadTracks(withMediaType: .audio)) ?? []
        var out: [(AVAssetTrack, AudioRole)] = []
        for t in tracks {
            let tag = try? await t.load(.extendedLanguageTag)
            out.append((t, AudioRole.from(tag: tag ?? nil)))
        }
        return out
    }
}

enum MuteMix {
    private static func time(_ x: Double) -> CMTime { CMTime(seconds: x, preferredTimescale: 600) }

    /// One track's volume over the timeline [origin, origin + length]: mutes (10 ms slopes), fades at the start and end of the
    /// mix, an optional clip fade, and a constant gain, all multiplied into one curve. AVFoundation rejects overlapping
    /// ramps and volume steps that touch ramps, so the curve is emitted as back-to-back ramps between breakpoints.
    static func params(track: AVAssetTrack, origin: Double, length: Double, fadeIn: Double, fadeOut: Double,
                       mutes: [(start: Double, end: Double)], gain: Float = 1,
                       clip: (start: Double, end: Double, fadeIn: Double, fadeOut: Double)? = nil) -> AVMutableAudioMixInputParameters {
        let p = AVMutableAudioMixInputParameters(track: track)
        guard length > 0.05 else { return p }
        let end = origin + length
        var merged: [(start: Double, end: Double)] = []
        for m in mutes.sorted(by: { $0.start < $1.start }) where m.end - m.start > 0.02 {
            if let last = merged.last, m.start - last.end < 0.03 { merged[merged.count - 1].end = max(last.end, m.end) }
            else { merged.append(m) }
        }
        func slope(_ m: (start: Double, end: Double)) -> Double { min(0.01, (m.end - m.start) / 2) }
        func muteAmount(_ t: Double, _ m: (start: Double, end: Double)) -> Double {     // 1 inside a mute, easing at its edges
            let rf = slope(m)
            guard t > m.start, t < m.end else { return 0 }
            return min(1, (t - m.start) / rf, (m.end - t) / rf)
        }
        func gainAt(_ t: Double) -> Double {
            var g = Double(gain)
            let u = t - origin
            if fadeIn > 0 { g *= min(1, max(0, u / fadeIn)) }
            if fadeOut > 0 { g *= min(1, max(0, (length - u) / fadeOut)) }
            for m in merged { g *= 1 - muteAmount(t, m) }
            if let c = clip {
                var k = 1.0
                if c.fadeIn > 0 { k = min(k, (t - c.start) / c.fadeIn) }
                if c.fadeOut > 0 { k = min(k, (c.end - t) / c.fadeOut) }
                g *= min(1, max(0, k))
            }
            return g
        }
        var points: [Double] = [origin, end]
        if fadeIn > 0 { points.append(origin + fadeIn) }
        if fadeOut > 0 { points.append(end - fadeOut) }
        for m in merged { points += [m.start, m.start + slope(m), m.end - slope(m), m.end] }
        if let c = clip {
            points += [c.start, c.end]
            if c.fadeIn > 0 { points.append(c.start + c.fadeIn) }
            if c.fadeOut > 0 { points.append(c.end - c.fadeOut) }
        }
        var unique: [Double] = []
        for t in points.filter({ $0 >= origin - 1e-9 && $0 <= end + 1e-9 }).sorted() {
            if let last = unique.last, t - last <= 0.002 { continue }
            unique.append(t)
        }
        for i in 0..<max(0, unique.count - 1) {
            let t0 = time(unique[i]), t1 = time(unique[i + 1])           // shared boundaries, so ramps never overlap by a tick
            p.setVolumeRamp(fromStartVolume: Float(gainAt(unique[i])), toEndVolume: Float(gainAt(unique[i + 1])),
                            timeRange: CMTimeRange(start: t0, duration: CMTimeSubtract(t1, t0)))
        }
        return p
    }

    static func make(tracks: [(track: AVAssetTrack, role: AudioRole)], origin: Double = 0, length: Double,
                     fadeIn: Double = 0, fadeOut: Double = 0,
                     ranges: (AudioRole) -> [(start: Double, end: Double)]) -> AVMutableAudioMix {
        let mix = AVMutableAudioMix()
        mix.inputParameters = tracks.map {
            params(track: $0.track, origin: origin, length: length, fadeIn: fadeIn, fadeOut: fadeOut, mutes: ranges($0.role))
        }
        return mix
    }
}

struct ExportFailure: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

/// Renders a VideoProject: removes the cut sections, draws shapes/text, pixelates blur regions that follow their
/// keyframes, and re-encodes at the chosen bitrate.
enum VideoExporter {
    static func export(source: URL, project: VideoProject, bitrateMbps: Int,
                       progress: @escaping (Double) -> Void) async throws -> URL {
        let asset = AVURLAsset(url: source)
        guard let vTrack = try await asset.loadTracks(withMediaType: .video).first else { throw ExportFailure("The recording has no video.") }
        let audioTracks = await AudioTracks.load(from: asset)
        let natural = try await vTrack.load(.naturalSize)
        let nominalFPS = try await vTrack.load(.nominalFrameRate)
        let pieces = project.pieces()
        guard !pieces.isEmpty else { throw ExportFailure("Nothing is left after the cuts.") }
        func t600(_ x: Double) -> CMTime { CMTime(seconds: x, preferredTimescale: 600) }

        // 1. Stitch the surviving parts into one timeline, speeding up or slowing down where asked.
        let comp = AVMutableComposition()
        guard let cv = comp.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid) else {
            throw ExportFailure("Couldn't build the edit.")
        }
        // one composition track per source audio track, so each can be muted on its own
        var compAudio: [(track: AVAssetTrack, role: AudioRole)] = []
        for a in audioTracks {
            if let t = comp.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid) { compAudio.append((t, a.role)) }
        }
        var cursor = CMTime.zero
        for piece in pieces {
            let range = CMTimeRange(start: t600(piece.srcStart), duration: t600(piece.srcEnd - piece.srcStart))
            try cv.insertTimeRange(range, of: vTrack, at: cursor)
            for (i, a) in audioTracks.enumerated() {
                try (compAudio[i].track as? AVMutableCompositionTrack)?.insertTimeRange(range, of: a.track, at: cursor)
            }
            if abs(piece.rate - 1) > 0.001 {                                   // retime this stretch (video and audio together)
                let newLength = t600(range.duration.seconds / piece.rate)
                comp.scaleTimeRange(CMTimeRange(start: cursor, duration: range.duration), toDuration: newLength)
                cursor = CMTimeAdd(cursor, newLength)
            } else {
                cursor = CMTimeAdd(cursor, range.duration)
            }
        }
        let total = max(cursor.seconds, 0.001)

        // Audio of imported videos: each plays from the moment its layer appears, cut along with the recording,
        // at its own volume (muted layers contribute nothing).
        var overlayAudio: [AVMutableCompositionTrack] = []
        var overlayParams: [AVMutableAudioMixInputParameters] = []
        for o in project.objects where o.kind == .media && !o.audioMuted && o.volume > 0.001 {
            guard let clip = o.media, let clipAsset = clip.audioAsset,
                  let srcTrack = try? await clipAsset.loadTracks(withMediaType: .audio).first,
                  let track = comp.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid) else { continue }
            let playsUntil = min(o.end, o.start + max(0, clip.duration - o.mediaOffset))
            for piece in pieces {
                let a = max(piece.srcStart, o.start), b = min(piece.srcEnd, playsUntil)
                guard b > a else { continue }
                let slice = CMTimeRange(start: t600(a - o.start + o.mediaOffset), duration: t600(b - a))
                let at = t600(piece.compStart + (a - piece.srcStart) / piece.rate)
                try? track.insertTimeRange(slice, of: srcTrack, at: at)
                if abs(piece.rate - 1) > 0.001 {                              // the clip is part of the footage, so it speeds up too
                    track.scaleTimeRange(CMTimeRange(start: at, duration: slice.duration), toDuration: t600((b - a) / piece.rate))
                }
            }
            overlayAudio.append(track)
            overlayParams.append(MuteMix.params(
                track: track, origin: 0, length: total, fadeIn: project.audioFadeIn, fadeOut: project.audioFadeOut, mutes: [],
                gain: Float(o.volume),
                clip: (project.outputTime(forSource: o.start), project.outputTime(forSource: playsUntil), o.fadeIn, o.fadeOut)))
        }

        // 2. Per-frame drawing. The handler gets composition time; map it back to source time for the keyframes.
        let timeMap = TimeMap(pieces: pieces)
        let objects = project.objects
        let renderer = AnnotationRenderer(size: natural)
        let vc = AVMutableVideoComposition(asset: comp) { request in
            let src = timeMap.sourceTime(request.compositionTime.seconds)
            let active = objects.filter { $0.isVisible(at: src) && $0.kind != .zoom }
            let zoom = project.zoomRect(at: src, videoSize: natural)
            guard !active.isEmpty || zoom != nil else { request.finish(with: request.sourceImage, context: nil); return }
            let base = request.sourceImage
            var image = base
            let height = natural.height

            for o in active where o.kind == .blur {                     // pixelate underneath first
                let g = o.geometry(at: src)
                let r = AnnotationRenderer.rect(g.p1, g.p2).intersection(CGRect(origin: .zero, size: natural)).integral
                guard r.width >= 2, r.height >= 2 else { continue }
                let ciRect = CGRect(x: r.minX, y: height - r.maxY, width: r.width, height: r.height)
                let k = AnnotationRenderer.blurStrength(level: o.level)
                let pixelated: CIImage                                    // (named for the pixel style; it is the soft blur when chosen)
                if o.softBlur {
                    pixelated = base.clampedToExtent().applyingGaussianBlur(sigma: Double(renderer.unit * 5 * k)).cropped(to: ciRect)
                } else {
                    pixelated = base.clampedToExtent()
                        .applyingFilter("CIPixellate", parameters: [kCIInputScaleKey: max(8, renderer.unit * 6 * k),
                                                                    kCIInputCenterKey: CIVector(x: ciRect.minX, y: ciRect.minY)])
                        .cropped(to: ciRect)
                }
                let opacity = o.opacity(at: src)                       // blur regions fade by blending the pixelated patch in
                let patch = opacity < 0.999
                    ? pixelated.applyingFilter("CIColorMatrix", parameters: ["inputAVector": CIVector(x: 0, y: 0, z: 0, w: opacity)])
                    : pixelated
                image = patch.composited(over: image)
            }
            let shapes = active.filter { $0.kind != .blur }.map { $0.annotation(at: src) }
            if !shapes.isEmpty, let overlay = Self.overlay(for: shapes, renderer: renderer, size: natural) {
                image = overlay.composited(over: image)
            }
            if let zoom { image = Self.zoomed(image, to: zoom, natural: natural) }                // the camera, after everything is drawn
            request.finish(with: image.cropped(to: base.extent), context: nil)
        }
        vc.renderSize = natural
        vc.frameDuration = CMTime(value: 1, timescale: CMTimeScale(max(1, nominalFPS.rounded())))

        // 3. Read the rendered frames and encode them with our own bitrate.
        let reader = try AVAssetReader(asset: comp)
        let vOut = AVAssetReaderVideoCompositionOutput(videoTracks: [cv], videoSettings: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA])
        vOut.videoComposition = vc
        reader.add(vOut)
        var aOut: AVAssetReaderAudioMixOutput?
        if !compAudio.isEmpty || !overlayAudio.isEmpty {
            let o = AVAssetReaderAudioMixOutput(audioTracks: compAudio.map { $0.track } + overlayAudio, audioSettings: [
                AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: 48_000, AVNumberOfChannelsKey: 2,
                AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true,
                AVLinearPCMIsNonInterleaved: false, AVLinearPCMIsBigEndianKey: false])
            o.audioTimePitchAlgorithm = .spectral          // keep the pitch when a section is sped up or slowed down
            let mix = MuteMix.make(tracks: compAudio, length: total, fadeIn: project.audioFadeIn, fadeOut: project.audioFadeOut) {
                project.mutedRanges(for: $0, inOutput: true)
            }                                                                                        // tracks are summed into one
            mix.inputParameters += overlayParams
            o.audioMix = mix
            reader.add(o)
            aOut = o
        }

        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("render-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let out = dir.appendingPathComponent(source.lastPathComponent)
        let writer = try AVAssetWriter(outputURL: out, fileType: .mp4)
        let vIn = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: Int(natural.width), AVVideoHeightKey: Int(natural.height),
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: bitrateMbps * 1_000_000,
                AVVideoMaxKeyFrameIntervalKey: Int(max(1, nominalFPS.rounded())),
                AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
            ] as [String: Any]])
        vIn.expectsMediaDataInRealTime = false
        writer.add(vIn)
        var aIn: AVAssetWriterInput?
        if aOut != nil {
            let i = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC, AVNumberOfChannelsKey: 2, AVSampleRateKey: 48_000, AVEncoderBitRateKey: 160_000])
            i.expectsMediaDataInRealTime = false
            writer.add(i)
            aIn = i
        }
        writer.shouldOptimizeForNetworkUse = true

        guard reader.startReading() else { throw ExportFailure(reader.error?.localizedDescription ?? "Couldn't read the recording.") }
        guard writer.startWriting() else { throw ExportFailure(writer.error?.localizedDescription ?? "Couldn't create the video file.") }
        writer.startSession(atSourceTime: .zero)

        let group = DispatchGroup()
        func pump(_ output: AVAssetReaderOutput, _ input: AVAssetWriterInput, label: String, reportsProgress: Bool) {
            group.enter()
            input.requestMediaDataWhenReady(on: DispatchQueue(label: label)) {
                while input.isReadyForMoreMediaData {
                    guard let sb = output.copyNextSampleBuffer() else {
                        input.markAsFinished(); group.leave(); return
                    }
                    if reportsProgress { progress(min(1, sb.presentationTimeStamp.seconds / total)) }
                    if !input.append(sb) { input.markAsFinished(); group.leave(); return }
                }
            }
        }
        pump(vOut, vIn, label: "polt.export.video", reportsProgress: true)
        if let aOut, let aIn { pump(aOut, aIn, label: "polt.export.audio", reportsProgress: false) }
        await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in group.notify(queue: .global()) { c.resume() } }

        if reader.status == .failed {
            writer.cancelWriting()
            throw ExportFailure(reader.error?.localizedDescription ?? "Rendering failed.")
        }
        await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in writer.finishWriting { c.resume() } }
        guard writer.status == .completed else { throw ExportFailure(writer.error?.localizedDescription ?? "Couldn't write the video.") }
        progress(1)
        return out
    }

    /// Crops to the camera region and scales it back up to the full frame.
    private static func zoomed(_ image: CIImage, to region: CGRect, natural: CGSize) -> CIImage {
        let r = region.intersection(CGRect(origin: .zero, size: natural))
        guard r.width > 8, r.height > 8 else { return image }
        let ci = CGRect(x: r.minX, y: natural.height - r.maxY, width: r.width, height: r.height)       // CIImage origin is bottom-left
        let kx = natural.width / r.width, ky = natural.height / r.height
        return image.cropped(to: ci)
            .transformed(by: CGAffineTransform(translationX: -ci.minX, y: -ci.minY))
            .applyingFilter("CILanczosScaleTransform", parameters: [kCIInputScaleKey: ky, kCIInputAspectRatioKey: kx / ky])
    }

    /// Shapes and text for one frame as a transparent image the size of the video.
    private static func overlay(for shapes: [Annotation], renderer: AnnotationRenderer, size: CGSize) -> CIImage? {
        let w = Int(size.width), h = Int(size.height)
        guard let cg = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                 space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                 bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        cg.translateBy(x: 0, y: CGFloat(h))
        cg.scaleBy(x: 1, y: -1)                       // top-left coordinates, like the editor
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: cg, flipped: true)
        for a in shapes { renderer.draw(a) }
        NSGraphicsContext.restoreGraphicsState()
        guard let image = cg.makeImage() else { return nil }
        return CIImage(cgImage: image)
    }

    /// For recordings that only need their audio sorted out (several tracks → one, mutes applied) and maybe a trim:
    /// the video is copied as-is, only the audio is re-encoded.
    static func remuxWithMixedAudio(source: URL, project: VideoProject,
                                    progress: @escaping (Double) -> Void) async throws -> URL {
        let asset = AVURLAsset(url: source)
        guard let vTrack = try await asset.loadTracks(withMediaType: .video).first else { throw ExportFailure("The recording has no video.") }
        let audioTracks = await AudioTracks.load(from: asset)
        let fullDuration = try await asset.load(.duration)
        let workDir = FileManager.default.temporaryDirectory.appendingPathComponent("mix-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: workDir, withIntermediateDirectories: true)

        // 1. Mix every audio track (with mutes) into one AAC file covering the whole recording.
        let mixedURL = workDir.appendingPathComponent("mixed.m4a")
        if !audioTracks.isEmpty {
            let comp = AVMutableComposition()
            var compAudio: [(track: AVAssetTrack, role: AudioRole)] = []
            for a in audioTracks {
                guard let t = comp.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid) else { continue }
                try t.insertTimeRange(CMTimeRange(start: .zero, duration: fullDuration), of: a.track, at: .zero)
                compAudio.append((t, a.role))
            }
            let reader = try AVAssetReader(asset: comp)
            let out = AVAssetReaderAudioMixOutput(audioTracks: compAudio.map { $0.track }, audioSettings: [
                AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: 48_000, AVNumberOfChannelsKey: 2,
                AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true,
                AVLinearPCMIsNonInterleaved: false, AVLinearPCMIsBigEndianKey: false])
            out.audioMix = MuteMix.make(tracks: compAudio, origin: project.trimStart, length: project.trimEnd - project.trimStart,
                                        fadeIn: project.audioFadeIn, fadeOut: project.audioFadeOut) {
                project.mutedRanges(for: $0, inOutput: false)
            }
            reader.add(out)
            let writer = try AVAssetWriter(outputURL: mixedURL, fileType: .m4a)
            let input = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC, AVNumberOfChannelsKey: 2, AVSampleRateKey: 48_000, AVEncoderBitRateKey: 160_000])
            input.expectsMediaDataInRealTime = false
            writer.add(input)
            guard reader.startReading() else { throw ExportFailure(reader.error?.localizedDescription ?? "Couldn't read the audio.") }
            writer.startWriting()
            writer.startSession(atSourceTime: .zero)
            let total = max(fullDuration.seconds, 0.001)
            await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in
                input.requestMediaDataWhenReady(on: DispatchQueue(label: "polt.mix.audio")) {
                    while input.isReadyForMoreMediaData {
                        guard let sb = out.copyNextSampleBuffer() else { input.markAsFinished(); c.resume(); return }
                        progress(min(0.8, sb.presentationTimeStamp.seconds / total * 0.8))
                        if !input.append(sb) { input.markAsFinished(); c.resume(); return }
                    }
                }
            }
            if reader.status == .failed { writer.cancelWriting(); throw ExportFailure(reader.error?.localizedDescription ?? "Mixing the audio failed.") }
            await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in writer.finishWriting { c.resume() } }
            guard writer.status == .completed else { throw ExportFailure(writer.error?.localizedDescription ?? "Couldn't write the mixed audio.") }
        }

        // 2. Original video + the one mixed track, copied into a new MP4 (trimmed if asked). Nothing is re-encoded here.
        let comp = AVMutableComposition()
        guard let cv = comp.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid) else {
            throw ExportFailure("Couldn't build the video.")
        }
        try cv.insertTimeRange(CMTimeRange(start: .zero, duration: fullDuration), of: vTrack, at: .zero)
        cv.preferredTransform = (try? await vTrack.load(.preferredTransform)) ?? .identity
        if !audioTracks.isEmpty {
            let mixed = AVURLAsset(url: mixedURL)
            if let mt = try await mixed.loadTracks(withMediaType: .audio).first,
               let ca = comp.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid) {
                let len = min(fullDuration, try await mixed.load(.duration))
                try ca.insertTimeRange(CMTimeRange(start: .zero, duration: len), of: mt, at: .zero)
            }
        }
        guard let session = AVAssetExportSession(asset: comp, presetName: AVAssetExportPresetPassthrough) else {
            throw ExportFailure("Couldn't create the export.")
        }
        let out = workDir.appendingPathComponent(source.lastPathComponent)
        session.outputURL = out
        session.outputFileType = .mp4
        session.shouldOptimizeForNetworkUse = true
        session.timeRange = CMTimeRange(start: CMTime(seconds: project.trimStart, preferredTimescale: 600),
                                        end: CMTime(seconds: project.trimEnd, preferredTimescale: 600))
        await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in session.exportAsynchronously { c.resume() } }
        guard session.status == .completed else { throw ExportFailure(session.error?.localizedDescription ?? "Couldn't save the video.") }
        try? FileManager.default.removeItem(at: mixedURL)
        progress(1)
        return out
    }
}
