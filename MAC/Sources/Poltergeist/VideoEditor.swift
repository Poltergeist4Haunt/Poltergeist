import AppKit
import SwiftUI
import AVKit
import AVFoundation

// MARK: - Model

final class VideoEditorModel: ObservableObject {
    let url: URL
    let videoSize: CGSize
    let player: AVPlayer
    private let audioTracks: [(track: AVAssetTrack, role: AudioRole)]
    private let asset: AVURLAsset
    private var zoomSignature = ""
    private(set) var zoomPreviewOn = false

    /// Mutes are applied to the preview live, so you hear exactly what will be exported.
    @Published var project: VideoProject { didSet { updateAudioMix(); updateZoomPreview() } }
    @Published var playhead = 0.0
    @Published var playing = false
    @Published var tool: EditorTool?            // nil = select / move
    @Published var colorIndex = 0               // -1 = custom colour
    @Published var customColor = NSColor(hex: 0x8a5cff)
    @Published var level = 2
    @Published var animate = true               // dragging records keyframes
    @Published var blurSoft = false             // style for new blur boxes
    @Published var selection: UUID? {
        didSet {
            if selection != nil { selectedSpeed = nil; selectedSegment = nil; selectedAudio = nil } else { selectedKeyframe = nil }
            updateZoomPreview()                      // a selected zoom shows the whole frame so its box can be edited
        }
    }
    @Published var selectedKeyframe: Int?
    /// A short message shown in the bottom bar (for example why a split didn't happen).
    @Published var notice: String?
    private var noticeToken = 0

    func flash(_ text: String) {
        notice = text
        noticeToken += 1
        let token = noticeToken
        DispatchQueue.main.asyncAfter(deadline: .now() + 3.5) { [weak self] in
            if self?.noticeToken == token { self?.notice = nil }
        }
    }
    @Published var selectedSpeed: UUID? { didSet { if selectedSpeed != nil { selection = nil; selectedSegment = nil; selectedAudio = nil } } }
    @Published var selectedSegment: UUID? { didSet { if selectedSegment != nil { selection = nil; selectedSpeed = nil; selectedAudio = nil } } }
    /// A part of a sound track picked in the timeline (split it, mute it).
    @Published var selectedAudio: AudioSelection? { didSet { if selectedAudio != nil { selection = nil; selectedSpeed = nil; selectedSegment = nil } } }
    /// Loudness of each sound track over time (one value per `WaveformReader.bucket` seconds), for drawing the lanes.
    @Published var waves: [AudioRole: [Float]] = [:]

    private var undoStack: [VideoProject] = []
    private var observer: Any?

    /// One video frame, in seconds (from the recording's frame rate).
    let frameDuration: Double

    init(asset: AVURLAsset, url: URL, duration: Double, videoSize: CGSize,
         audioTracks: [(track: AVAssetTrack, role: AudioRole)], frameRate: Double = 30) {
        frameDuration = 1 / min(max(frameRate, 1), 120)
        self.url = url
        self.videoSize = videoSize
        self.audioTracks = audioTracks
        self.asset = asset
        var p = VideoProject(duration: duration)
        p.audioRoles = audioTracks.map { $0.role }
        project = p
        let item = AVPlayerItem(asset: asset)                         // same asset the audio tracks came from
        item.audioTimePitchAlgorithm = .spectral                      // sped-up / slowed sections keep their pitch
        player = AVPlayer(playerItem: item)
        player.actionAtItemEnd = .pause
        observer = player.addPeriodicTimeObserver(forInterval: CMTime(value: 1, timescale: 30), queue: .main) { [weak self] t in
            self?.tick(t.seconds)
        }
        loadWaveforms()
    }

    private func loadWaveforms() {
        let tracks = audioTracks, asset = self.asset
        Task { @MainActor [weak self] in
            for (track, role) in tracks {
                if self?.waves[role] != nil { continue }
                if let peaks = await WaveformReader.peaks(of: track, in: asset) { self?.waves[role] = peaks }
            }
        }
    }

    /// The part of a sound track between its split points that contains `t`.
    func audioPart(_ role: AudioRole, at t: Double) -> AudioSelection {
        var a = 0.0, b = project.duration
        for c in project.audioSplits.filter({ $0.role == role }).map({ $0.time }).sorted() {
            if c <= t { a = c } else { b = c; break }
        }
        return AudioSelection(role: role, start: a, end: b)
    }

    /// Mutes the picked part of a sound track (delete the mute to bring it back).
    func muteSelectedAudio() {
        guard let a = selectedAudio else { return }
        beginChange()
        let mute = AudioMute(role: a.role, start: a.start, end: a.end)
        project.mutes.append(mute)
        selectedAudio = nil
    }

    deinit { if let observer { player.removeTimeObserver(observer) } }

    private func updateAudioMix() {
        guard !audioTracks.isEmpty, let item = player.currentItem else { return }
        let snapshot = project
        item.audioMix = MuteMix.make(tracks: audioTracks, origin: snapshot.trimStart, length: snapshot.trimEnd - snapshot.trimStart,
                                     fadeIn: snapshot.audioFadeIn, fadeOut: snapshot.audioFadeOut) {
            snapshot.mutedRanges(for: $0, inOutput: false)
        }
    }

    // MARK: Zoom preview

    /// While no zoom is selected, the preview shows what the camera will do. (With one selected you see the whole frame so
    /// its box can be dragged.) Rebuilt only when a zoom changes.
    func updateZoomPreview() {
        guard let item = player.currentItem else { return }
        let zooms = project.objects.filter { $0.kind == .zoom }
        let want = !zooms.isEmpty && selectedObject?.kind != .zoom
        let signature = want ? zooms.map { "\($0.id)|\($0.start)|\($0.end)|\($0.fadeIn)|\($0.fadeOut)|\($0.keyframes)" }.joined() : ""
        guard signature != zoomSignature else { return }
        zoomSignature = signature
        zoomPreviewOn = want
        if !want {
            item.videoComposition = nil
        } else {
            let snapshot = project, size = videoSize
            let vc = AVMutableVideoComposition(asset: asset) { request in
                guard let r = snapshot.zoomRect(at: request.compositionTime.seconds, videoSize: size)?
                    .intersection(CGRect(origin: .zero, size: size)), r.width > 8, r.height > 8 else {
                    request.finish(with: request.sourceImage, context: nil); return
                }
                let ci = CGRect(x: r.minX, y: size.height - r.maxY, width: r.width, height: r.height)
                let out = request.sourceImage.cropped(to: ci)
                    .transformed(by: CGAffineTransform(translationX: -ci.minX, y: -ci.minY))
                    .applyingFilter("CILanczosScaleTransform", parameters: [
                        kCIInputScaleKey: size.height / r.height, kCIInputAspectRatioKey: (size.width / r.width) / (size.height / r.height)])
                request.finish(with: out.cropped(to: request.sourceImage.extent), context: nil)
            }
            vc.renderSize = size
            item.videoComposition = vc
        }
        player.seek(to: player.currentTime(), toleranceBefore: .zero, toleranceAfter: .zero)      // redraw the current frame
    }

    /// The camera region currently applied to the preview, or nil when it shows the whole frame.
    var previewZoom: CGRect? { zoomPreviewOn ? project.zoomRect(at: playhead, videoSize: videoSize) : nil }

    // MARK: Speed, zoom, fades

    func addSpeed() {
        beginChange()
        var start = min(playhead, max(project.trimStart, project.trimEnd - 0.5))
        var end = min(project.trimEnd, start + 2)
        if let sid = selectedSegment, let (_, seg) = segment(sid), !seg.removed {        // a selected clip: speed up that whole clip
            start = max(seg.start, project.trimStart)
            end = min(seg.end, project.trimEnd)
        }
        let r = SpeedRegion(start: start, end: end, rate: 2)
        project.speeds.append(r)
        selectedSpeed = r.id
    }

    func removeSpeed(_ id: UUID) {
        beginChange()
        project.speeds.removeAll { $0.id == id }
        if selectedSpeed == id { selectedSpeed = nil }
    }

    func setSpeedRate(_ id: UUID, _ rate: Double) {
        beginChange()
        if let i = project.speeds.firstIndex(where: { $0.id == id }) { project.speeds[i].rate = rate }
    }

    /// A camera move at the playhead: starts as a 2× zoom on the middle. Drag its box onto the detail; add keyframes to pan.
    func addZoom() {
        beginChange()
        let v = videoSize
        let w = v.width / 2, h = v.height / 2
        var o = VideoObject(kind: .zoom, color: NSColor(hex: 0xffb020), level: 2, text: "", start: playhead,
                            end: min(project.duration, playhead + 3),
                            keyframes: [Keyframe(time: playhead, p1: CGPoint(x: (v.width - w) / 2, y: (v.height - h) / 2),
                                                 p2: CGPoint(x: (v.width + w) / 2, y: (v.height + h) / 2))])
        o.fadeIn = 0.4
        o.fadeOut = 0.4
        project.objects.append(o)
        selection = o.id
        selectedKeyframe = nil
        tool = nil
    }

    func setFade(_ id: UUID, fadeIn: Double? = nil, fadeOut: Double? = nil) {
        mutate(id) { if let f = fadeIn { $0.fadeIn = f }; if let f = fadeOut { $0.fadeOut = f } }
    }

    func addMute(_ role: AudioRole) {
        beginChange()
        let start = min(playhead, max(project.trimStart, project.trimEnd - 0.3))
        project.mutes.append(AudioMute(role: role, start: start, end: min(project.trimEnd, start + 1)))
    }

    func removeMute(_ id: UUID) {
        beginChange()
        project.mutes.removeAll { $0.id == id }
    }

    var color: NSColor { colorIndex >= 0 ? EditorModel.palette[colorIndex] : customColor }
    var selectedObject: VideoObject? { project.objects.first { $0.id == selection } }

    // MARK: Playback (the preview skips cut sections and stops at the end handle)

    private func tick(_ t: Double) {
        if playing {
            if let cut = project.isCut(t) { seek(to: cut.end); return }
            if t >= project.trimEnd - 0.02 { pause(); seek(to: project.trimStart); return }
            let want = Float(project.rate(at: t))                       // speed regions play at their rate
            if abs(player.rate - want) > 0.01 { player.rate = want }
        }
        playhead = t
        if playing { syncOverlayAudio() }
    }

