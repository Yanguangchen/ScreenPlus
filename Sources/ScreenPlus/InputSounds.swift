import AVFoundation

/// Synthesized mouse-click and keyboard sounds, and the audio tracks built from them.
///
/// Everything is generated in code (no audio assets). Randomness is seeded so a recording
/// always sounds the same on every preview and export.
enum InputSounds {
    static let sampleRate = 48_000.0

    struct Hit {
        var time: Double
        var sound: [Float]
    }

    // MARK: Events → hits

    static func clickHits(_ session: RecordingSession) -> [Hit] {
        session.clicks.map { Hit(time: $0.t, sound: mouseClick) }
    }

    static func keyHits(_ session: RecordingSession) -> [Hit] {
        (session.keys ?? []).enumerated().map { index, key in
            let sound: [Float]
            switch key.kind {
            case .space: sound = spaceKey
            case .enter: sound = enterKey
            case .delete: sound = deleteKey
            case .regular: sound = regularKeys[(index * 7 + 3) % regularKeys.count]
            }
            return Hit(time: key.t, sound: sound)
        }
    }

    // MARK: Sounds

    /// Sharp press "snap" followed by a softer release, like a real mouse button.
    static let mouseClick: [Float] = {
        var out = buffer(seconds: 0.13)
        var rng = Noise(seed: 0xC11C)
        mix(into: &out, at: 0, gain: 1.0) { t in
            let snap = rng.highpassed() * exp(-t * 2_600) * 1.4
            let ping = sin(2 * .pi * 5_200 * t) * exp(-t * 1_900) * 0.8
            let tick = sin(2 * .pi * 2_700 * t) * exp(-t * 1_000) * 0.6
            let body = sin(2 * .pi * 950 * t) * exp(-t * 380) * 0.35
            return snap + ping + tick + body
        }
        mix(into: &out, at: 0.075, gain: 0.5) { t in
            let snap = rng.highpassed() * exp(-t * 3_200) * 1.2
            let ping = sin(2 * .pi * 4_600 * t) * exp(-t * 2_200) * 0.7
            let body = sin(2 * .pi * 1_100 * t) * exp(-t * 500) * 0.3
            return snap + ping + body
        }
        return normalized(out, peak: 0.95)
    }()

    /// A handful of slightly different mechanical key sounds so typing doesn't sound robotic.
    static let regularKeys: [[Float]] = (0..<6).map { i in
        let v = Double(i)
        return keySound(seed: UInt32(100 + i), thock: 210 + v * 14, plate: 1_750 + v * 90,
                        release: 0.085 + v * 0.004, peak: 0.55 + Float(i % 3) * 0.05)
    }
    static let spaceKey = keySound(seed: 7, thock: 140, plate: 1_300, release: 0.11, peak: 0.7, length: 0.24)
    static let enterKey = keySound(seed: 8, thock: 170, plate: 1_500, release: 0.1, peak: 0.75, length: 0.22)
    static let deleteKey = keySound(seed: 9, thock: 195, plate: 1_650, release: 0.09, peak: 0.6)

    /// Key press: a click of the switch, the "thock" of bottoming out, and a ring from the plate;
    /// then a quieter key release.
    private static func keySound(seed: UInt32, thock: Double, plate: Double, release: Double,
                                 peak: Float, length: Double = 0.2) -> [Float] {
        var out = buffer(seconds: length)
        var rng = Noise(seed: seed)
        mix(into: &out, at: 0, gain: 1.0) { t in
            let click = rng.highpassed() * exp(-t * 1_500) * 0.9
            let bottom = sin(2 * .pi * thock * t) * exp(-t * 55) * 0.9
            let ring = sin(2 * .pi * plate * t) * exp(-t * 420) * 0.3
            return click + bottom + ring
        }
        mix(into: &out, at: release, gain: 0.35) { t in
            let click = rng.highpassed() * exp(-t * 2_000) * 0.8
            let bottom = sin(2 * .pi * thock * 1.25 * t) * exp(-t * 80) * 0.6
            return click + bottom
        }
        return normalized(out, peak: peak)
    }

    // MARK: Track writing

    /// Writes an AAC (.m4a) track of `duration` seconds with every hit mixed in at its time.
    static func writeTrack(_ hits: [Hit], duration: Double, to url: URL) throws {
        try? FileManager.default.removeItem(at: url)
        let format = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: 1)!
        let file = try AVAudioFile(forWriting: url, settings: [
            AVFormatIDKey: kAudioFormatMPEG4AAC,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: 1,
            AVEncoderBitRateKey: 128_000,
        ], commonFormat: .pcmFormatFloat32, interleaved: false)

