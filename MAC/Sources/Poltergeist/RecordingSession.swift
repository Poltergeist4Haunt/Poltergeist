import Foundation
import ScreenCaptureKit
import AVFoundation
import CoreMedia

enum RecordingError: LocalizedError {
    case noDisplay
    case noWindow
    case writer(String)
    var errorDescription: String? {
        switch self {
        case .noDisplay: return "Couldn't find that display."
        case .noWindow: return "That window isn't available to record."
        case .writer(let m): return "Couldn't start the video file: \(m)"
        }
    }
}

enum RecordingTarget {
    case region(displayID: CGDirectDisplayID, sourceRect: CGRect, excludedWindowIDs: [CGWindowID])
    case window(CGWindowID)
}

struct RecordingOptions {
    var fps: Int
    var bitrateMbps: Int
    var systemAudio: Bool
    var mic: Bool
    var micDeviceID: String   // "" = system default
}

/// One recording: ScreenCaptureKit frames (+ optional desktop audio and microphone) → H.264/AAC MP4, with pause support.
/// Everything that touches the writer or mixer runs on `queue`.
final class RecordingSession: NSObject, SCStreamOutput, SCStreamDelegate, AVCaptureAudioDataOutputSampleBufferDelegate {
    let outputURL: URL
    var onError: ((String) -> Void)?
    var onWarning: ((String) -> Void)?

    private let queue = DispatchQueue(label: "gg.haunt.poltergeist.recording")
    private var stream: SCStream?
    private var micSession: AVCaptureSession?
    private var writer: AVAssetWriter?
    private var input: AVAssetWriterInput?
    private var systemInput: AVAssetWriterInput?        // desktop audio and mic are written as separate tracks
    private var micInput: AVAssetWriterInput?
    private var systemMixer: AudioMixer?
    private var micMixer: AudioMixer?
    private var flushTimer: DispatchSourceTimer?
    private var sessionStarted = false
    private var paused = false
    private var pausedAt = CMTime.zero
    private var pauseOffset = CMTime.zero
    private var dropBefore = CMTime.negativeInfinity   // buffers captured before a resume are stale
    private var lastPTS = CMTime.negativeInfinity
    private var lastSample: CMSampleBuffer?                 // most recent video frame we wrote, kept to hold it until the stop time
    private var lastSampleEnd = CMTime.negativeInfinity
    private var frameDuration = CMTime(value: 1, timescale: 30)

    init(outputURL: URL) { self.outputURL = outputURL }

    private static var hostNow: CMTime { CMClockGetTime(CMClockGetHostTimeClock()) }
    /// Audio is held back this long so late buffers from either source can still be mixed in.
    private static let audioLatency = CMTime(seconds: 0.3, preferredTimescale: 1000)