    func seek(to t: Double) {
        // the very last instant has no frame to show; stay just inside it
        let clamped = min(max(0, t), max(0, project.duration - 0.03))
        player.seek(to: CMTime(seconds: clamped, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        playhead = clamped
    }

    /// Moves the playhead by whole frames (negative = back). Pauses playback; lands exactly on a frame.
    func stepFrames(_ n: Int) {
        pause()
        let current = (playhead / frameDuration).rounded()                 // snap to the frame grid first
        seek(to: (current + Double(n)) * frameDuration)
    }

    func togglePlay() {
        if playing { pause(); return }
        if playhead < project.trimStart || playhead >= project.trimEnd - 0.05 { seek(to: project.trimStart) }
        if let cut = project.isCut(playhead) { seek(to: cut.end) }
        player.play()
        playing = true
    }

    func pause() { player.pause(); playing = false; stopOverlayAudio() }

    // MARK: Editing

    /// Call once before a change so it can be undone.
    func beginChange() {
        undoStack.append(project)
        if undoStack.count > 60 { undoStack.removeFirst() }
    }

    func undo() {
        guard let last = undoStack.popLast() else { return }
        project = last
        if selectedObject == nil { selection = nil; selectedKeyframe = nil }
    }

    var canUndo: Bool { !undoStack.isEmpty }

    func mutate(_ id: UUID, _ change: (inout VideoObject) -> Void) {
        guard let i = project.objects.firstIndex(where: { $0.id == id }) else { return }
        change(&project.objects[i])
    }

    func replace(_ o: VideoObject) {
        guard let i = project.objects.firstIndex(where: { $0.id == o.id }) else { return }
        project.objects[i] = o
    }

    /// New object at the playhead, visible for 3 seconds (or to the end).
    @discardableResult
    func addObject(kind: Annotation.Kind, p1: CGPoint, p2: CGPoint, text: String = "",
                   path: [CGPoint] = [], keepTool: Bool = false) -> VideoObject {
        beginChange()
        var o = VideoObject(kind: kind, color: kind == .blur ? .black : color, level: level, text: text,
                            start: playhead, end: min(project.duration, playhead + 3),
                            keyframes: [Keyframe(time: playhead, p1: p1, p2: p2)])
        if kind == .blur { o.softBlur = blurSoft }
        o.path = path
        project.objects.append(o)
        selection = keepTool ? nil : o.id          // drawing tools stay active so you can keep drawing
        selectedKeyframe = nil
        if !keepTool { tool = nil }               // back to select, ready to move/adjust it
        return o
    }

    func deleteSelected() {
        if selectedAudio != nil { muteSelectedAudio(); return }
        if selectedObject == nil, let sid = selectedSegment { deleteSegment(sid); return }
        guard let o = selectedObject else { return }
        beginChange()
        if let k = selectedKeyframe, o.keyframes.count > 1, o.keyframes.indices.contains(k) {
            mutate(o.id) { $0.keyframes.remove(at: k) }
            selectedKeyframe = nil
        } else {
            project.objects.removeAll { $0.id == o.id }
            selection = nil
            selectedKeyframe = nil
        }
    }

    func addKeyframeAtPlayhead() {
        guard let o = selectedObject else { return }
        beginChange()
        let g = o.geometry(at: playhead)
        mutate(o.id) { $0.setKeyframe(at: playhead, p1: g.p1, p2: g.p2) }
        selectedKeyframe = selectedObject?.keyframes.firstIndex { abs($0.time - playhead) < 0.05 }
    }

    // MARK: Splitting the recording into clips

    /// Splits whatever is selected at the playhead: a layer, a speed block, or (with nothing selected) the video clip under it.
    /// Each half can then be moved, deleted, restyled or retimed on its own.
    func splitAtPlayhead() {
        let t = playhead
        if let a = selectedAudio {
            guard t > a.start + 0.1, t < a.end - 0.1 else { flash("Move the playhead inside the picked part of the sound to split it."); return }
            beginChange()
            project.audioSplits.append(AudioSplit(role: a.role, time: t))
            selectedAudio = audioPart(a.role, at: t)
            return
        }
        if let o = selectedObject {
            guard t > o.start + 0.1, t < o.end - 0.1 else { flash("Move the playhead onto the \(label(for: o).lowercased()) layer to split it."); return }
            splitObject(o, at: t)
            return
        }
        if let sid = selectedSpeed, let r = project.speeds.first(where: { $0.id == sid }) {
            guard t > r.start + 0.1, t < r.end - 0.1 else { flash("Move the playhead inside the speed block to split it."); return }
            beginChange()
            var first = r
            first.end = t
            let second = SpeedRegion(start: t, end: r.end, rate: r.rate)
            if let i = project.speeds.firstIndex(where: { $0.id == sid }) { project.speeds.replaceSubrange(i...i, with: [first, second]) }
            selectedSpeed = second.id
            return
        }
        guard let i = project.segments.firstIndex(where: { t > $0.start + 0.1 && t < $0.end - 0.1 }) else { return }
        beginChange()
        let s = project.segments[i]
        var first = s
        first.end = t
        let second = Segment(start: t, end: s.end, removed: s.removed)
        project.segments.replaceSubrange(i...i, with: [first, second])
        selectedSegment = second.id
    }

    /// Two layers that meet at `t` and look and move exactly like the original. The second half keeps playing an imported
    /// clip from where the first half stopped.
    private func splitObject(_ o: VideoObject, at t: Double) {
        beginChange()
        let g = o.geometry(at: t)
        let here = Keyframe(time: t, p1: g.p1, p2: g.p2)
        var first = o, second = o
        second.id = UUID()
        first.end = t
        second.start = t
        first.keyframes = o.keyframes.filter { $0.time < t - 0.001 } + [here]
        second.keyframes = [here] + o.keyframes.filter { $0.time > t + 0.001 }
        first.fadeOut = 0                                  // the fade in stays on the first half, the fade out on the second
        second.fadeIn = 0
        second.mediaOffset = o.mediaOffset + (t - o.start)
        if let i = project.objects.firstIndex(where: { $0.id == o.id }) { project.objects.replaceSubrange(i...i, with: [first, second]) }
        selection = second.id
        selectedKeyframe = nil
    }

    func segment(_ id: UUID) -> (index: Int, segment: Segment)? {
        project.segments.firstIndex(where: { $0.id == id }).map { ($0, project.segments[$0]) }
    }

    /// Removes a clip from the preview and the export. The last remaining clip can't be removed.
    func deleteSegment(_ id: UUID) {
        guard let (i, seg) = segment(id), !seg.removed else { return }
        guard project.segments.filter({ !$0.removed }).count > 1 else { return }
        beginChange()
        project.segments[i].removed = true
    }

    func restoreSegment(_ id: UUID) {
        guard let (i, seg) = segment(id), seg.removed else { return }
        beginChange()
        project.segments[i].removed = false
    }

    /// Removes the split between this clip and the one before it.
    func joinWithPrevious(_ id: UUID) {
        guard let (i, seg) = segment(id), i > 0 else { return }
        beginChange()
        var prev = project.segments[i - 1]
        prev.end = seg.end
        prev.removed = prev.removed && seg.removed             // keep the footage unless both halves were removed
        project.segments.replaceSubrange((i - 1)...i, with: [prev])
        selectedSegment = prev.id
    }

    /// Drags the split between clip `index - 1` and `index`.
    func moveBoundary(_ index: Int, to t: Double) {
        guard index >= 1, index < project.segments.count else { return }
        let lo = project.segments[index - 1].start + 0.1, hi = project.segments[index].end - 0.1
        guard lo < hi else { return }
        let c = min(max(t, lo), hi)
        project.segments[index - 1].end = c
        project.segments[index].start = c
    }

    func label(for o: VideoObject) -> String {
        let same = project.objects.filter { $0.kind == o.kind }
        let n = (same.firstIndex { $0.id == o.id } ?? 0) + 1
        let name: String
        switch o.kind {
        case .arrow: name = "Arrow"
        case .box: name = "Box"
        case .circle: name = "Circle"
        case .text: name = "Text"
        case .blur: name = "Blur"
        case .zoom: name = "Zoom"
        case .pen: name = "Pen"
        case .marker: name = "Marker"
        case .curve: name = "Curve arrow"
        case .media:
            switch o.media?.kind {
            case .gif?: name = "GIF"
            case .video?: name = "Video"
            default: name = "Image"
            }
        }
        return same.count > 1 ? "\(name) \(n)" : name
    }

    func icon(for kind: Annotation.Kind) -> String {
        switch kind {
        case .arrow: return "arrow.up.right"
        case .box: return "square"
        case .circle: return "circle"
        case .text: return "textformat"
        case .blur: return "square.grid.3x3.fill"
        case .media: return "photo"
        case .zoom: return "plus.magnifyingglass"
        case .pen: return "pencil.tip"
        case .marker: return "highlighter"
        case .curve: return "point.topleft.down.curvedto.point.bottomright.up"
        }
    }

    func icon(for o: VideoObject) -> String {
        guard o.kind == .media else { return icon(for: o.kind) }
        switch o.media?.kind {
        case .gif?: return "photo.stack"
        case .video?: return "film"
        default: return "photo"
        }
    }

    // MARK: Imported images, GIFs and videos

    /// Places a clip over the recording at the playhead: at most 40% of the frame, centred (or where it was dropped).
    /// GIFs loop for at least 3 s; videos play once from the moment they appear.
    func addMedia(_ clip: MediaClip, center: CGPoint? = nil) {
        beginChange()
        let v = videoSize
        let k = min(1, v.width * 0.4 / clip.pixelSize.width, v.height * 0.4 / clip.pixelSize.height)
        let w = clip.pixelSize.width * k, h = clip.pixelSize.height * k
        let c = center ?? CGPoint(x: v.width / 2, y: v.height / 2)
        let x = min(max(c.x - w / 2, 0), max(0, v.width - w)), y = min(max(c.y - h / 2, 0), max(0, v.height - h))
        let length: Double
        switch clip.kind {
        case .video: length = clip.duration
        case .gif: length = max(3, clip.duration)
        case .image: length = 3
        }
        var o = VideoObject(kind: .media, color: NSColor(hex: 0x5ac8fa), level: 2, text: clip.name, start: playhead,
                            end: min(project.duration, playhead + length),
                            keyframes: [Keyframe(time: playhead, p1: CGPoint(x: x, y: y), p2: CGPoint(x: x + w, y: y + h))])
        o.media = clip
        project.objects.append(o)
        selection = o.id
        selectedKeyframe = nil
        tool = nil
    }

    func setClipVolume(_ v: Double) {
        guard let o = selectedObject else { return }
        mutate(o.id) { $0.volume = min(max(v, 0), 1) }
        syncOverlayAudio()
    }

    func toggleClipMute() {
        guard let o = selectedObject else { return }
        beginChange()
        mutate(o.id) { $0.audioMuted.toggle() }
        syncOverlayAudio()
    }

    // The audio of imported videos is previewed with one extra player per clip, kept in step with the main player.
    private var overlayPlayers: [UUID: AVPlayer] = [:]

    func syncOverlayAudio() {
        for o in project.objects where o.kind == .media {
            guard let clip = o.media, let asset = clip.audioAsset else { continue }
            let player = overlayPlayers[o.id] ?? {
                let p = AVPlayer(playerItem: AVPlayerItem(asset: asset))
                overlayPlayers[o.id] = p
                return p
            }()
            let sinceStart = playhead - o.start
            let local = sinceStart + o.mediaOffset
            let audible = playing && !o.audioMuted && o.volume > 0.001 && sinceStart >= 0 && playhead < o.end && local < clip.duration
            let wholeFade = project.audioFadeIn > 0 ? min(1, max(0, (playhead - project.trimStart) / project.audioFadeIn)) : 1
            let wholeFadeOut = project.audioFadeOut > 0 ? min(1, max(0, (project.trimEnd - playhead) / project.audioFadeOut)) : 1
            player.volume = Float(o.volume * Double(o.opacity(at: playhead)) * wholeFade * wholeFadeOut)
            if audible {
                let drift = abs(player.currentTime().seconds - local)
                if player.rate == 0 || drift > 0.3 {
                    player.seek(to: CMTime(seconds: local, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
                    player.play()
                }
                player.rate = Float(project.rate(at: playhead))
            } else if player.rate != 0 {
                player.pause()
            }
        }
        for id in overlayPlayers.keys where !project.objects.contains(where: { $0.id == id }) {
            overlayPlayers[id]?.pause()
            overlayPlayers[id] = nil
        }
    }

    func stopOverlayAudio() { overlayPlayers.values.forEach { $0.pause() } }
}

// MARK: - Preview overlay (draw, select, drag)

final class VideoOverlayView: NSView, NSTextFieldDelegate {
    let model: VideoEditorModel
    private let renderer: AnnotationRenderer
    private var draft: Annotation?
    private var textField: NSTextField?
    private var textPoint = CGPoint.zero
    private var editingID: UUID?            // the existing text object being edited (hidden meanwhile)
    private var freePoints: [CGPoint] = []
    private var freeKind: Annotation.Kind?
    private var dragTarget: HandleTarget?
    private var dragOriginal: VideoObject?
    private var dragStart = CGPoint.zero
    private var dragBegan = false

    init(model: VideoEditorModel) {
        self.model = model
        renderer = AnnotationRenderer(size: model.videoSize)
        super.init(frame: .zero)
    }
    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func viewDidMoveToWindow() {
        registerForDraggedTypes([.fileURL, .tiff, .png])
        DispatchQueue.main.async { [weak self] in self?.window?.makeFirstResponder(self) }
    }
    override func resetCursorRects() { addCursorRect(bounds, cursor: model.tool == nil ? .arrow : (model.tool == .text ? .iBeam : .crosshair)) }

    // The video is shown aspect-fit; map between view points and video pixels.
    /// `rect` is where the picture is; `scale` and `origin` map video pixels to the view. While the preview is zoomed they
    /// map through the camera region, so drawing and clicking still line up with what you see.
    private var fit: (rect: CGRect, scale: CGFloat, origin: CGPoint) {
        let v = model.videoSize
        let k = min(bounds.width / v.width, bounds.height / v.height)
        let w = v.width * k, h = v.height * k
        let rect = CGRect(x: (bounds.width - w) / 2, y: (bounds.height - h) / 2, width: w, height: h)
        if let z = model.previewZoom, z.width > 8 {
            let zk = k * v.width / z.width
            return (rect, zk, CGPoint(x: rect.minX - z.minX * zk, y: rect.minY - z.minY * zk))
        }
        return (rect, k, rect.origin)
    }
    private func toVideo(_ p: NSPoint) -> CGPoint {
        let f = fit
        return CGPoint(x: min(max((p.x - f.origin.x) / f.scale, 0), model.videoSize.width),
                       y: min(max((p.y - f.origin.y) / f.scale, 0), model.videoSize.height))
    }
    private func toView(_ p: CGPoint) -> CGPoint {
        let f = fit
        return CGPoint(x: f.origin.x + p.x * f.scale, y: f.origin.y + p.y * f.scale)
    }

    // MARK: Drawing

    override func draw(_ dirtyRect: NSRect) {
        let f = fit, now = model.playhead
        NSGraphicsContext.saveGraphicsState()
        NSBezierPath(rect: f.rect).addClip()
        let t = NSAffineTransform()
        t.translateX(by: f.origin.x, yBy: f.origin.y)
        t.scale(by: f.scale)
        t.concat()
        for o in model.project.objects where o.isVisible(at: now) && o.id != editingID {
            let a = o.annotation(at: now) { [weak self] in self?.needsDisplay = true }   // video layers decode in the background
            if o.kind == .zoom {
                if o.id == model.selection { drawZoomRegion(a) }                          // the camera box, shown only while editing it
            } else if o.kind == .blur { drawBlurPlaceholder(a) } else { renderer.draw(a) }
        }
        if let d = draft { if d.kind == .blur { drawBlurPlaceholder(d) } else { renderer.draw(d) } }
        NSGraphicsContext.restoreGraphicsState()

        if let o = model.selectedObject, o.isVisible(at: now), o.id != editingID { drawSelection(o, at: now) }
    }

    /// Dims everything outside the camera region so it's clear what the zoom will show.
    private func drawZoomRegion(_ a: Annotation) {
        let r = AnnotationRenderer.rect(a.p1, a.p2)
        let dim = NSBezierPath(rect: CGRect(origin: .zero, size: model.videoSize))
        dim.append(NSBezierPath(rect: r))
        dim.windingRule = .evenOdd
        NSColor.black.withAlphaComponent(0.5).setFill()
        dim.fill()
        let border = NSBezierPath(rect: r)
        border.lineWidth = 2 * renderer.unit
        border.setLineDash([8 * renderer.unit, 5 * renderer.unit], count: 2, phase: 0)
        NSColor(hex: 0xffb020).setStroke()
        border.stroke()
        NSAttributedString(string: "ZOOM", attributes: [
            .font: NSFont.systemFont(ofSize: 6 * renderer.unit, weight: .bold), .foregroundColor: NSColor(hex: 0xffb020),
        ]).draw(at: CGPoint(x: r.minX + 4 * renderer.unit, y: r.minY + 4 * renderer.unit))
    }

    /// A stand-in while editing; the export pixelates the real frames.
    private func drawBlurPlaceholder(_ a: Annotation) {
        let r = AnnotationRenderer.rect(a.p1, a.p2)
        NSColor.white.withAlphaComponent(0.16).setFill()
        r.fill()
        NSColor.white.withAlphaComponent(0.8).setStroke()
        let p = NSBezierPath(rect: r)
        p.lineWidth = renderer.unit
        p.setLineDash([6 * renderer.unit, 4 * renderer.unit], count: 2, phase: 0)
        p.stroke()
        NSAttributedString(string: a.soft ? "SOFT BLUR" : "PIXELS", attributes: [
            .font: NSFont.systemFont(ofSize: 5 * renderer.unit, weight: .bold),
            .foregroundColor: NSColor.white.withAlphaComponent(0.9),
        ]).draw(at: CGPoint(x: r.minX + 3 * renderer.unit, y: r.minY + 3 * renderer.unit))
    }

    private func bounds(of o: VideoObject, at t: Double) -> CGRect {
        let g = o.geometry(at: t)
        if o.kind == .text {
            let size = NSAttributedString(string: o.text, attributes: [.font: renderer.font(level: o.level)]).size()
            return CGRect(origin: g.p1, size: size)
        }
        return AnnotationRenderer.rect(g.p1, g.p2)
    }

    private func drawSelection(_ o: VideoObject, at t: Double) {
        let g = o.geometry(at: t)
        let b = bounds(of: o, at: t)
        let tl = toView(CGPoint(x: b.minX, y: b.minY)), br = toView(CGPoint(x: b.maxX, y: b.maxY))
        let box = NSBezierPath(rect: NSRect(x: tl.x, y: tl.y, width: br.x - tl.x, height: br.y - tl.y).insetBy(dx: -3, dy: -3))
        NSColor(Theme.accent).setStroke()
        box.lineWidth = 1.5
        box.setLineDash([5, 3], count: 2, phase: 0)
        box.stroke()
        guard o.kind != .text else { return }
        for p in [g.p1, g.p2] {
            let v = toView(p)
            let h = NSBezierPath(rect: NSRect(x: v.x - 4.5, y: v.y - 4.5, width: 9, height: 9))
            NSColor.white.setFill()
            h.fill()
            NSColor(Theme.accent).setStroke()
            h.lineWidth = 1.5
            h.setLineDash([], count: 0, phase: 0)
            h.stroke()
        }
    }

    // MARK: Hit testing

    private func hits(_ o: VideoObject, _ p: CGPoint, tol: CGFloat, at t: Double) -> Bool {
        if !o.path.isEmpty {
            let a = o.annotation(at: t)
            return AnnotationRenderer.nearPath(a, p, tolerance: tol + renderer.strokeWidth(for: a) / 2)
        }
        let g = o.geometry(at: t)
        if o.kind == .arrow {
            let dx = g.p2.x - g.p1.x, dy = g.p2.y - g.p1.y
            let len2 = dx * dx + dy * dy
            let f = len2 > 0 ? min(1, max(0, ((p.x - g.p1.x) * dx + (p.y - g.p1.y) * dy) / len2)) : 0
            return hypot(p.x - (g.p1.x + f * dx), p.y - (g.p1.y + f * dy)) <= tol + renderer.unit * CGFloat(1 + o.level)
        }
        return bounds(of: o, at: t).insetBy(dx: -tol, dy: -tol).contains(p)
    }

    // MARK: Mouse

    override func mouseDown(with event: NSEvent) {
        let viewPoint = convert(event.locationInWindow, from: nil)
        window?.makeFirstResponder(self)
        commitText()
        let p = toVideo(viewPoint)
        let now = model.playhead
        let tol = 9 / fit.scale

        // Double-click on text edits it, whatever tool is active.
        if event.clickCount == 2,
           let o = model.project.objects.last(where: { $0.kind == .text && $0.isVisible(at: now) && hits($0, p, tol: 6 / fit.scale, at: now) }) {
            model.selection = o.id
            model.selectedKeyframe = nil
            beginText(at: o.geometry(at: now).p1, viewPoint: toView(o.geometry(at: now).p1), existing: o)
            return
        }

        if let kind = model.tool {
            if kind == .text { beginText(at: p, viewPoint: viewPoint); return }
            if let fk = kind.freehand { freeKind = fk; freePoints = [p]; return }
            let k: Annotation.Kind = kind == .arrow ? .arrow : kind == .box ? .box : kind == .circle ? .circle : .blur
            draft = Annotation(kind: k, p1: p, p2: p, color: k == .blur ? .black : model.color, level: model.level)
            return
        }

        // Select tool: a handle of the selected object first, then any object under the cursor.
        if let o = model.selectedObject, o.isVisible(at: now), o.kind != .text {
            let g = o.geometry(at: now)
            if hypot(p.x - g.p1.x, p.y - g.p1.y) <= tol { startDrag(o, .p1, p); return }
            if hypot(p.x - g.p2.x, p.y - g.p2.y) <= tol { startDrag(o, .p2, p); return }
        }
        if let o = model.project.objects.last(where: {
            $0.isVisible(at: now) && ($0.kind != .zoom || $0.id == model.selection) && hits($0, p, tol: tol, at: now)
        }) {
            model.selection = o.id
            model.selectedKeyframe = nil
            startDrag(o, .body, p)
        } else {
            model.selection = nil
            model.selectedKeyframe = nil
        }
        needsDisplay = true
    }

    private func startDrag(_ o: VideoObject, _ target: HandleTarget, _ p: CGPoint) {
        dragOriginal = o
        dragTarget = target
        dragStart = p
        dragBegan = false
        model.selection = o.id
    }

    override func mouseDragged(with event: NSEvent) {
        let p = toVideo(convert(event.locationInWindow, from: nil))
        if let k = freeKind {
            if let last = freePoints.last, hypot(p.x - last.x, p.y - last.y) >= max(1, renderer.unit * 0.8) {
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
        guard let original = dragOriginal, let target = dragTarget else { return }
        let delta = CGSize(width: p.x - dragStart.x, height: p.y - dragStart.y)
        if !dragBegan {
            guard hypot(delta.width, delta.height) * fit.scale > 2 else { return }   // ignore tiny wobbles while clicking
            model.beginChange()
            dragBegan = true
        }
        // Always apply the total delta to the object as it was at mouse-down, so the drag doesn't accumulate keyframes.
        model.replace(original.moved(target, by: delta, at: model.playhead, animate: model.animate,
                                     videoAspect: model.videoSize.width / model.videoSize.height))
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        if let k = freeKind {
            freeKind = nil
            let p = toVideo(convert(event.locationInWindow, from: nil))
            if freePoints.last != p { freePoints.append(p) }
            draft = nil
            if let a = Annotation.freehand(k, points: freePoints, color: model.color, level: model.level),
               freePoints.count >= 3 || hypot(a.p2.x - a.p1.x, a.p2.y - a.p1.y) > 6 {
                model.addObject(kind: k, p1: a.p1, p2: a.p2, path: a.path, keepTool: true)
            }
            freePoints = []
            needsDisplay = true
            return
        }
        if let d = draft {
            draft = nil
            let p = toVideo(convert(event.locationInWindow, from: nil))
            if hypot(p.x - d.p1.x, p.y - d.p1.y) > 6 { model.addObject(kind: d.kind, p1: d.p1, p2: p) }
        }
        dragOriginal = nil
        dragTarget = nil
        if let o = model.selectedObject, dragBegan, let k = o.keyframes.firstIndex(where: { abs($0.time - model.playhead) < 0.05 }) {
            model.selectedKeyframe = k
        }
        dragBegan = false
        needsDisplay = true
    }

    // MARK: Keyboard

    override func keyDown(with event: NSEvent) {
        switch event.keyCode {
        case 51, 117: model.deleteSelected()                                         // delete
        case 49: model.togglePlay()                                                  // space
        case 53: model.tool = nil; model.selection = nil                             // esc
        case 123: stepKey(-1, event)                                                 // ←
        case 124: stepKey(1, event)                                                  // →
        case 43: model.stepFrames(-1)                                                // , and <
        case 47: model.stepFrames(1)                                                 // . and >
        default: super.keyDown(with: event)
        }
    }

    /// Arrow keys: one frame; with shift, one second.
    private func stepKey(_ direction: Int, _ event: NSEvent) {
        if event.modifierFlags.contains(.shift) { model.pause(); model.seek(to: model.playhead + Double(direction)) }
        else { model.stepFrames(direction) }
    }

    // MARK: Paste and drop

    /// ⌘V (through the Edit menu): a copied picture, or a copied image / GIF / video file, goes onto the recording.
    @objc func paste(_ sender: Any?) { importMedia(from: .general, at: nil) }

    private func importMedia(from pb: NSPasteboard, at point: CGPoint?) {
        Task { @MainActor in
            for clip in await MediaImport.clips(from: pb, allowVideo: true) { model.addMedia(clip, center: point) }
            needsDisplay = true
        }
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation { .copy }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        importMedia(from: sender.draggingPasteboard, at: toVideo(convert(sender.draggingLocation, from: nil)))
        return true
    }

    // MARK: Text

    private func beginText(at p: CGPoint, viewPoint: NSPoint, existing: VideoObject? = nil) {
        textPoint = p
        editingID = existing?.id
        let size = renderer.font(level: existing?.level ?? model.level).pointSize * fit.scale
        let field = NSTextField(frame: NSRect(x: viewPoint.x, y: viewPoint.y, width: max(160, bounds.maxX - viewPoint.x - 8), height: size * 1.5))
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
        needsDisplay = true
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
            if let o = model.project.objects.first(where: { $0.id == id }), o.text != text {
                if text.isEmpty { model.selection = id; model.deleteSelected() }
                else { model.beginChange(); model.mutate(id) { $0.text = text } }
            }
            editingID = nil
        } else if !text.isEmpty {
            model.addObject(kind: .text, p1: textPoint, p2: textPoint, text: text)
        }
        window?.makeFirstResponder(self)
        needsDisplay = true
    }
}

private struct OverlayRepresentable: NSViewRepresentable {
    @ObservedObject var model: VideoEditorModel

    func makeNSView(context: Context) -> VideoOverlayView { VideoOverlayView(model: model) }

    func updateNSView(_ view: VideoOverlayView, context: Context) {
        _ = model.project.objects.count                 // redraw on playhead, selection and edits
        _ = model.playhead
        _ = model.selection
        _ = model.tool
        view.window?.invalidateCursorRects(for: view)
        view.needsDisplay = true
    }
}

private struct PlayerRepresentable: NSViewRepresentable {
    let player: AVPlayer
    func makeNSView(context: Context) -> AVPlayerView {
        let v = AVPlayerView()
        v.player = player
        v.controlsStyle = .none
        v.videoGravity = .resizeAspect
        return v
    }
    func updateNSView(_ v: AVPlayerView, context: Context) {}
}

// MARK: - Timeline

/// Hundredths of a second, so single-frame steps are visible in the readout.
private func fmtPrecise(_ t: Double) -> String {
    let t = max(0, t)
    return String(format: "%d:%02d.%02d", Int(t) / 60, Int(t) % 60, Int((t - floor(t)) * 100))
}

private func fmt(_ t: Double) -> String {
    let t = max(0, t)
    return String(format: "%d:%02d.%01d", Int(t) / 60, Int(t) % 60, Int((t - floor(t)) * 10))
}

private struct TimelineEditor: View {
    @ObservedObject var model: VideoEditorModel
    private let labelW: CGFloat = 124
    private let rowH: CGFloat = 28
    @State private var began = false
    @State private var originObject: VideoObject?
    @State private var originMute: AudioMute?
    @State private var originSpeed: SpeedRegion?

    var body: some View {
        GeometryReader { geo in
            let trackW = max(80, geo.size.width - labelW - 16)
            let dur = max(model.project.duration, 0.01)
            let X: (Double) -> CGFloat = { labelW + CGFloat($0 / dur) * trackW }
            let T: (CGFloat) -> Double = { min(max(Double(($0 - labelW) / trackW) * dur, 0), dur) }
            ZStack(alignment: .topLeading) {
                VStack(spacing: 4) {
                    ruler(X, T, dur, trackW)
                    videoRow(X, T, dur, trackW)
                    ForEach(model.project.audioRoles, id: \.self) { role in audioRow(role, X, T, dur, trackW) }
                    ScrollView(.vertical) {
                        VStack(spacing: 4) {
                            ForEach(model.project.objects) { o in objectRow(o, X, T, dur, trackW) }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
                Rectangle().fill(Color.white).frame(width: 1.5)
                    .offset(x: X(model.playhead) - 0.75).allowsHitTesting(false)
            }
            .coordinateSpace(name: "timeline")
        }
    }

    private func begin(_ action: () -> Void) {
        if !began { began = true; action() }
    }

    // MARK: Rows

    private func ruler(_ X: @escaping (Double) -> CGFloat, _ T: @escaping (CGFloat) -> Double, _ dur: Double, _ trackW: CGFloat) -> some View {
        let step = dur > 120 ? 30.0 : dur > 40 ? 10.0 : dur > 15 ? 5.0 : 1.0
        return ZStack(alignment: .topLeading) {
            Rectangle().fill(Color.white.opacity(0.04)).frame(height: 20)
            ForEach(Array(stride(from: 0.0, through: dur, by: step)), id: \.self) { t in
                if X(t) + 44 < labelW + trackW + 8 {            // skip a label that would run off the right edge
                    Text(fmt(t)).font(.system(size: 9, design: .monospaced)).foregroundColor(Theme.muted)
                        .offset(x: X(t) + 2, y: 3)
                }
                Rectangle().fill(Theme.border).frame(width: 1, height: 20).offset(x: X(t))
            }
        }
        .frame(height: 20)
        .contentShape(Rectangle())
        .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("timeline")).onChanged { v in
            model.pause()
            model.seek(to: T(v.location.x))
        })
    }

    private func videoRow(_ X: @escaping (Double) -> CGFloat, _ T: @escaping (CGFloat) -> Double, _ dur: Double, _ trackW: CGFloat) -> some View {
        let p = model.project
        return ZStack(alignment: .leading) {
            Text("Video").font(.system(size: 12, weight: .medium)).foregroundColor(Theme.text)
                .frame(width: labelW - 8, alignment: .leading).padding(.leading, 4)
            Capsule().fill(Color.white.opacity(0.07)).frame(width: trackW, height: 12).offset(x: labelW)

            // the recording as a row of clips: click one to select it (and move the playhead there)
            ForEach(Array(p.segments.enumerated()), id: \.element.id) { i, seg in segmentBlock(seg, i, X, T) }
            // outside the trim handles
            Rectangle().fill(Color.black.opacity(0.55)).frame(width: max(0, X(p.trimStart) - labelW), height: rowH - 6).offset(x: labelW)
                .allowsHitTesting(false)
            Rectangle().fill(Color.black.opacity(0.55)).frame(width: max(0, labelW + trackW - X(p.trimEnd)), height: rowH - 6)
                .offset(x: X(p.trimEnd)).allowsHitTesting(false)
            // drag a split to move it
            ForEach(Array(p.segments.enumerated()).dropFirst(), id: \.element.id) { i, seg in
                Rectangle().fill(Color.white.opacity(0.001)).frame(width: 10, height: rowH - 4)
                    .overlay(Rectangle().fill(Color.white.opacity(0.85)).frame(width: 2, height: rowH - 8))
                    .offset(x: X(seg.start) - 5)
                    .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("timeline")).onChanged { v in
                        begin { model.beginChange() }
                        model.moveBoundary(i, to: T(v.location.x))
                    }.onEnded { _ in began = false })
            }
            ForEach(p.speeds) { sp in speedBlock(sp, X, T, dur, trackW) }

            knob.offset(x: X(p.trimStart) - 7)
                .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("timeline")).onChanged { v in
                    begin { model.beginChange() }
                    model.project.trimStart = min(max(0, T(v.location.x)), model.project.trimEnd - 0.3)
                    model.pause(); model.seek(to: model.project.trimStart)
                }.onEnded { _ in began = false })
            knob.offset(x: X(p.trimEnd) - 7)
                .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("timeline")).onChanged { v in
                    begin { model.beginChange() }
                    model.project.trimEnd = max(min(dur, T(v.location.x)), model.project.trimStart + 0.3)
                    model.pause(); model.seek(to: model.project.trimEnd)
                }.onEnded { _ in began = false })
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .frame(height: rowH)
    }

    private func segmentBlock(_ seg: Segment, _ index: Int, _ X: @escaping (Double) -> CGFloat, _ T: @escaping (CGFloat) -> Double) -> some View {
        let x0 = X(seg.start) + 1, x1 = X(seg.end) - 1
        let selected = model.selectedSegment == seg.id
        let red = Color(hex: 0xff4d4d)
        return RoundedRectangle(cornerRadius: 4)
            .fill(seg.removed ? red.opacity(0.35) : Theme.accent.opacity(selected ? 0.65 : 0.4))
            .overlay(RoundedRectangle(cornerRadius: 4).stroke(selected ? Color.white : (seg.removed ? red : Color.clear), lineWidth: selected ? 2 : 1))
            .overlay(Group {
                if seg.removed { Image(systemName: "scissors").font(.system(size: 10)).foregroundColor(.white.opacity(0.9)) }
                else if x1 - x0 > 44 { Text(String(format: "%.1fs", seg.end - seg.start)).font(.system(size: 10, weight: .medium)).foregroundColor(.white.opacity(0.85)) }
            })
            .frame(width: max(4, x1 - x0), height: rowH - 8)
            .contentShape(Rectangle())            // the clickable area is this block...
            .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("timeline")).onEnded { v in
                model.selectedSegment = seg.id
                model.pause()
                model.seek(to: T(v.location.x))
            })
            .offset(x: x0)                        // ...and it moves along when placed. (A hit area set after the offset stays behind.)
    }

    private func speedBlock(_ r: SpeedRegion, _ X: @escaping (Double) -> CGFloat, _ T: @escaping (CGFloat) -> Double,
                            _ dur: Double, _ trackW: CGFloat) -> some View {
        let x0 = X(r.start), x1 = X(r.end)
        let blue = Color(hex: 0x5ac8fa)
        let selected = model.selectedSpeed == r.id
        func update(_ change: (inout SpeedRegion) -> Void) {
            if let i = model.project.speeds.firstIndex(where: { $0.id == r.id }) { change(&model.project.speeds[i]) }
        }
        return ZStack(alignment: .leading) {
            RoundedRectangle(cornerRadius: 5).fill(blue.opacity(selected ? 0.55 : 0.35))
                .overlay(RoundedRectangle(cornerRadius: 5).stroke(selected ? Theme.accent : blue, lineWidth: selected ? 2 : 1))
                .overlay(Text(String(format: "%g×", r.rate)).font(.system(size: 10, weight: .bold)).foregroundColor(.white))
                .frame(width: max(8, x1 - x0), height: rowH - 4)
                .offset(x: x0)
                .gesture(DragGesture(minimumDistance: 2, coordinateSpace: .named("timeline")).onChanged { v in
                    begin { model.beginChange(); originSpeed = r; model.selectedSpeed = r.id }
                    guard let o = originSpeed else { return }
                    let shift = Double(v.translation.width / trackW) * dur
                    let len = o.end - o.start
                    let s = min(max(0, o.start + shift), dur - len)
                    update { $0.start = s; $0.end = s + len }
                }.onEnded { _ in began = false; originSpeed = nil })
                .simultaneousGesture(TapGesture().onEnded { model.selectedSpeed = r.id })
            edge(x: x0 - 3).gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("timeline")).onChanged { v in
                begin { model.beginChange(); model.selectedSpeed = r.id }
                update { $0.start = min(max(0, T(v.location.x)), $0.end - 0.1) }
            }.onEnded { _ in began = false })
            edge(x: x1 - 3).gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("timeline")).onChanged { v in
                begin { model.beginChange(); model.selectedSpeed = r.id }
                update { $0.end = max(min(dur, T(v.location.x)), $0.start + 0.1) }
            }.onEnded { _ in began = false })
        }
    }

    private func audioRow(_ role: AudioRole, _ X: @escaping (Double) -> CGFloat, _ T: @escaping (CGFloat) -> Double,
                          _ dur: Double, _ trackW: CGFloat) -> some View {
        let sel = model.selectedAudio?.role == role ? model.selectedAudio : nil
        return ZStack(alignment: .leading) {
            HStack(spacing: 6) {
                Image(systemName: role == .mic ? "mic" : "speaker.wave.2").font(.system(size: 11)).foregroundColor(Theme.muted)
                Text(role.title).font(.system(size: 12)).foregroundColor(Theme.muted).lineLimit(1)
                Spacer(minLength: 0)
            }
            .frame(width: labelW - 8).padding(.leading, 4)
            Capsule().fill(Color.white.opacity(0.07)).frame(width: trackW, height: 10).offset(x: labelW)
            if let peaks = model.waves[role] {
                Canvas { ctx, size in
                    let mid = size.height / 2, half = (size.height - 6) / 2
                    var x: CGFloat = 0
                    while x < size.width {
                        let t0 = Double(x / size.width) * dur, t1 = Double((x + 2) / size.width) * dur
                        let i0 = Int(t0 / WaveformReader.bucket), i1 = max(i0, Int(t1 / WaveformReader.bucket))
                        var v: Float = 0
                        if i0 < peaks.count { for i in i0...min(i1, peaks.count - 1) where peaks[i] > v { v = peaks[i] } }
                        let h = max(0.5, CGFloat(v) * half)
                        ctx.fill(Path(CGRect(x: x, y: mid - h, width: 1.2, height: h * 2)), with: .color(Color(hex: 0x9fd8ff).opacity(0.7)))
                        x += 2
                    }
                }
                .frame(width: trackW, height: rowH).offset(x: labelW).allowsHitTesting(false)
            }
            // click a sound track to pick the part under the cursor (and put the playhead there)
            Rectangle().fill(Color.white.opacity(0.001)).frame(width: trackW, height: rowH).offset(x: labelW)
                .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("timeline")).onChanged { v in
                    let t = T(v.location.x)
                    model.pause()
                    model.seek(to: t)
                    model.selectedAudio = model.audioPart(role, at: t)
                })
            if let sel {
                RoundedRectangle(cornerRadius: 5).fill(Theme.accent.opacity(0.14))
                    .overlay(RoundedRectangle(cornerRadius: 5).stroke(Theme.accent, lineWidth: 1.5))
                    .frame(width: max(4, X(sel.end) - X(sel.start)), height: rowH - 4)
                    .offset(x: X(sel.start)).allowsHitTesting(false)
            }
            ForEach(model.project.audioSplits.filter { $0.role == role }) { sp in
                Rectangle().fill(Color.white.opacity(0.85)).frame(width: 1.5, height: rowH - 4)
                    .offset(x: X(sp.time) - 0.75).allowsHitTesting(false)
            }
            ForEach(model.project.mutes.filter { $0.role == role }) { m in muteBlock(m, role, X, T, dur, trackW) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .frame(height: rowH)
    }

    private func muteBlock(_ m: AudioMute, _ role: AudioRole, _ X: @escaping (Double) -> CGFloat, _ T: @escaping (CGFloat) -> Double,
                           _ dur: Double, _ trackW: CGFloat) -> some View {
        let x0 = X(m.start), x1 = X(m.end)
        let amber = Color(hex: 0xffb020)
        func update(_ change: (inout AudioMute) -> Void) {
            if let i = model.project.mutes.firstIndex(where: { $0.id == m.id }) { change(&model.project.mutes[i]) }
        }
        return ZStack(alignment: .leading) {
            RoundedRectangle(cornerRadius: 5).fill(amber.opacity(0.4))
                .overlay(RoundedRectangle(cornerRadius: 5).stroke(amber, lineWidth: 1))
                .overlay(Image(systemName: role == .mic ? "mic.slash" : "speaker.slash").font(.system(size: 10)).foregroundColor(.white.opacity(0.9)))
                .frame(width: max(8, x1 - x0), height: rowH - 4)
                .offset(x: x0)
                .gesture(DragGesture(minimumDistance: 2, coordinateSpace: .named("timeline")).onChanged { v in
                    begin { model.beginChange(); originMute = m }
                    guard let o = originMute else { return }
                    let shift = Double(v.translation.width / trackW) * dur
                    let len = o.end - o.start
                    let s = min(max(0, o.start + shift), dur - len)
                    update { $0.start = s; $0.end = s + len }
                }.onEnded { _ in began = false; originMute = nil })
            edge(x: x0 - 3).gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("timeline")).onChanged { v in
                begin { model.beginChange() }
                update { $0.start = min(max(0, T(v.location.x)), $0.end - 0.1) }
            }.onEnded { _ in began = false })
            edge(x: x1 - 3).gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("timeline")).onChanged { v in
                begin { model.beginChange() }
                update { $0.end = max(min(dur, T(v.location.x)), $0.start + 0.1) }
            }.onEnded { _ in began = false })
            if x1 - x0 > 46 {
                Button { model.removeMute(m.id) } label: {
                    Image(systemName: "xmark.circle.fill").font(.system(size: 12)).foregroundColor(.white.opacity(0.9))
                }.buttonStyle(.plain).offset(x: x1 - 18).help("Remove this mute")
            }
        }
    }

    private var knob: some View {
        RoundedRectangle(cornerRadius: 4).fill(Theme.accent).frame(width: 14, height: 24)
            .overlay(Capsule().fill(Color.black.opacity(0.5)).frame(width: 2, height: 10))
    }

    private func edge(x: CGFloat) -> some View {
        Rectangle().fill(Color.white.opacity(0.001)).frame(width: 8, height: rowH - 4)
            .overlay(Capsule().fill(Color.white.opacity(0.7)).frame(width: 2, height: 12))
            .offset(x: x)
    }

    private func objectRow(_ o: VideoObject, _ X: @escaping (Double) -> CGFloat, _ T: @escaping (CGFloat) -> Double,
                           _ dur: Double, _ trackW: CGFloat) -> some View {
        let selected = model.selection == o.id
        let x0 = X(o.start), x1 = X(o.end)
        let c = Color(nsColor: o.kind == .blur ? NSColor.white : o.color)
        return ZStack(alignment: .leading) {
            Button { model.selection = o.id; model.selectedKeyframe = nil } label: {
                HStack(spacing: 6) {
                    Image(systemName: model.icon(for: o)).font(.system(size: 11)).foregroundColor(selected ? Theme.accent : Theme.muted)
                    Text(model.label(for: o)).font(.system(size: 12, weight: selected ? .semibold : .regular))
                        .foregroundColor(selected ? Theme.text : Theme.muted).lineLimit(1)
                    Spacer(minLength: 0)
                }
                .frame(width: labelW - 8, height: rowH).padding(.leading, 4).contentShape(Rectangle())
            }.buttonStyle(.plain)

            Rectangle().fill(selected ? Color.white.opacity(0.05) : .clear).frame(width: trackW, height: rowH).offset(x: labelW)

            // visibility bar
            RoundedRectangle(cornerRadius: 6).fill(c.opacity(selected ? 0.45 : 0.28))
                .overlay(RoundedRectangle(cornerRadius: 6).stroke(selected ? Theme.accent : c.opacity(0.6), lineWidth: selected ? 2 : 1))
                .frame(width: max(8, x1 - x0), height: rowH - 6)
                .offset(x: x0)
                .gesture(DragGesture(minimumDistance: 2, coordinateSpace: .named("timeline")).onChanged { v in
                    begin { model.beginChange(); originObject = o; model.selection = o.id }
                    guard let origin = originObject else { return }
                    let shift = Double(v.translation.width / trackW) * dur
                    let len = origin.end - origin.start
                    let s = min(max(0, origin.start + shift), dur - len)
                    model.mutate(o.id) { $0.start = s; $0.end = s + len }
                }.onEnded { _ in began = false; originObject = nil })
                .simultaneousGesture(TapGesture().onEnded { model.selection = o.id; model.selectedKeyframe = nil })

            edge(x: x0 - 3).gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("timeline")).onChanged { v in
                begin { model.beginChange(); model.selection = o.id }
                model.mutate(o.id) { $0.start = min(max(0, T(v.location.x)), $0.end - 0.2) }
            }.onEnded { _ in began = false })
            edge(x: x1 - 3).gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("timeline")).onChanged { v in
                begin { model.beginChange(); model.selection = o.id }
                model.mutate(o.id) { $0.end = max(min(dur, T(v.location.x)), $0.start + 0.2) }
            }.onEnded { _ in began = false })

            // keyframe diamonds
            ForEach(Array(o.keyframes.enumerated()), id: \.offset) { i, kf in
                let on = selected && model.selectedKeyframe == i
                Image(systemName: "diamond.fill").font(.system(size: 11))
                    .foregroundColor(on ? Theme.accent : .white)
                    .shadow(color: .black.opacity(0.6), radius: 1)
                    .frame(width: 16, height: rowH).contentShape(Rectangle())
                    .offset(x: X(kf.time) - 8)
                    .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("timeline")).onChanged { v in
                        let moved = abs(v.translation.width) > 3
                        if moved { begin { model.beginChange() } }
                        model.selection = o.id
                        model.selectedKeyframe = i
                        model.pause()
                        if moved {
                            let t = T(v.location.x)
                            model.mutate(o.id) { obj in
                                obj.keyframes[i].time = t
                                let moving = obj.keyframes[i]
                                obj.keyframes.sort { $0.time < $1.time }
                                model.selectedKeyframe = obj.keyframes.firstIndex(of: moving)
                            }
                        }
                        model.seek(to: model.selectedObject?.keyframes[model.selectedKeyframe ?? i].time ?? kf.time)
                    }.onEnded { _ in began = false })
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .frame(height: rowH)
    }
}

