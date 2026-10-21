import AppKit

/// A position of an object at one moment. Coordinates are video pixels, origin top-left.
struct Keyframe: Equatable {
    var time: Double
    var p1: CGPoint
    var p2: CGPoint
}

/// A shape, text or blur placed on the video. It's visible from `start` to `end` and glides between its keyframes.
struct VideoObject: Identifiable {
    var id = UUID()
    var kind: Annotation.Kind
    var color: NSColor
    var level: Int
    var text = ""
    var start: Double
    var end: Double
    var keyframes: [Keyframe]        // sorted by time, never empty
    // For .media layers (an image, GIF or video placed over the recording):
    var media: MediaClip? = nil
    var volume = 1.0                 // 0…1, the clip's own audio
    var audioMuted = false
    /// Seconds into the clip where this layer starts playing. Non-zero for the second half of a split GIF or video,
    /// so it carries on from where the first half stopped.
    var mediaOffset = 0.0
    /// For .blur: a smooth blur instead of pixels.
    var softBlur = false
    /// For freehand kinds: the drawn path, normalized to the rectangle (see Annotation.path).
    var path: [CGPoint] = []
    /// Fade in / out times in seconds. For a .zoom object they are the ease into and out of the zoom instead.
    var fadeIn = 0.0
    var fadeOut = 0.0

    /// Half-open, so two halves of a split layer never both show on the frame where they meet.
    func isVisible(at t: Double) -> Bool { t >= start && t < end }

    /// 0…1 from the fade in / out times.
    func opacity(at t: Double) -> CGFloat {
        var o = 1.0
        if fadeIn > 0 { o = min(o, (t - start) / fadeIn) }
        if fadeOut > 0 { o = min(o, (end - t) / fadeOut) }
        return CGFloat(min(1, max(0, o)))
    }

    /// Linear interpolation between the surrounding keyframes; holds the first/last position outside them.
    func geometry(at t: Double) -> (p1: CGPoint, p2: CGPoint) {
        guard let first = keyframes.first, let last = keyframes.last else { return (.zero, .zero) }
        if t <= first.time { return (first.p1, first.p2) }
        if t >= last.time { return (last.p1, last.p2) }
        for i in 1..<keyframes.count {
            let a = keyframes[i - 1], b = keyframes[i]
            if t <= b.time {
                let span = b.time - a.time
                let f = span > 0 ? CGFloat((t - a.time) / span) : 1
                return (Self.lerp(a.p1, b.p1, f), Self.lerp(a.p2, b.p2, f))
            }
        }
        return (last.p1, last.p2)
    }

    /// `preview` is given by the live editor so video layers can decode frames in the background; export asks for exact frames.
    func annotation(at t: Double, preview: (() -> Void)? = nil) -> Annotation {
        let g = geometry(at: t)
        var a = Annotation(kind: kind, p1: g.p1, p2: g.p2, text: text, color: color, level: level)
        if kind != .zoom { a.opacity = opacity(at: t) }
        a.soft = softBlur
        a.path = path
        if kind == .media, let m = media {
            let local = t - start + mediaOffset         // the clip starts playing when the layer appears
            a.image = preview.map { m.previewFrame(at: local, ready: $0) } ?? m.frame(at: local)
        }
        return a
    }

    /// Sets the position at time `t`, reusing a keyframe that's already (almost) there.
    mutating func setKeyframe(at t: Double, p1: CGPoint, p2: CGPoint) {
        if let i = keyframes.firstIndex(where: { abs($0.time - t) < 0.05 }) {
            keyframes[i].p1 = p1
            keyframes[i].p2 = p2
        } else {
            keyframes.append(Keyframe(time: t, p1: p1, p2: p2))
            keyframes.sort { $0.time < $1.time }
        }
    }

