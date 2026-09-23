import AVFoundation
import CoreImage
import Metal

enum RenderError: LocalizedError {
    case noVideoTrack
    case failed(String)

    var errorDescription: String? {
        switch self {
        case .noVideoTrack: return "The recording has no video track."
        case .failed(let msg): return "Rendering failed: \(msg)"
        }
    }
}

/// Exports a recording to an .mp4 at full quality, frame by frame.
final class Renderer {
    private let session: RecordingSession
    private let settings: RenderSettings
    private let cursor: CursorArt
    private let context: CIContext

    init(session: RecordingSession, settings: RenderSettings, cursor: CursorArt) {
        self.session = session
        self.settings = settings
        self.cursor = cursor
        if let device = MTLCreateSystemDefaultDevice() {
            context = CIContext(mtlDevice: device, options: [.cacheIntermediates: false])
        } else {
            context = CIContext()
        }
    }

    func render(to finalURL: URL, progress: @escaping (Double) -> Void) async throws {
        // With sounds, render the picture to a temporary file, then add the audio track.
        var hits: [InputSounds.Hit] = []
        if settings.clickSounds { hits += InputSounds.clickHits(session) }
        if settings.keyboardSounds { hits += InputSounds.keyHits(session) }
        let url = !hits.isEmpty
            ? FileManager.default.temporaryDirectory.appendingPathComponent("ScreenPlus-\(UUID().uuidString).mp4")
            : finalURL
        defer { if url != finalURL { try? FileManager.default.removeItem(at: url) } }

        let asset = AVURLAsset(url: session.videoURL)
        guard let track = try await asset.loadTracks(withMediaType: .video).first else { throw RenderError.noVideoTrack }
        let duration = try await asset.load(.duration).seconds

        let composer = FrameComposer(session: session, settings: settings, cursor: cursor, duration: duration,
                                     outputWidth: settings.outputWidth)
        let canvas = composer.canvas

        // Reader
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
        ])
        output.alwaysCopiesSampleData = false
        reader.add(output)
        guard reader.startReading() else { throw RenderError.failed(reader.error?.localizedDescription ?? "reader") }

        // Writer
        try? FileManager.default.removeItem(at: url)
        let writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: Int(canvas.width),
            AVVideoHeightKey: Int(canvas.height),
            AVVideoColorPropertiesKey: [
                AVVideoColorPrimariesKey: AVVideoColorPrimaries_ITU_R_709_2,
                AVVideoTransferFunctionKey: AVVideoTransferFunction_ITU_R_709_2,
                AVVideoYCbCrMatrixKey: AVVideoYCbCrMatrix_ITU_R_709_2,
            ],
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: 20_000_000,
                AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
                AVVideoExpectedSourceFrameRateKey: settings.fps,
            ],
        ])
        input.expectsMediaDataInRealTime = false
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: Int(canvas.width),
            kCVPixelBufferHeightKey as String: Int(canvas.height),
            kCVPixelBufferMetalCompatibilityKey as String: true,
        ])
        writer.add(input)
        guard writer.startWriting() else { throw RenderError.failed(writer.error?.localizedDescription ?? "writer") }
        writer.startSession(atSourceTime: .zero)

        let colorSpace = CGColorSpace(name: CGColorSpace.sRGB)!
        let fps = Double(settings.fps)
        let frameCount = max(1, Int(duration * fps))

        // Source frames arrive at a variable rate (ScreenCaptureKit skips unchanged frames),
        // so we hold the latest frame at or before each output time.
        var basePTS: Double?
        var current: CIImage?
        var next = output.copyNextSampleBuffer()
        func time(of buffer: CMSampleBuffer) -> Double {
            let pts = buffer.presentationTimeStamp.seconds
            if basePTS == nil { basePTS = pts }
            return pts - basePTS!
        }

        for frame in 0..<frameCount {
            try Task.checkCancellation()
            let t = Double(frame) / fps

            while let buffer = next, current == nil || time(of: buffer) <= t + 0.001 {
                if let pixels = buffer.imageBuffer { current = CIImage(cvPixelBuffer: pixels) }
                next = output.copyNextSampleBuffer()
            }
            guard let source = current else { break }

            let image = composer.frame(source: source, time: t)

            while !input.isReadyForMoreMediaData {
                try await Task.sleep(nanoseconds: 2_000_000)
            }
            guard let pool = adaptor.pixelBufferPool else { throw RenderError.failed("no pixel buffer pool") }
            var pixelBuffer: CVPixelBuffer?
            CVPixelBufferPoolCreatePixelBuffer(nil, pool, &pixelBuffer)
            guard let pixelBuffer else { throw RenderError.failed("could not allocate frame") }
            context.render(image, to: pixelBuffer, bounds: canvas, colorSpace: colorSpace)
            adaptor.append(pixelBuffer, withPresentationTime: CMTime(value: CMTimeValue(frame), timescale: CMTimeScale(settings.fps)))

            if frame % 10 == 0 { progress(Double(frame) / Double(frameCount)) }
        }

        reader.cancelReading()
        input.markAsFinished()
        await writer.finishWriting()
        if writer.status != .completed {
            throw RenderError.failed(writer.error?.localizedDescription ?? "writer did not complete")
        }

        if !hits.isEmpty {
            let audioURL = InputSounds.temporaryURL("sounds")
            defer { try? FileManager.default.removeItem(at: audioURL) }
            try InputSounds.writeTrack(hits, duration: duration, to: audioURL)
            try await InputSounds.mux(video: url, audio: audioURL, to: finalURL)
        }
        progress(1)
    }
}