// MARK: - Editor window

struct VideoEditorView: View {
    @ObservedObject var model: VideoEditorModel
    let backupFolder: String?
    let onUpload: () -> Void
    let onCopy: () -> Void
    let onSkip: () -> Void

    private var p: VideoProject { model.project }

    var body: some View {
        VStack(spacing: 10) {
            ViewThatFits(in: .horizontal) {
                tools(compact: false)
                tools(compact: true)
            }

            ZStack {
                Color.black
                PlayerRepresentable(player: model.player)
                OverlayRepresentable(model: model)
            }
            .frame(minHeight: 240)
            .clipShape(RoundedRectangle(cornerRadius: 12))

            ViewThatFits(in: .horizontal) {
                transport(compact: false)
                transport(compact: true)
            }

            inspector

            TimelineEditor(model: model)
                .frame(height: min(300, CGFloat(60 + 32 * (p.audioRoles.count + max(1, p.objects.count)))))

            HStack(spacing: 10) {
                Text(model.notice ?? summary).font(.system(size: 11))
                    .foregroundColor(model.notice == nil ? Theme.muted : Theme.warning).lineLimit(2)
                Spacer()
                if let backupFolder {
                    Text("Full copy kept in \(backupFolder)").font(.system(size: 11)).foregroundColor(Theme.muted).lineLimit(1)
                }
                // No Esc/Return shortcuts here: Esc deselects and Return commits text, so they must never close or upload by accident.
                Button("Don't upload", action: onSkip).buttonStyle(PillButtonStyle(prominent: false))
                SplitActionButton(title: uploadTitle, primary: onUpload, primaryShortcut: KeyboardShortcut("u", modifiers: .command), choices: [
                    .init(title: p.needsRender ? "Render & copy to clipboard" : "Copy to clipboard", icon: "doc.on.clipboard", action: onCopy,
                          shortcut: KeyboardShortcut("c", modifiers: [.command, .shift])),
                ])
            }
        }
        .padding(16)
        .background(Theme.background)
        .preferredColorScheme(.dark)
    }