    /// Dragging at the playhead. With `animate` it records a keyframe at time `t` (so the object moves over time);
    /// without it the whole path is shifted and no keyframe is added.
    func moved(_ target: HandleTarget, by d: CGSize, at t: Double, animate: Bool, videoAspect: CGFloat = 16.0 / 9.0) -> VideoObject {
        func shift(_ p: CGPoint, _ moves: Bool) -> CGPoint { moves ? CGPoint(x: p.x + d.width, y: p.y + d.height) : p }
        let movesP1 = target != .p2, movesP2 = target != .p1
        func locked(_ a: CGPoint, _ b: CGPoint) -> (CGPoint, CGPoint) {      // pictures keep their proportions when resized
            guard target != .body, kind == .media || kind == .zoom else { return (a, b) }
            var p1 = a, p2 = b
            Annotation.lockAspect(&p1, &p2, moving: target, aspect: kind == .zoom ? videoAspect : (media?.aspect ?? 1))
            return (p1, p2)
        }
        var copy = self
        if animate {
            let g = geometry(at: t)
            let n = locked(shift(g.p1, movesP1), shift(g.p2, movesP2))
            copy.setKeyframe(at: t, p1: n.0, p2: n.1)
        } else {
            for i in copy.keyframes.indices {
                let n = locked(shift(copy.keyframes[i].p1, movesP1), shift(copy.keyframes[i].p2, movesP2))
                copy.keyframes[i].p1 = n.0
                copy.keyframes[i].p2 = n.1
            }
        }
        return copy
    }

    private static func lerp(_ a: CGPoint, _ b: CGPoint, _ f: CGFloat) -> CGPoint {
        CGPoint(x: a.x + (b.x - a.x) * f, y: a.y + (b.y - a.y) * f)
    }
}

/// Which recorded source an audio track holds. Recordings label their tracks so the editor can tell them apart.
enum AudioRole: String, CaseIterable {
    case system, mic, unknown

    var title: String {
        switch self {
        case .system: return "Desktop audio"
        case .mic: return "Microphone"
        case .unknown: return "Audio"
        }
    }

    /// Stored in the track's language tag (private-use BCP 47), which survives in the MP4.
    var tag: String? {
        switch self {
        case .system: return "und-x-psys"
        case .mic: return "und-x-pmic"
        case .unknown: return nil
        }
    }

    static func from(tag: String?) -> AudioRole {
        switch tag {
        case "und-x-psys": return .system
        case "und-x-pmic": return .mic
        default: return .unknown
        }
    }
}

/// A stretch where one audio track is silenced (source time). The other tracks keep playing.
struct AudioMute: Identifiable, Equatable {
    var id = UUID()
    var role: AudioRole
    var start: Double
    var end: Double
}

/// A place where an audio track was split in the editor, so its parts can be picked and muted one by one. Not used by the export itself.
struct AudioSplit: Identifiable, Equatable {
    var id = UUID()
    var role: AudioRole
    var time: Double
}

/// A part of an audio track between its split points, as picked in the timeline.
struct AudioSelection: Equatable {
    var role: AudioRole
    var start: Double
    var end: Double
}

/// A stretch of the recording played faster or slower (source time).
struct SpeedRegion: Identifiable, Equatable {
    var id = UUID()
    var start: Double
    var end: Double
    var rate: Double              // 0.25 … 4
}

/// One clip of the recording after splitting. The clips always cover the whole timeline end to end; a removed clip is
/// skipped in the preview and left out of the export.
struct Segment: Identifiable, Equatable {
    var id = UUID()
    var start: Double
    var end: Double
    var removed = false
}

struct Cut: Identifiable, Equatable {
    var id = UUID()
    var start: Double
    var end: Double
}

/// Everything the user did to a recording. Times are in the original (source) video.
struct VideoProject {
    let duration: Double
    var objects: [VideoObject] = []
    var segments: [Segment] = []
    var mutes: [AudioMute] = []
    var audioSplits: [AudioSplit] = []
    var speeds: [SpeedRegion] = []
    var audioFadeIn = 0.0            // fades of the whole mix at the start and end of the exported video
    var audioFadeOut = 0.0
    var audioRoles: [AudioRole] = []      // the audio tracks in the file, in order
    var trimStart = 0.0
    var trimEnd: Double