        let totalFrames = Int(max(duration, 0.1) * sampleRate)
        let placed = hits.filter { $0.time >= 0 && $0.time < duration }
            .map { (start: Int($0.time * sampleRate), sound: $0.sound) }
            .sorted { $0.start < $1.start }
        let chunk = Int(sampleRate)  // one second at a time

        var chunkStart = 0
        while chunkStart < totalFrames {
            let count = min(chunk, totalFrames - chunkStart)
            let pcm = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(count))!
            pcm.frameLength = AVAudioFrameCount(count)
            let data = pcm.floatChannelData![0]
            data.initialize(repeating: 0, count: count)

            for hit in placed where hit.start < chunkStart + count && hit.start + hit.sound.count > chunkStart {
                let from = max(chunkStart, hit.start), to = min(chunkStart + count, hit.start + hit.sound.count)
                for frame in from..<to {
                    data[frame - chunkStart] += hit.sound[frame - hit.start]
                }
            }
            for i in 0..<count { data[i] = max(-1, min(1, data[i])) }  // overlapping hits can't clip
            try file.write(from: pcm)
            chunkStart += count
        }
    }

    /// Combines a video file with an audio track into an .mp4 without re-encoding the video.
    static func mux(video: URL, audio: URL, to output: URL) async throws {
        let (composition, _) = try await combine(video: AVURLAsset(url: video), audio: [audio])
        try? FileManager.default.removeItem(at: output)
        guard let export = AVAssetExportSession(asset: composition, presetName: AVAssetExportPresetPassthrough) else {
            throw RenderError.failed("could not create audio export session")
        }
        try await export.export(to: output, as: .mp4)
    }

    /// A composition with the video's picture plus one audio track per file.
    /// Returns the audio track IDs in the same order, so the preview can mute them individually.
    static func combine(video videoAsset: AVAsset, audio: [URL]) async throws
        -> (AVMutableComposition, [CMPersistentTrackID])
    {
        let composition = AVMutableComposition()
        let duration = try await videoAsset.load(.duration)

        if let track = try await videoAsset.loadTracks(withMediaType: .video).first,
           let target = composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid) {
            try target.insertTimeRange(CMTimeRange(start: .zero, duration: duration), of: track, at: .zero)
        }

        var audioIDs: [CMPersistentTrackID] = []
        for url in audio {
            let asset = AVURLAsset(url: url)
            guard let track = try await asset.loadTracks(withMediaType: .audio).first,
                  let target = composition.addMutableTrack(withMediaType: .audio,
                                                           preferredTrackID: kCMPersistentTrackID_Invalid)
            else { continue }
            let audioDuration = try await asset.load(.duration)
            try target.insertTimeRange(CMTimeRange(start: .zero, duration: min(duration, audioDuration)),
                                       of: track, at: .zero)
            audioIDs.append(target.trackID)
        }
        return (composition, audioIDs)
    }

    static func temporaryURL(_ name: String) -> URL {
        FileManager.default.temporaryDirectory.appendingPathComponent("ScreenPlus-\(name)-\(UUID().uuidString).m4a")
    }

    // MARK: Synthesis helpers

    private static func buffer(seconds: Double) -> [Float] {
        [Float](repeating: 0, count: Int(sampleRate * seconds))
    }

    private static func mix(into out: inout [Float], at offset: Double, gain: Double, _ voice: (Double) -> Double) {
        let start = Int(offset * sampleRate)
        for i in start..<out.count {
            out[i] += Float(voice(Double(i - start) / sampleRate) * gain)
        }
    }

    private static func normalized(_ samples: [Float], peak: Float) -> [Float] {
        let maxValue = samples.map(abs).max() ?? 1
        guard maxValue > 0 else { return samples }
        return samples.map { $0 / maxValue * peak }
    }

    /// Deterministic white noise, with a first-order high-pass for a brighter "snap".
    private struct Noise {
        var state: UInt32
        var previous: Double = 0

        init(seed: UInt32) { state = seed &* 2_654_435_761 | 1 }

        mutating func next() -> Double {
            state = state &* 1_664_525 &+ 1_013_904_223
            return Double(state >> 8) / Double(1 << 24) * 2 - 1
        }

        mutating func highpassed() -> Double {
            let x = next()
            defer { previous = x }
            return (x - previous) * 0.5
        }
    }
}