    private var uploadTitle: String {
        if p.needsRender { return "Render & upload" }
        return p.isTrimmed || !p.mutes.isEmpty ? "Apply & upload" : "Upload"
    }

    private var summary: String {
        var parts = ["Output \(fmt(p.outputDuration))"]
        if p.needsRender { parts.append("re-encoded at \(Settings.shared.recordBitrate) Mbps") }
        else if p.isTrimmed || p.needsAudioMix { parts.append("video copied, not re-encoded") }
        if p.audioRoles.count > 1 { parts.append("desktop audio and mic mixed into one track") }
        return parts.joined(separator: " · ")
    }

    // MARK: Toolbars

    private func tools(compact: Bool) -> some View {
        HStack(spacing: 8) {
            Button { model.tool = nil } label: {
                if compact { Image(systemName: "cursorarrow") } else { Label("Select", systemImage: "cursorarrow") }
            }.buttonStyle(PillButtonStyle(prominent: model.tool == nil)).help("Select, move and resize")
            ForEach(EditorTool.allCases, id: \.self) { t in
                Button { model.tool = t } label: {
                    if compact { Image(systemName: t.icon) } else { Label(t.title, systemImage: t.icon) }
                }.buttonStyle(PillButtonStyle(prominent: model.tool == t)).help(t.help)
            }
            Button { importMedia() } label: {
                if compact { Image(systemName: "photo.badge.plus") } else { Label("Media", systemImage: "photo.badge.plus") }
            }
            .buttonStyle(PillButtonStyle(prominent: false))
            .help("Add an image, GIF or video from a file. You can also paste one with ⌘V or drop it onto the preview.")
            Button { model.addZoom() } label: {
                if compact { Image(systemName: "plus.magnifyingglass") } else { Label("Zoom", systemImage: "plus.magnifyingglass") }
            }
            .buttonStyle(PillButtonStyle(prominent: model.selectedObject?.kind == .zoom))
            .help("Punch in on a detail from the playhead. Drag its box onto the detail; add keyframes to pan.")
            Divider().frame(height: 22)
            ForEach(Array(EditorModel.palette.enumerated()), id: \.offset) { i, c in
                Button { model.colorIndex = i; applyColor() } label: {
                    Circle().fill(Color(nsColor: c)).frame(width: 18, height: 18)
                        .overlay(Circle().stroke(model.colorIndex == i ? Theme.accent : Theme.border, lineWidth: model.colorIndex == i ? 2.5 : 1))
                        .padding(3).contentShape(Circle())
                }.buttonStyle(.plain)
            }
            HStack(spacing: 2) {
                Button { model.colorIndex = -1; applyColor() } label: {
                    Circle().fill(Color(nsColor: model.customColor)).frame(width: 18, height: 18)
                        .overlay(Circle().stroke(model.colorIndex == -1 ? Theme.accent : Theme.border, lineWidth: model.colorIndex == -1 ? 2.5 : 1))
                        .padding(3).contentShape(Circle())
                }.buttonStyle(.plain)
                ColorPicker("", selection: Binding(
                    get: { Color(nsColor: model.customColor) },
                    set: { model.customColor = NSColor($0).usingColorSpace(.sRGB) ?? model.customColor; model.colorIndex = -1; applyColor() }
                ), supportsOpacity: false).labelsHidden().frame(width: 40)
            }
            Picker("", selection: $model.level) { Text("S").tag(1); Text("M").tag(2); Text("L").tag(3) }
                .pickerStyle(.segmented).labelsHidden().frame(width: 90)
            if model.tool == .blur {
                Picker("", selection: $model.blurSoft) { Text("Pixels").tag(false); Text("Soft").tag(true) }
                    .pickerStyle(.segmented).labelsHidden().frame(width: 120)
                    .help("Pixelate, or blur softly. Soft blur looks better on large areas.")
            }
            Spacer(minLength: 0)
        }
    }