    init(duration: Double) {
        self.duration = duration
        trimEnd = duration
        segments = [Segment(start: 0, end: duration)]
    }

    /// The removed clips, as time ranges (what the preview skips and the export leaves out).
    /// Assigning replaces the clips with a split at each cut's edges.
    var cuts: [Cut] {
        get { segments.filter { $0.removed }.map { Cut(id: $0.id, start: $0.start, end: $0.end) } }
        set {
            var out: [Segment] = []
            var cursor = 0.0
            for c in newValue.sorted(by: { $0.start < $1.start }) {
                let s = max(c.start, cursor), e = min(c.end, duration)
                guard e > s else { continue }
                if s > cursor { out.append(Segment(start: cursor, end: s)) }
                out.append(Segment(id: c.id, start: s, end: e, removed: true))
                cursor = e
            }
            if cursor < duration { out.append(Segment(start: cursor, end: duration)) }
            segments = out.isEmpty ? [Segment(start: 0, end: duration)] : out
        }
    }

    var isTrimmed: Bool { trimStart > 0.05 || trimEnd < duration - 0.05 }
    /// Annotations and cut-outs need the video re-encoded; plain trimming doesn't.
    var needsRender: Bool { !objects.isEmpty || !cuts.isEmpty || !speeds.isEmpty }

    /// The parts of the source that survive the trim and cuts, in order.
    func keptSegments() -> [(start: Double, end: Double)] {
        var out: [(Double, Double)] = []
        var cursor = trimStart
        for c in cuts.sorted(by: { $0.start < $1.start }) {
            let cs = max(c.start, trimStart), ce = min(c.end, trimEnd)
            guard ce > cs else { continue }
            if cs > cursor { out.append((cursor, cs)) }
            cursor = max(cursor, ce)
        }
        if cursor < trimEnd { out.append((cursor, trimEnd)) }
        return out.filter { $0.1 - $0.0 > 0.04 }.map { (start: $0.0, end: $0.1) }
    }

    /// A stretch of the output: part of the source played at one rate, starting at `compStart` on the exported timeline.
    struct Piece {
        let srcStart: Double
        let srcEnd: Double
        let rate: Double
        let compStart: Double
        var compLength: Double { (srcEnd - srcStart) / rate }
        var compEnd: Double { compStart + compLength }
    }

    /// Speed regions clipped to the trim, in order, with overlaps resolved (an earlier region wins).
    private func normalizedSpeeds() -> [SpeedRegion] {
        var out: [SpeedRegion] = []
        var cursor = trimStart
        for r in speeds.sorted(by: { $0.start < $1.start }) {
            let s = max(r.start, cursor, trimStart), e = min(r.end, trimEnd)
            guard e - s > 0.05 else { continue }
            out.append(SpeedRegion(id: r.id, start: s, end: e, rate: min(4, max(0.25, r.rate))))
            cursor = e
        }
        return out
    }

    /// What the export plays, in order: the kept parts of the source (after trim and cuts), split wherever the speed changes.
    func pieces() -> [Piece] {
        let regions = normalizedSpeeds()
        var out: [Piece] = []
        var comp = 0.0
        func add(_ a: Double, _ b: Double, _ rate: Double) {
            guard b - a > 0.001 else { return }
            let piece = Piece(srcStart: a, srcEnd: b, rate: rate, compStart: comp)
            out.append(piece)
            comp = piece.compEnd
        }
        for seg in keptSegments() {
            var cursor = seg.start
            for r in regions where r.end > seg.start && r.start < seg.end {
                let s = max(r.start, seg.start), e = min(r.end, seg.end)
                add(cursor, s, 1)
                add(s, e, r.rate)
                cursor = e
            }
            add(cursor, seg.end, 1)
        }
        return out
    }

