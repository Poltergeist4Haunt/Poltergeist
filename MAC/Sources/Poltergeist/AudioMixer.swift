import AVFoundation
import CoreMedia

/// Mixes desktop audio and microphone into one 48 kHz stereo stream on the recording's timeline.
/// Not thread-safe: call everything from the recording session's queue.
///
/// Both sources arrive on their own clocks in their own formats. Each buffer is converted to
/// 48 kHz stereo Float32 and summed into a pending window at its timeline position; the window
/// is emitted in chunks once it's old enough that late buffers have had time to arrive.
final class AudioMixer {
    enum Source { case system, mic }
    static let rate = 48_000.0

    private let outFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: AudioMixer.rate, channels: 2, interleaved: true)!
    private let emit: (CMSampleBuffer) -> Void
    private var base = CMTime.invalid
    private var emitIndex: Int64 = 0            // timeline position (frames) of mix[0]
    private var mix: [Float] = []               // interleaved stereo, pending
    private var nextIndex: [Source: Int64] = [:]
    private var converters: [Source: (format: AVAudioFormat, converter: AVAudioConverter)] = [:]

    init(emit: @escaping (CMSampleBuffer) -> Void) { self.emit = emit }

    var isReady: Bool { base.isValid }

    /// Timeline zero: the first video frame's adjusted timestamp.
    func begin(base: CMTime) { self.base = base }

    func add(_ sb: CMSampleBuffer, adjustedPTS: CMTime, source: Source) {
        guard base.isValid, let samples = convert(sb, source) else { return }
        let frames = Int64(samples.count / 2)
        var idx = Int64((CMTimeSubtract(adjustedPTS, base).seconds * Self.rate).rounded())
        // Timestamps jitter by a few samples; keep each source contiguous unless it really jumped.
        if let n = nextIndex[source], abs(idx - n) < 480 { idx = n }
        nextIndex[source] = idx + frames

        var skip = 0                                   // samples already emitted (late) or before timeline zero
        if idx < emitIndex {
            let late = emitIndex - idx
            if late >= frames { return }
            skip = Int(late) * 2
            idx = emitIndex
        }
        let rel = Int(idx - emitIndex) * 2
        let needed = rel + samples.count - skip
        if mix.count < needed { mix.append(contentsOf: repeatElement(0, count: needed - mix.count)) }
        for i in skip..<samples.count { mix[rel + i - skip] += samples[i] }
    }

    /// Emits everything up to `time` (adjusted timeline). Leaves < 20 ms pending.
    func flush(through time: CMTime) {
        guard base.isValid else { return }
        var frames = Int64((CMTimeSubtract(time, base).seconds * Self.rate).rounded()) - emitIndex
        while frames >= 960 {
            let chunk = Int(min(frames, 9_600))
            var out = [Float](repeating: 0, count: chunk * 2)
            let have = min(mix.count, chunk * 2)
            for i in 0..<have { out[i] = max(-1, min(1, mix[i])) }
            mix.removeFirst(have)
            let pts = CMTimeAdd(base, CMTime(value: emitIndex, timescale: CMTimeScale(Self.rate)))
            if let sb = makeSampleBuffer(out, pts: pts) { emit(sb) }
            emitIndex += Int64(chunk)
            frames -= Int64(chunk)
        }
    }

    // MARK: Conversion

    private func convert(_ sb: CMSampleBuffer, _ source: Source) -> [Float]? {
        guard let desc = sb.formatDescription else { return nil }
        let inFmt = AVAudioFormat(cmAudioFormatDescription: desc)
        let frames = AVAudioFrameCount(CMSampleBufferGetNumSamples(sb))
        guard frames > 0, let inBuf = AVAudioPCMBuffer(pcmFormat: inFmt, frameCapacity: frames) else { return nil }
        inBuf.frameLength = frames
        guard CMSampleBufferCopyPCMDataIntoAudioBufferList(sb, at: 0, frameCount: Int32(frames),
                                                           into: inBuf.mutableAudioBufferList) == noErr else { return nil }

        let converter: AVAudioConverter
        if let c = converters[source], c.format == inFmt {
            converter = c.converter
        } else {
            guard let c = AVAudioConverter(from: inFmt, to: outFormat) else { return nil }
            if inFmt.channelCount == 1 { c.channelMap = [0, 0] }   // mono → both ears
            converters[source] = (inFmt, c)
            converter = c
        }

        let capacity = AVAudioFrameCount(Double(frames) * Self.rate / inFmt.sampleRate) + 256
        guard let out = AVAudioPCMBuffer(pcmFormat: outFormat, frameCapacity: capacity) else { return nil }
        var supplied = false
        var error: NSError?
        converter.convert(to: out, error: &error) { _, status in
            if supplied { status.pointee = .noDataNow; return nil }
            supplied = true
            status.pointee = .haveData
            return inBuf
        }
        guard error == nil, out.frameLength > 0 else { return nil }
        let count = Int(out.frameLength) * 2
        let abl = UnsafeMutableAudioBufferListPointer(out.mutableAudioBufferList)
        guard let data = abl[0].mData else { return nil }
        return Array(UnsafeBufferPointer(start: data.assumingMemoryBound(to: Float.self), count: count))
    }

    private func makeSampleBuffer(_ samples: [Float], pts: CMTime) -> CMSampleBuffer? {
        var asbd = outFormat.streamDescription.pointee
        var format: CMAudioFormatDescription?
        guard CMAudioFormatDescriptionCreate(allocator: nil, asbd: &asbd, layoutSize: 0, layout: nil, magicCookieSize: 0,
                                             magicCookie: nil, extensions: nil, formatDescriptionOut: &format) == noErr,
              let format else { return nil }
        let bytes = samples.count * MemoryLayout<Float>.size
        var block: CMBlockBuffer?
        guard CMBlockBufferCreateWithMemoryBlock(allocator: nil, memoryBlock: nil, blockLength: bytes, blockAllocator: nil,
                                                 customBlockSource: nil, offsetToData: 0, dataLength: bytes, flags: 0,
                                                 blockBufferOut: &block) == noErr, let block,
              CMBlockBufferAssureBlockMemory(block) == noErr else { return nil }
        let copied = samples.withUnsafeBytes {
            CMBlockBufferReplaceDataBytes(with: $0.baseAddress!, blockBuffer: block, offsetIntoDestination: 0, dataLength: bytes)
        }
        guard copied == noErr else { return nil }
        var sb: CMSampleBuffer?
        guard CMAudioSampleBufferCreateReadyWithPacketDescriptions(
            allocator: nil, dataBuffer: block, formatDescription: format, sampleCount: samples.count / 2,
            presentationTimeStamp: pts, packetDescriptions: nil, sampleBufferOut: &sb) == noErr else { return nil }
        return sb
    }
}