    @ViewBuilder private var inspector: some View {
        if let a = model.selectedAudio {
            audioPartInspector(a)
        } else if let gid = model.selectedSegment, let (i, seg) = model.segment(gid) {
            clipSegmentInspector(seg, i)
        } else if let sid = model.selectedSpeed, let r = p.speeds.first(where: { $0.id == sid }) {
            speedInspector(r)
        } else if let o = model.selectedObject {
            objectInspector(o)
        } else if !p.audioRoles.isEmpty || p.objects.contains(where: { $0.media?.hasAudio == true }) {
            HStack(spacing: 14) {
                Image(systemName: "waveform").foregroundColor(Theme.accent)
                Text("Audio fades").font(.system(size: 12, weight: .medium)).foregroundColor(Theme.text)
                Text("Fade the whole mix in and out").font(.system(size: 11)).foregroundColor(Theme.muted)
                Spacer(minLength: 8)
                fadeSlider("In", p.audioFadeIn, max: 5) { model.project.audioFadeIn = $0 }
                fadeSlider("Out", p.audioFadeOut, max: 5) { model.project.audioFadeOut = $0 }
            }
            .inspectorBox()
        }
    }

    private func audioPartInspector(_ a: AudioSelection) -> some View {
        HStack(spacing: 12) {
            Image(systemName: a.role == .mic ? "mic" : "speaker.wave.2").foregroundColor(Theme.accent)
            Text(a.role.title).font(.system(size: 12, weight: .medium)).foregroundColor(Theme.text)
            Text("\(fmt(a.start)) – \(fmt(a.end))  ·  \(String(format: "%.1f", a.end - a.start)) s")
                .font(.system(size: 11)).foregroundColor(Theme.muted)
            Spacer(minLength: 8)
            Button { model.splitAtPlayhead() } label: { Label("Split here", systemImage: "scissors") }
                .buttonStyle(PillButtonStyle(prominent: false)).help("Split this sound track at the playhead")
            Button { model.muteSelectedAudio() } label: { Label("Mute this part", systemImage: "speaker.slash") }
                .buttonStyle(PillButtonStyle()).help("Silence this part of the track (remove the mute to bring it back)")
        }
        .inspectorBox()
    }