    var outputDuration: Double { pieces().last?.compEnd ?? 0 }

    /// Position on the exported timeline for a source time (a moment inside a cut maps to where the cut closes).
    func outputTime(forSource t: Double) -> Double {
        var last = 0.0
        for p in pieces() {
            if t <= p.srcStart { return p.compStart }
            if t < p.srcEnd { return p.compStart + (t - p.srcStart) / p.rate }
            last = p.compEnd
        }
        return last
    }

    /// Speed at a source time (1 outside every region), used by the preview.
    func rate(at t: Double) -> Double {
        normalizedSpeeds().first { t >= $0.start && t < $0.end }?.rate ?? 1
    }

    /// Where the camera is at time `t` (source time): nil means the whole frame. A zoom object eases from the full frame to its
    /// region over `fadeIn` seconds and back over `fadeOut`.
    func zoomRect(at t: Double, videoSize: CGSize) -> CGRect? {
        guard let z = objects.last(where: { $0.kind == .zoom && $0.isVisible(at: t) }) else { return nil }
        let g = z.geometry(at: t)
        let target = AnnotationRenderer.rect(g.p1, g.p2)
        let full = CGRect(origin: .zero, size: videoSize)
        var k = 1.0
        if z.fadeIn > 0 { k = min(k, (t - z.start) / z.fadeIn) }
        if z.fadeOut > 0 { k = min(k, (z.end - t) / z.fadeOut) }
        k = min(1, max(0, k))
        k = k * k * (3 - 2 * k)                           // smoothstep: eases in and out
        func lerp(_ a: CGFloat, _ b: CGFloat) -> CGFloat { a + (b - a) * CGFloat(k) }
        return CGRect(x: lerp(full.minX, target.minX), y: lerp(full.minY, target.minY),
                      width: lerp(full.width, target.width), height: lerp(full.height, target.height))
    }

    /// Several audio tracks must be mixed into one for upload (players only use the first), and mutes are baked in.
    var needsAudioMix: Bool { audioRoles.count > 1 || !mutes.isEmpty || audioFadeIn > 0 || audioFadeOut > 0 }

    /// Muted time ranges for a track, merged and sorted. `inOutput` maps them onto the exported timeline (after cuts and trim).
    func mutedRanges(for role: AudioRole, inOutput: Bool) -> [(start: Double, end: Double)] {
        let raw = mutes.filter { $0.role == role && $0.end > $0.start }.sorted { $0.start < $1.start }
        var merged: [(start: Double, end: Double)] = []
        for m in raw {
            if let last = merged.last, m.start <= last.end { merged[merged.count - 1].end = max(last.end, m.end) }
            else { merged.append((m.start, m.end)) }
        }
        guard inOutput else { return merged }
        var out: [(start: Double, end: Double)] = []
        for piece in pieces() {
            for m in merged {
                let a = max(m.start, piece.srcStart), b = min(m.end, piece.srcEnd)
                if b > a { out.append((piece.compStart + (a - piece.srcStart) / piece.rate, piece.compStart + (b - piece.srcStart) / piece.rate)) }
            }
        }
        return out
    }

    func isCut(_ t: Double) -> Cut? { cuts.first { t >= $0.start && t < $0.end } }
}

/// Maps a time in the exported video back to the source, so annotations (defined on source time) line up after cuts and speed changes.
struct TimeMap {
    private let pieces: [VideoProject.Piece]

    init(pieces: [VideoProject.Piece]) { self.pieces = pieces }

    func sourceTime(_ comp: Double) -> Double {
        for p in pieces.reversed() where comp >= p.compStart - 0.0005 {
            return p.srcStart + min(comp - p.compStart, p.compLength) * p.rate
        }
        return pieces.first?.srcStart ?? comp
    }
}
