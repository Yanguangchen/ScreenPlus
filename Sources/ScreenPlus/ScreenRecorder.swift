import AppKit
import AVFoundation
import ScreenCaptureKit

enum RecorderError: LocalizedError {
    case noDisplay
    case noFrames
    case writerFailed(String)

    var errorDescription: String? {
        switch self {
        case .noDisplay: return "Could not find a display to record."
        case .noFrames: return "No video frames were captured."
        case .writerFailed(let msg): return "Video writer failed: \(msg)"
        }
    }
}

/// Records one display (without the cursor) to a .mov file using ScreenCaptureKit.
final class ScreenRecorder: NSObject, SCStreamOutput, SCStreamDelegate {
    struct Result {
        var url: URL
        var width: Int
        var height: Int
        /// Host time (CACurrentMediaTime clock) of the first written frame.
        var firstFrameHostTime: Double
    }

    /// Frame of the recorded screen in global AppKit coordinates (points, bottom-left origin).
    private(set) var screenFrame: CGRect = .zero
    private(set) var pixelsPerPoint: Double = 1

    private var stream: SCStream?
    private var writer: AVAssetWriter?
    private var input: AVAssetWriterInput?
    private var adaptor: AVAssetWriterInputPixelBufferAdaptor?
    private let queue = DispatchQueue(label: "ScreenPlus.capture", qos: .userInitiated)

    // Only touched on `queue`.
    private var firstFrameTime: CMTime?
    private var lastFrameTime: CMTime = .zero
    private var outputURL: URL?
    private var size = (width: 0, height: 0)

    /// Starts recording the screen that currently contains the mouse.
    @MainActor
    func start(to url: URL) async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)

        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) } ?? NSScreen.main
        guard let screen,
              let displayID = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID,
              let display = content.displays.first(where: { $0.displayID == displayID }) ?? content.displays.first
        else { throw RecorderError.noDisplay }

        // Keep our own window out of the recording.
        let ownApp = content.applications.filter { $0.processID == ProcessInfo.processInfo.processIdentifier }
        let filter = SCContentFilter(display: display, excludingApplications: ownApp, exceptingWindows: [])

        let scale = Double(filter.pointPixelScale)
        let width = Int(filter.contentRect.width * scale) & ~1
        let height = Int(filter.contentRect.height * scale) & ~1
        screenFrame = screen.frame
        pixelsPerPoint = scale

        let config = SCStreamConfiguration()
        config.width = width
        config.height = height
        config.minimumFrameInterval = CMTime(value: 1, timescale: 60)
        config.showsCursor = false  // we draw our own, smoothed cursor later
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.queueDepth = 6

        try? FileManager.default.removeItem(at: url)
        let writer = try AVAssetWriter(outputURL: url, fileType: .mov)
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.hevc,
            AVVideoWidthKey: width,
            AVVideoHeightKey: height,
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: max(width * height * 6, 10_000_000),
                AVVideoExpectedSourceFrameRateKey: 60,
            ],
        ])
        input.expectsMediaDataInRealTime = true
        writer.add(input)
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: nil)

        queue.sync {
            self.writer = writer
            self.input = input
            self.adaptor = adaptor
            self.firstFrameTime = nil
            self.outputURL = url
            self.size = (width, height)
        }

        let stream = SCStream(filter: filter, configuration: config, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        try await stream.startCapture()
        self.stream = stream
    }

    func stop() async throws -> Result {
        if let stream {
            try? await stream.stopCapture()
        }
        stream = nil

        return try await withCheckedThrowingContinuation { cont in
            queue.async {
                guard let writer = self.writer, let input = self.input, let first = self.firstFrameTime,
                      let url = self.outputURL
                else {
                    self.writer?.cancelWriting()
                    cont.resume(throwing: RecorderError.noFrames)
                    return
                }
                // ScreenCaptureKit only delivers frames when the screen changes, so extend the
                // video to "now" so a static ending is kept.
                let now = CMClockGetTime(CMClockGetHostTimeClock())
                input.markAsFinished()
                writer.endSession(atSourceTime: max(now, self.lastFrameTime))
                let size = self.size
                writer.finishWriting {
                    if writer.status == .completed {
                        cont.resume(returning: Result(url: url, width: size.width, height: size.height,
                                                      firstFrameHostTime: first.seconds))
                    } else {
                        cont.resume(throwing: RecorderError.writerFailed(writer.error?.localizedDescription ?? "unknown"))
                    }
                }
                self.writer = nil
                self.input = nil
                self.adaptor = nil
            }
        }
    }

    // MARK: SCStreamOutput

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
                as? [[SCStreamFrameInfo: Any]],
              let statusRaw = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: statusRaw) == .complete,
              let pixelBuffer = sampleBuffer.imageBuffer,
              let writer, let input, let adaptor
        else { return }

        let pts = sampleBuffer.presentationTimeStamp
        if firstFrameTime == nil {
            guard writer.startWriting() else { return }
            writer.startSession(atSourceTime: pts)
            firstFrameTime = pts
        }
        if input.isReadyForMoreMediaData, writer.status == .writing {
            adaptor.append(pixelBuffer, withPresentationTime: pts)
            lastFrameTime = pts
        }
    }

    // MARK: SCStreamDelegate

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        NSLog("ScreenPlus: stream stopped with error: \(error)")
    }
}