    private func clipSegmentInspector(_ seg: Segment, _ index: Int) -> some View {
        HStack(spacing: 12) {
            Image(systemName: "rectangle.split.2x1").foregroundColor(seg.removed ? Color(hex: 0xff4d4d) : Theme.accent)
            Text("Clip \(index + 1)").font(.system(size: 12, weight: .medium)).foregroundColor(Theme.text)
            Text("\(fmt(seg.start)) – \(fmt(seg.end))  ·  \(String(format: "%.1f", seg.end - seg.start)) s" + (seg.removed ? "  ·  removed" : ""))
                .font(.system(size: 11)).foregroundColor(Theme.muted)
            Spacer(minLength: 8)
            if index > 0 {
                Button { model.joinWithPrevious(seg.id) } label: { Label("Join with previous", systemImage: "arrow.left.to.line") }
                    .buttonStyle(PillButtonStyle(prominent: false)).help("Remove the split before this clip")
            }
            if seg.removed {
                Button { model.restoreSegment(seg.id) } label: { Label("Restore clip", systemImage: "arrow.uturn.backward") }
                    .buttonStyle(PillButtonStyle())
            } else {
                Button { model.addSpeed() } label: { Label("Speed", systemImage: "speedometer") }
                    .buttonStyle(PillButtonStyle(prominent: false)).help("Speed up or slow down this whole clip")
                Button { model.deleteSegment(seg.id) } label: { Label("Delete clip", systemImage: "trash") }
                    .buttonStyle(PillButtonStyle()).help("Remove this clip from the video (you can restore it)")
            }
        }
        .inspectorBox()
    }