    func start(target: RecordingTarget, pixelSize: (width: Int, height: Int), options: RecordingOptions) async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        let cfg = SCStreamConfiguration()
        let filter: SCContentFilter
        switch target {
        case .region(let displayID, let rect, let excludedIDs):
            guard let display = content.displays.first(where: { $0.displayID == displayID }) else { throw RecordingError.noDisplay }
            // Our controls and outline are excluded from the capture, so they never show up in the video.
            let excluded = content.windows.filter { excludedIDs.contains($0.windowID) }
            filter = SCContentFilter(display: display, excludingWindows: excluded)
            cfg.sourceRect = rect
        case .window(let id):
            guard let window = content.windows.first(where: { $0.windowID == id }) else { throw RecordingError.noWindow }
            filter = SCContentFilter(desktopIndependentWindow: window)
        }
        cfg.width = pixelSize.width
        cfg.height = pixelSize.height
        cfg.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(options.fps))
        cfg.pixelFormat = kCVPixelFormatType_32BGRA
        cfg.showsCursor = true
        cfg.queueDepth = 6
        if options.systemAudio {
            cfg.capturesAudio = true
            cfg.sampleRate = 48_000
            cfg.channelCount = 2
            cfg.excludesCurrentProcessAudio = true   // don't record Poltergeist's own chimes
        }

        frameDuration = CMTime(value: 1, timescale: CMTimeScale(options.fps))
        try? FileManager.default.removeItem(at: outputURL)
        let w: AVAssetWriter
        do { w = try AVAssetWriter(outputURL: outputURL, fileType: .mp4) }
        catch { throw RecordingError.writer(error.localizedDescription) }

        let videoSettings: [String: Any] = [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: pixelSize.width,
            AVVideoHeightKey: pixelSize.height,
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: options.bitrateMbps * 1_000_000,
                AVVideoExpectedSourceFrameRateKey: options.fps,
                AVVideoMaxKeyFrameIntervalKey: options.fps,   // a keyframe every second keeps trimming accurate
                AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
            ] as [String: Any],
        ]
        let video = AVAssetWriterInput(mediaType: .video, outputSettings: videoSettings)
        video.expectsMediaDataInRealTime = true
        guard w.canAdd(video) else { throw RecordingError.writer("unsupported video settings") }
        w.add(video)
        input = video

        // Desktop audio and the microphone are separate tracks, so either can be muted later without touching the other.
        // Each carries a language tag so the editor knows which is which. The upload copy is mixed to one track.
        func audioTrack(tag: String) -> (AVAssetWriterInput, AudioMixer)? {
            let input = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC, AVNumberOfChannelsKey: 2,
                AVSampleRateKey: 48_000, AVEncoderBitRateKey: 128_000,
            ])
            input.expectsMediaDataInRealTime = true
            input.extendedLanguageTag = tag
            guard w.canAdd(input) else { return nil }
            w.add(input)
            let mixer = AudioMixer { [weak self, weak input] sb in
                guard let self, let input, self.writer?.status == .writing, input.isReadyForMoreMediaData else { return }
                input.append(sb)
            }
            return (input, mixer)
        }
        if options.systemAudio, let t = audioTrack(tag: AudioRole.system.tag!) { systemInput = t.0; systemMixer = t.1 }
        if options.mic, let t = audioTrack(tag: AudioRole.mic.tag!) { micInput = t.0; micMixer = t.1 }
        writer = w

        let s = SCStream(filter: filter, configuration: cfg, delegate: self)
        try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        if options.systemAudio { try s.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue) }
        if options.mic { startMic(deviceID: options.micDeviceID) }
        try await s.startCapture()
        stream = s
        startFlushTimer()
    }

    // MARK: Microphone

    private func startMic(deviceID: String) {
        let device = (deviceID.isEmpty ? nil : AVCaptureDevice(uniqueID: deviceID)) ?? AVCaptureDevice.default(for: .audio)
        guard let device, let micInput = try? AVCaptureDeviceInput(device: device) else {
            onWarning?("Couldn't open the microphone. Recording without it.")
            return
        }
        let session = AVCaptureSession()
        let out = AVCaptureAudioDataOutput()
        out.setSampleBufferDelegate(self, queue: queue)
        guard session.canAddInput(micInput), session.canAddOutput(out) else {
            onWarning?("The microphone can't be used for recording. Recording without it.")
            return
        }
        session.addInput(micInput)
        session.addOutput(out)
        micSession = session
        DispatchQueue.global(qos: .userInitiated).async { session.startRunning() }   // blocks until running
    }

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        handleAudio(sampleBuffer, source: .mic)
    }

    // MARK: Control

    func pause() {
        queue.async {
            guard !self.paused else { return }
            self.paused = true
            self.pausedAt = Self.hostNow
            // Everything captured so far goes out now; mic and desktop audio both stop until resume.
            let upTo = CMTimeSubtract(self.pausedAt, self.pauseOffset)
            self.systemMixer?.flush(through: upTo)
            self.micMixer?.flush(through: upTo)
        }
    }

    func resume() {
        queue.async {
            guard self.paused else { return }
            let now = Self.hostNow
            self.pauseOffset = CMTimeAdd(self.pauseOffset, CMTimeSubtract(now, self.pausedAt))
            self.dropBefore = now
            self.paused = false
        }
    }

    /// Finishes the file. Returns true if a playable video was written.
    func stop() async -> Bool {
        stopInputs()
        try? await stream?.stopCapture()
        stream = nil
        return await withCheckedContinuation { cont in
            queue.async {
                guard let writer = self.writer, let input = self.input, self.sessionStarted, writer.status == .writing else {
                    self.writer?.cancelWriting()
                    cont.resume(returning: false)
                    return
                }
                // Static screens deliver few frames; end the timeline at the real stop time so the video has the right length.
                var end = self.paused ? self.pausedAt : Self.hostNow
                end = CMTimeSubtract(end, self.pauseOffset)
                if CMTimeCompare(end, self.lastPTS) < 0 { end = self.lastPTS }

                // The audio runs to the exact stop time, but the last video frame only covers a frame or so (and on a
                // still screen, frames stop arriving long before). Without this the file ends with black. Hold the last
                // frame until the end by writing one more copy of it.
                if let last = self.lastSample, CMTimeCompare(CMTimeSubtract(end, self.lastSampleEnd), CMTime(value: 1, timescale: 1000)) > 0 {
                    let t = CMTimeMaximum(self.lastSampleEnd, CMTimeSubtract(end, self.frameDuration))
                    var timing = CMSampleTimingInfo(duration: CMTimeSubtract(end, t), presentationTimeStamp: t, decodeTimeStamp: .invalid)
                    var filler: CMSampleBuffer?
                    CMSampleBufferCreateCopyWithNewTiming(allocator: nil, sampleBuffer: last, sampleTimingEntryCount: 1,
                                                          sampleTimingArray: &timing, sampleBufferOut: &filler)
                    var waits = 0
                    while !input.isReadyForMoreMediaData && waits < 50 { Thread.sleep(forTimeInterval: 0.01); waits += 1 }
                    if let filler, input.isReadyForMoreMediaData, input.append(filler) { self.lastPTS = t }
                }
                self.lastSample = nil
                self.systemMixer?.flush(through: end)
                self.micMixer?.flush(through: end)
                input.markAsFinished()
                self.systemInput?.markAsFinished()
                self.micInput?.markAsFinished()
                writer.endSession(atSourceTime: end)
                writer.finishWriting { cont.resume(returning: writer.status == .completed) }
            }
        }
    }

    func abort() async {
        stopInputs()
        try? await stream?.stopCapture()
        stream = nil
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            queue.async {
                if self.writer?.status == .writing { self.writer?.cancelWriting() }
                cont.resume()
            }
        }
        try? FileManager.default.removeItem(at: outputURL)
    }

    private func stopInputs() {
        flushTimer?.cancel()
        flushTimer = nil
        if let micSession { DispatchQueue.global().async { micSession.stopRunning() } }
        micSession = nil
    }

    private func startFlushTimer() {
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + 0.1, repeating: 0.1)
        t.setEventHandler { [weak self] in
            guard let self, self.sessionStarted, !self.paused else { return }
            let target = CMTimeSubtract(CMTimeSubtract(Self.hostNow, self.pauseOffset), Self.audioLatency)
            self.systemMixer?.flush(through: target)
            self.micMixer?.flush(through: target)
        }
        t.resume()
        flushTimer = t
    }

    // MARK: Samples (all on `queue`)

    func stream(_ stream: SCStream, didOutputSampleBuffer sb: CMSampleBuffer, of type: SCStreamOutputType) {
        switch type {
        case .screen: handleVideo(sb)
        case .audio: handleAudio(sb, source: .system)
        default: break
        }
    }

    private func handleVideo(_ sb: CMSampleBuffer) {
        guard sb.isValid, !paused, let writer, let input else { return }
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let raw = attachments.first?[.status] as? Int,
              raw == SCFrameStatus.complete.rawValue else { return }   // skip idle / blank frames

        let pts = sb.presentationTimeStamp
        if CMTimeCompare(pts, dropBefore) < 0 { return }
        let adjusted = CMTimeSubtract(pts, pauseOffset)
        if CMTimeCompare(adjusted, lastPTS) <= 0 { return }

        if !sessionStarted {
            guard writer.startWriting() else {
                onError?(writer.error?.localizedDescription ?? "The video file couldn't be written.")
                return
            }
            writer.startSession(atSourceTime: adjusted)
            systemMixer?.begin(base: adjusted)
            micMixer?.begin(base: adjusted)
            sessionStarted = true
        }
        guard writer.status == .writing, input.isReadyForMoreMediaData else { return }

        var timing = CMSampleTimingInfo(duration: sb.duration, presentationTimeStamp: adjusted, decodeTimeStamp: .invalid)
        var retimed: CMSampleBuffer?
        CMSampleBufferCreateCopyWithNewTiming(allocator: nil, sampleBuffer: sb, sampleTimingEntryCount: 1,
                                               sampleTimingArray: &timing, sampleBufferOut: &retimed)
        if let retimed, input.append(retimed) {
            lastPTS = adjusted
            lastSample = retimed
            lastSampleEnd = CMTimeAdd(adjusted, sb.duration.isValid ? sb.duration : frameDuration)
        }
    }

    private func handleAudio(_ sb: CMSampleBuffer, source: AudioMixer.Source) {
        guard sessionStarted, !paused, sb.isValid else { return }
        let pts = sb.presentationTimeStamp
        if CMTimeCompare(pts, dropBefore) < 0 { return }
        let adjusted = CMTimeSubtract(pts, pauseOffset)
        switch source {
        case .system: systemMixer?.add(sb, adjustedPTS: adjusted, source: .system)
        case .mic: micMixer?.add(sb, adjustedPTS: adjusted, source: .mic)
        }
    }

    // MARK: SCStreamDelegate

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        onError?(error.localizedDescription)
    }
}