    private func speedInspector(_ r: SpeedRegion) -> some View {
        HStack(spacing: 12) {
            Image(systemName: "speedometer").foregroundColor(Color(hex: 0x5ac8fa))
            Text("Speed").font(.system(size: 12, weight: .medium)).foregroundColor(Theme.text)
            Text(String(format: "%.1f s of footage plays in %.1f s", r.end - r.start, (r.end - r.start) / r.rate))
                .font(.system(size: 11)).foregroundColor(Theme.muted)
            Spacer(minLength: 8)
            Picker("", selection: Binding(get: { r.rate }, set: { model.setSpeedRate(r.id, $0) })) {
                ForEach([0.25, 0.5, 0.75, 1.5, 2.0, 3.0, 4.0], id: \.self) { Text(String(format: "%g×", $0)).tag($0) }
            }
            .labelsHidden().pickerStyle(.menu).fixedSize()
            Button { model.removeSpeed(r.id) } label: { Label("Remove", systemImage: "trash") }
                .buttonStyle(PillButtonStyle(prominent: false))
        }
        .inspectorBox()
    }

    private func fadeSlider(_ title: String, _ value: Double, max: Double, set: @escaping (Double) -> Void) -> some View {
        HStack(spacing: 6) {
            Text(title).font(.system(size: 11)).foregroundColor(Theme.muted)
            Slider(value: Binding(get: { value }, set: set), in: 0...Swift.max(max, 0.1)) { editing in
                if editing { model.beginChange() }
            }.frame(width: 90)
            Text(String(format: "%.1fs", value)).font(.system(size: 11, design: .monospaced)).foregroundColor(Theme.muted)
                .frame(width: 36, alignment: .trailing)
        }
    }

    /// Name, fades (or the ease in and out for a zoom), and for imported videos their own audio.
    @ViewBuilder private func objectInspector(_ o: VideoObject) -> some View {
        let limit = Swift.max(0.1, Swift.min(3, (o.end - o.start) / 2))
        let isZoom = o.kind == .zoom
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 12) {
                objectTitle(o)
                Spacer(minLength: 8)
                if let clip = o.media, clip.hasAudio { clipAudioControls(o) }
                if o.kind == .blur { blurControls(o) }
                fadeSlider(isZoom ? "Ease in" : "Fade in", o.fadeIn, max: limit) { model.setFade(o.id, fadeIn: $0) }
                fadeSlider(isZoom ? "Ease out" : "Fade out", o.fadeOut, max: limit) { model.setFade(o.id, fadeOut: $0) }
            }
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    objectTitle(o)
                    Spacer(minLength: 8)
                    if let clip = o.media, clip.hasAudio { clipAudioControls(o) }
                    if o.kind == .blur { blurControls(o) }
                }
                HStack(spacing: 14) {
                    fadeSlider(isZoom ? "Ease in" : "Fade in", o.fadeIn, max: limit) { model.setFade(o.id, fadeIn: $0) }
                    fadeSlider(isZoom ? "Ease out" : "Fade out", o.fadeOut, max: limit) { model.setFade(o.id, fadeOut: $0) }
                }
            }
        }
        .inspectorBox()
    }

    /// Style (pixels or a soft blur) and strength of a blur box.
    private func blurControls(_ o: VideoObject) -> some View {
        HStack(spacing: 8) {
            Picker("", selection: Binding(get: { o.softBlur }, set: { v in
                model.beginChange(); model.mutate(o.id) { $0.softBlur = v }
            })) { Text("Pixels").tag(false); Text("Soft").tag(true) }
                .pickerStyle(.segmented).labelsHidden().frame(width: 120)
            Picker("", selection: Binding(get: { o.level }, set: { v in
                model.beginChange(); model.mutate(o.id) { $0.level = v }
            })) { Text("S").tag(1); Text("M").tag(2); Text("L").tag(3) }
                .pickerStyle(.segmented).labelsHidden().frame(width: 90)
                .help("Blur strength")
        }
    }

    private func objectTitle(_ o: VideoObject) -> some View {
        HStack(spacing: 8) {
            Image(systemName: model.icon(for: o)).foregroundColor(Theme.accent)
            Text(o.kind == .media ? (o.media?.name ?? "Media") : model.label(for: o))
                .font(.system(size: 12, weight: .medium)).foregroundColor(Theme.text).lineLimit(1)
            if o.kind == .media, let clip = o.media {
                Text(clip.kind == .image ? "image" : clip.kind == .gif ? "GIF, loops" : String(format: "video, %.1f s", clip.duration))
                    .font(.system(size: 11)).foregroundColor(Theme.muted)
            } else if o.kind == .zoom {
                Text("drag the box onto the detail").font(.system(size: 11)).foregroundColor(Theme.muted)
            }
        }
    }

    private func clipAudioControls(_ o: VideoObject) -> some View {
        HStack(spacing: 8) {
            Button { model.toggleClipMute() } label: {
                Label(o.audioMuted ? "Clip audio off" : "Clip audio on", systemImage: o.audioMuted ? "speaker.slash.fill" : "speaker.wave.2.fill")
            }.buttonStyle(PillButtonStyle(prominent: !o.audioMuted)).help("Mute or unmute this clip's own sound")
            Slider(value: Binding(get: { o.volume }, set: { model.setClipVolume($0) }), in: 0...1) { editing in
                if editing { model.beginChange() }
            }
            .frame(width: 100).disabled(o.audioMuted)
            Text("\(Int(o.volume * 100))%").font(.system(size: 11, design: .monospaced)).foregroundColor(Theme.muted)
                .frame(width: 36, alignment: .trailing)
        }
    }

    /// Name and, for videos with sound, the clip's own volume and mute (separate from the recording's mic/desktop tracks).
    private func clipInspector(_ o: VideoObject, _ clip: MediaClip) -> some View {
        HStack(spacing: 12) {
            Image(systemName: model.icon(for: o)).foregroundColor(Theme.accent)
            Text(clip.name).font(.system(size: 12, weight: .medium)).foregroundColor(Theme.text).lineLimit(1)
            Text(clip.kind == .image ? "image" : clip.kind == .gif ? "GIF, loops" : String(format: "video, %.1f s", clip.duration))
                .font(.system(size: 11)).foregroundColor(Theme.muted)
            Spacer(minLength: 8)
            if clip.hasAudio {
                Button { model.toggleClipMute() } label: {
                    Label(o.audioMuted ? "Clip audio off" : "Clip audio on", systemImage: o.audioMuted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                }.buttonStyle(PillButtonStyle(prominent: !o.audioMuted)).help("Mute or unmute this clip's own sound")
                Slider(value: Binding(get: { o.volume }, set: { model.setClipVolume($0) }), in: 0...1) { editing in
                    if editing { model.beginChange() }
                }
                .frame(width: 140).disabled(o.audioMuted)
                Text("\(Int(o.volume * 100))%").font(.system(size: 11, design: .monospaced)).foregroundColor(Theme.muted)
                    .frame(width: 40, alignment: .trailing)
            } else if clip.kind == .video {
                Text("no sound").font(.system(size: 11)).foregroundColor(Theme.muted)
            }
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
        .background(Theme.card)
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(Theme.border))
        .clipShape(RoundedRectangle(cornerRadius: 12))
    }

    private func importMedia() {
        let urls = MediaImport.choose(allowVideo: true)
        Task { @MainActor in
            for u in urls { if let clip = await MediaClip.load(url: u) { model.addMedia(clip) } }
        }
    }

    /// Changing colour / size with an object selected restyles that object.
    private func applyColor() {
        guard let o = model.selectedObject, o.kind != .blur else { return }
        model.beginChange()
        let c = model.color
        model.mutate(o.id) { $0.color = c }
    }

    private func transport(compact: Bool) -> some View {
        HStack(spacing: 8) {
            Button { model.togglePlay() } label: {
                Label(model.playing ? "Pause" : "Play", systemImage: model.playing ? "pause.fill" : "play.fill")
            }.buttonStyle(PillButtonStyle(prominent: false)).keyboardShortcut(.space, modifiers: [])
            Button { model.stepFrames(-1) } label: { Image(systemName: "chevron.left") }
                .buttonStyle(PillButtonStyle(prominent: false)).keyboardShortcut(",", modifiers: [])
                .help("Previous frame  ( , or < or ← )")
            Button { model.stepFrames(1) } label: { Image(systemName: "chevron.right") }
                .buttonStyle(PillButtonStyle(prominent: false)).keyboardShortcut(".", modifiers: [])
                .help("Next frame  ( . or > or → )")
            Text("\(fmtPrecise(model.playhead)) / \(fmt(p.duration))")
                .font(.system(size: 12, design: .monospaced)).foregroundColor(Theme.text)
            Divider().frame(height: 22)
            Button { model.animate.toggle() } label: {
                if compact { Image(systemName: "diamond.fill") } else { Label("Animate", systemImage: "diamond.fill") }
            }
            .buttonStyle(PillButtonStyle(prominent: model.animate))
            .help(model.animate ? "On: moving an object at a new time adds a keyframe, so it glides there" : "Off: moving an object shifts its whole path")
            Button { model.addKeyframeAtPlayhead() } label: {
                if compact { Image(systemName: "plus.diamond") } else { Label("Keyframe", systemImage: "plus.diamond") }
            }
            .buttonStyle(PillButtonStyle(prominent: false)).disabled(model.selectedObject == nil)
            .help("Add a keyframe for the selected object at the playhead")
            Button { model.addSpeed() } label: {
                if compact { Image(systemName: "speedometer") } else { Label("Speed", systemImage: "speedometer") }
            }
            .buttonStyle(PillButtonStyle(prominent: model.selectedSpeed != nil))
            .help("Speed up or slow down a section starting at the playhead (drag its ends; click it to pick the rate)")
            Button { model.splitAtPlayhead() } label: {
                if compact { Image(systemName: "scissors") } else { Label("Split", systemImage: "scissors") }
            }
            .buttonStyle(PillButtonStyle(prominent: false))
            .help("Split at the playhead: the selected layer or speed block, or the video clip if nothing is selected. Then move, delete or change either half.")
            ForEach(p.audioRoles, id: \.self) { role in
                Button { model.addMute(role) } label: {
                    let icon = role == .mic ? "mic.slash" : "speaker.slash"
                    let name = role == .mic ? "Mute mic" : role == .system ? "Mute desktop" : "Mute audio"
                    if compact { Image(systemName: icon) } else { Label(name, systemImage: icon) }
                }
                .buttonStyle(PillButtonStyle(prominent: false))
                .help("Silence only the \(role.title.lowercased()) from the playhead (drag its ends to size it); everything else keeps playing")
            }
            Button { model.deleteSelected() } label: {
                if compact { Image(systemName: "trash") } else {
                    Label(model.selectedKeyframe != nil ? "Delete keyframe" : (model.selectedObject == nil && model.selectedSegment != nil ? "Delete clip" : "Delete"),
                          systemImage: "trash")
                }
            }
            .buttonStyle(PillButtonStyle(prominent: false)).disabled(model.selectedObject == nil && model.selectedSegment == nil)
            Button { model.undo() } label: { Image(systemName: "arrow.uturn.backward") }
                .buttonStyle(PillButtonStyle(prominent: false)).disabled(!model.canUndo)
                .keyboardShortcut("z", modifiers: .command).help("Undo")
            Spacer(minLength: 0)
        }
    }
}

enum VideoEditResult {
    case upload(VideoProject)
    case copy(VideoProject)         // put the finished video file on the clipboard instead of uploading
    case skip
}

final class VideoEditor: NSObject, NSWindowDelegate {
    private static var open: [VideoEditor] = []
    private var window: NSWindow?
    private var completion: ((VideoEditResult) -> Void)?
    private let model: VideoEditorModel

    /// Opens the editor. `completion` gets the edits, or .skip if the user closed it / chose not to upload.
    static func show(_ url: URL, duration: Double, backupFolder: String?, completion: @escaping (VideoEditResult) -> Void) {
        Task { @MainActor in
            var size = CGSize(width: 1280, height: 720)
            let asset = AVURLAsset(url: url)
            if let track = try? await asset.loadTracks(withMediaType: .video).first,
               let s = try? await track.load(.naturalSize), s.width > 0, s.height > 0 { size = s }
            let audio = await AudioTracks.load(from: asset)
            var fps = 30.0
            if let track = try? await asset.loadTracks(withMediaType: .video).first,
               let rate = try? await track.load(.nominalFrameRate), rate > 1 { fps = Double(rate) }
            let model = VideoEditorModel(asset: asset, url: url, duration: duration, videoSize: size, audioTracks: audio, frameRate: fps)
            let e = VideoEditor(model: model, completion: completion)
            open.append(e)
            e.present(backupFolder: backupFolder)
        }
    }

    private init(model: VideoEditorModel, completion: @escaping (VideoEditResult) -> Void) {
        self.model = model
        self.completion = completion
    }

    private func present(backupFolder: String?) {
        let view = VideoEditorView(model: model, backupFolder: backupFolder,
                                   onUpload: { [weak self] in self?.finish(.upload) },
                                   onCopy: { [weak self] in self?.finish(.copy) },
                                   onSkip: { [weak self] in self?.finish(.skip) })
        let w = NSWindow(contentViewController: NSHostingController(rootView: view))
        w.title = "Edit recording"
        w.styleMask = [.titled, .closable, .resizable, .miniaturizable]
        w.appearance = NSAppearance(named: .darkAqua)
        w.backgroundColor = NSColor(Theme.background)
        w.isReleasedWhenClosed = false
        w.delegate = self
        w.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
        w.contentMinSize = NSSize(width: 760, height: 640)
        let screen = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1280, height: 800)
        w.setFrame(screen.insetBy(dx: screen.width * 0.02, dy: screen.height * 0.02), display: false)   // ~96% of the screen
        w.center()
        window = w
        w.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private enum Action { case upload, copy, skip }

    private func finish(_ action: Action) {
        guard let completion else { return }
        self.completion = nil
        model.pause()
        let result: VideoEditResult
        switch action {
        case .upload: result = .upload(model.project)
        case .copy: result = .copy(model.project)
        case .skip: result = .skip
        }
        window?.delegate = nil
        window?.close()
        window = nil
        Self.open.removeAll { $0 === self }
        completion(result)
    }

    func windowWillClose(_ notification: Notification) { finish(.skip) }   // closing = don't upload
}

private extension View {
    func inspectorBox() -> some View {
        self.padding(.horizontal, 12).padding(.vertical, 8)
            .background(Theme.card)
            .overlay(RoundedRectangle(cornerRadius: 12).stroke(Theme.border))
            .clipShape(RoundedRectangle(cornerRadius: 12))
    }
}


/// Reads one sound track and returns how loud it is over time, for the timeline lanes.
enum WaveformReader {
    static let bucket = 0.02                         // seconds per value

    static func peaks(of track: AVAssetTrack, in asset: AVAsset) async -> [Float]? {
        await Task.detached(priority: .utility) { () -> [Float]? in
            guard let reader = try? AVAssetReader(asset: asset) else { return nil }
            let settings: [String: Any] = [
                AVFormatIDKey: kAudioFormatLinearPCM, AVLinearPCMBitDepthKey: 16, AVLinearPCMIsFloatKey: false,
                AVLinearPCMIsBigEndianKey: false, AVLinearPCMIsNonInterleaved: false, AVNumberOfChannelsKey: 1, AVSampleRateKey: 4000,
            ]
            let out = AVAssetReaderTrackOutput(track: track, outputSettings: settings)
            guard reader.canAdd(out) else { return nil }
            reader.add(out)
            guard reader.startReading() else { return nil }
            let per = Int(4000 * bucket)
            var peaks: [Float] = []
            var peak: Float = 0
            var count = 0
            while let sb = out.copyNextSampleBuffer() {
                guard let bb = CMSampleBufferGetDataBuffer(sb) else { continue }
                let len = CMBlockBufferGetDataLength(bb)
                var samples = [Int16](repeating: 0, count: len / 2)
                CMBlockBufferCopyDataBytes(bb, atOffset: 0, dataLength: len / 2 * 2, destination: &samples)
                for s in samples {
                    let v = Float(abs(Int32(s))) / 32768
                    if v > peak { peak = v }
                    count += 1
                    if count == per { peaks.append(peak); peak = 0; count = 0 }
                }
            }
            if count > 0 { peaks.append(peak) }
            guard !peaks.isEmpty else { return nil }
            let sorted = peaks.sorted()
            let top = max(0.02, sorted[min(sorted.count - 1, Int(Double(sorted.count) * 0.98))])
            return peaks.map { min(1, ($0 / top).squareRoot()) }
        }.value
    }
}
