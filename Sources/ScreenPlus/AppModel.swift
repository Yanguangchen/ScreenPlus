import AppKit
import AVFoundation
import SwiftUI
import UniformTypeIdentifiers

@MainActor
final class AppModel: ObservableObject {
    enum Phase {
        case idle
        case starting
        case recording(since: Date)
        case processing
        case editing
        case failed(String)
    }

    @Published var phase: Phase = .idle
    @Published var settings = RenderSettings() {
        didSet {
            updatePreviewAudio()
            schedulePreviewUpdate()
        }
    }

    // Editor state
    @Published private(set) var session: RecordingSession?
    @Published private(set) var duration: Double = 0
    @Published private(set) var segments: [CameraPath.Segment] = []
    @Published private(set) var previewSize = CGSize(width: 16, height: 10)
    @Published private(set) var exportProgress: Double?
    @Published private(set) var exportedURL: URL?
    let player = AVPlayer()

    private let recorder = ScreenRecorder()
    private let tracker = InputTracker()
    private let toolbar = ToolbarController()
    private weak var mainWindow: NSWindow?
    private var folder: URL?
    private var previewAsset: AVAsset?
    private var previewAudioURLs: [URL] = []
    /// Preview audio tracks: [clicks, keys].
    private var previewAudioTrackIDs: [CMPersistentTrackID] = []
    private var previewTask: Task<Void, Never>?
    private var exportTask: Task<Void, Never>?
    private lazy var cursor = Self.cursorArt()

    /// Preview renders smaller and with fewer blur samples so it plays back in real time.
    private static let previewWidth = 1280
    private static let previewBlurSamples = 6

    var isRecording: Bool {
        if case .recording = phase { return true }
        return false
    }

    var isBusy: Bool {
        switch phase {
        case .starting, .recording, .processing: return true
        default: return exportProgress != nil
        }
    }

    // MARK: Windows

    /// Called once the main window exists. On launch we start in the floating toolbar.
    func registerMainWindow(_ window: NSWindow) {
        let firstTime = mainWindow == nil
        mainWindow = window
        if firstTime, case .idle = phase {
            showRecorder()
        }
    }

    /// Hides the main window and shows the floating recording toolbar.
    func showRecorder() {
        guard !isBusy else { return }
        player.pause()
        mainWindow?.orderOut(nil)
        toolbar.show(model: self)
    }

    /// Hides the toolbar and brings back the main window (home or editor).
    func hideRecorder() {
        guard !isRecording else { return }
        toolbar.hide()
        showMainWindow()
    }

    private func showMainWindow() {
        NSApp.activate()
        mainWindow?.makeKeyAndOrderFront(nil)
    }

    // MARK: Recording

    func startRecording() {
        guard !isBusy else { return }
        guard CGPreflightScreenCaptureAccess() || CGRequestScreenCaptureAccess() else {
            phase = .failed("ScreenPlus needs Screen Recording permission. Enable it in System Settings → Privacy & Security → Screen & System Audio Recording, then relaunch the app.")
            hideRecorder()
            return
        }

        player.pause()
        InputTracker.requestKeyboardAccessIfNeeded()
        let folder = Self.recordingsRoot.appendingPathComponent("Recording \(Self.timestamp())", isDirectory: true)
        self.folder = folder
        phase = .starting

        Task {
            do {
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                try await recorder.start(to: folder.appendingPathComponent("raw.mov"))
                tracker.start()
                phase = .recording(since: Date())
            } catch {
                phase = .failed(error.localizedDescription)
                hideRecorder()
            }
        }
    }

    func stopRecording() {
        guard isRecording, let folder else { return }
        let events = tracker.stop()
        phase = .processing
        toolbar.hide()
        showMainWindow()

        Task {
            do {
                let result = try await recorder.stop()
                let session = makeSession(from: result, events: events)
                try session.save(to: folder.appendingPathComponent("events.json"))
                try await openEditor(session: session, folder: folder)
            } catch {
                phase = .failed(error.localizedDescription)
            }
        }
    }

    /// Stops recording and throws the footage away; the toolbar stays up for another take.
    func discardRecording() {
        guard isRecording, let folder else { return }
        _ = tracker.stop()
        phase = .processing
        Task {
            _ = try? await recorder.stop()
            try? FileManager.default.removeItem(at: folder)
            phase = session == nil ? .idle : .editing
        }
    }

    /// Converts raw input events (host time, global points) into video time and source pixels.
    private func makeSession(from result: ScreenRecorder.Result, events: [InputTracker.RawEvent]) -> RecordingSession {
        let frame = recorder.screenFrame
        let scale = Double(result.width) / frame.width
        func convert(_ e: InputTracker.RawEvent) -> (t: Double, x: Double, y: Double) {
            (e.time - result.firstFrameHostTime,
             (e.point.x - frame.minX) * scale,
             (frame.maxY - e.point.y) * scale)
        }
        var cursor: [CursorSample] = [], clicks: [ClickEvent] = [], keys: [KeyEvent] = []
        for event in events {
            let p = convert(event)
            switch event.kind {
            case .move: cursor.append(CursorSample(t: p.t, x: p.x, y: p.y))
            case .click: clicks.append(ClickEvent(t: p.t, x: p.x, y: p.y))
            case .key(let kind): keys.append(KeyEvent(t: p.t, kind: kind))
            }
        }
        return RecordingSession(videoURL: result.url, width: result.width, height: result.height,
                                pixelsPerPoint: scale, cursor: cursor, clicks: clicks, keys: keys)
    }

    // MARK: Editor / live preview

    /// Lets you pick an earlier recording folder (or its events.json) and open it in the editor.
    func openRecording() {
        let panel = NSOpenPanel()
        panel.message = "Choose a recording folder or its events.json"
        panel.canChooseDirectories = true
        panel.allowedContentTypes = [.json]
        panel.directoryURL = Self.recordingsRoot
        guard panel.runModal() == .OK, let url = panel.url else { return }

        let eventsURL = url.hasDirectoryPath ? url.appendingPathComponent("events.json") : url
        let folder = eventsURL.deletingLastPathComponent()
        Task {
            do {
                var session = try JSONDecoder().decode(RecordingSession.self, from: Data(contentsOf: eventsURL))
                // Recordings may have been moved; prefer the video next to the events file.
                let localVideo = folder.appendingPathComponent("raw.mov")
                if FileManager.default.fileExists(atPath: localVideo.path) { session.videoURL = localVideo }
                try await openEditor(session: session, folder: folder)
            } catch {
                phase = .failed("Could not open recording: \(error.localizedDescription)")
            }
        }
    }

    private func openEditor(session: RecordingSession, folder: URL) async throws {
        let video = AVURLAsset(url: session.videoURL)
        duration = try await video.load(.duration).seconds

        // Preview plays the recording with separate click and keyboard tracks, so each can be
        // muted instantly from the sidebar.
        for old in previewAudioURLs { try? FileManager.default.removeItem(at: old) }
        let clicksURL = InputSounds.temporaryURL("clicks"), keysURL = InputSounds.temporaryURL("keys")
        try InputSounds.writeTrack(InputSounds.clickHits(session), duration: duration, to: clicksURL)
        try InputSounds.writeTrack(InputSounds.keyHits(session), duration: duration, to: keysURL)
        previewAudioURLs = [clicksURL, keysURL]
        let (asset, audioIDs) = try await InputSounds.combine(video: video, audio: previewAudioURLs)
        previewAudioTrackIDs = audioIDs

        self.session = session
        self.folder = folder
        previewAsset = asset
        exportedURL = nil
        player.replaceCurrentItem(with: AVPlayerItem(asset: asset))
        updatePreviewAudio()
        await updatePreview()
        phase = .editing
        player.play()
    }

    /// Mutes/unmutes the preview's click and keyboard tracks to match the settings.
    private func updatePreviewAudio() {
        guard let item = player.currentItem, previewAudioTrackIDs.count == 2 else { return }
        let volumes: [Float] = [settings.clickSounds ? 1 : 0, settings.keyboardSounds ? 1 : 0]
        let mix = AVMutableAudioMix()
        mix.inputParameters = zip(previewAudioTrackIDs, volumes).map { id, volume in
            let params = AVMutableAudioMixInputParameters()
            params.trackID = id
            params.setVolume(volume, at: .zero)
            return params
        }
        item.audioMix = mix
    }

    private func schedulePreviewUpdate() {
        guard session != nil else { return }
        previewTask?.cancel()
        previewTask = Task {
            try? await Task.sleep(nanoseconds: 60_000_000)  // coalesce slider drags
            guard !Task.isCancelled else { return }
            await updatePreview()
        }
    }

    /// Applies the current settings to the player as a live Core Image video composition.
    private func updatePreview() async {
        guard let session, let asset = previewAsset, let item = player.currentItem else { return }
        let composer = FrameComposer(session: session, settings: settings, cursor: cursor, duration: duration,
                                     outputWidth: Self.previewWidth, maxBlurSamples: Self.previewBlurSamples)
        do {
            let composition = try await AVMutableVideoComposition.videoComposition(with: asset) { request in
                let frame = composer.frame(source: request.sourceImage, time: request.compositionTime.seconds)
                request.finish(with: frame, context: nil)
            }
            composition.renderSize = composer.canvas.size
            composition.frameDuration = CMTime(value: 1, timescale: CMTimeScale(settings.fps))
            guard !Task.isCancelled, player.currentItem === item else { return }
            item.videoComposition = composition
            segments = composer.path.segments
            previewSize = composer.canvas.size
            // Redraw the current frame when paused.
            if player.rate == 0 {
                await player.seek(to: player.currentTime(), toleranceBefore: .zero, toleranceAfter: .zero)
            }
        } catch {
            NSLog("ScreenPlus: preview update failed: \(error)")
        }
    }

    func seek(to seconds: Double) {
        let time = CMTime(seconds: max(0, min(seconds, duration)), preferredTimescale: 600)
        player.seek(to: time, toleranceBefore: .zero, toleranceAfter: .zero)
    }

    // MARK: Export

    func export() {
        guard let session, exportProgress == nil else { return }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.mpeg4Movie]
        panel.nameFieldStringValue = "ScreenPlus \(Self.timestamp()).mp4"
        panel.directoryURL = folder
        guard panel.runModal() == .OK, let url = panel.url else { return }

        player.pause()
        exportProgress = 0
        exportedURL = nil
        let renderer = Renderer(session: session, settings: settings, cursor: cursor)

        exportTask = Task {
            do {
                try await Task.detached(priority: .userInitiated) {
                    try await renderer.render(to: url) { progress in
                        Task { @MainActor [weak self] in
                            if self?.exportProgress != nil { self?.exportProgress = progress }
                        }
                    }
                }.value
                exportProgress = nil
                exportedURL = url
            } catch {
                exportProgress = nil
                if !(error is CancellationError) {
                    phase = .failed(error.localizedDescription)
                }
            }
        }
    }

    func cancelExport() {
        exportTask?.cancel()
        exportProgress = nil
    }

    // MARK: Misc

    func closeEditor() {
        player.pause()
        player.replaceCurrentItem(with: nil)
        session = nil
        phase = .idle
    }

    func revealInFinder(_ url: URL) {
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }

    /// Lets the user pick an image file as the background.
    func chooseBackgroundImage() {
        let panel = NSOpenPanel()
        panel.message = "Choose a background image"
        panel.allowedContentTypes = [.image]
        panel.directoryURL = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask).first
        guard panel.runModal() == .OK, let url = panel.url else { return }
        guard NSImage(contentsOf: url) != nil else {
            phase = .failed("Couldn't open \(url.lastPathComponent) as an image.")
            return
        }
        settings.background = .image(url)
    }

    var hasKeyboardAccess: Bool { AXIsProcessTrusted() }

    func requestKeyboardAccess() {
        InputTracker.requestKeyboardAccess()
        openAccessibilitySettings()
    }

    func openAccessibilitySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            NSWorkspace.shared.open(url)
        }
    }

    func openSystemSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(url)
        }
    }

    static var recordingsRoot: URL {
        FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("ScreenPlus", isDirectory: true)
    }

    private static func timestamp() -> String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd 'at' HH.mm.ss"
        return f.string(from: Date())
    }

    static func cursorArt() -> CursorArt {
        let cursor = NSCursor.arrow
        var size = cursor.image.size
        var hotSpot = cursor.hotSpot
        if size.width < 1 || size.height < 1 {
            size = CGSize(width: 17, height: 23)
            hotSpot = CGPoint(x: 4, y: 4)
        }
        // Rasterize at high resolution so the cursor stays sharp when zoomed in.
        let scale = 8.0
        let width = Int(size.width * scale), height = Int(size.height * scale)
        let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.scaleBy(x: scale, y: scale)
        let rect = CGRect(origin: .zero, size: size)
        if cursor.image.isValid, cursor.image.size.width > 0 {
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: false)
            cursor.image.draw(in: rect)
            NSGraphicsContext.restoreGraphicsState()
        } else {
            drawFallbackArrow(in: ctx, size: size)
        }
        return CursorArt(image: ctx.makeImage()!, pointSize: size, hotSpot: hotSpot)
    }

    /// A classic black arrow with a white outline, in a bottom-left-origin context.
    private static func drawFallbackArrow(in ctx: CGContext, size: CGSize) {
        let h = size.height
        let points: [CGPoint] = [(4, 4), (4, 20), (8, 16), (11, 22), (13.5, 21), (10.5, 15), (16, 15)]
            .map { CGPoint(x: $0.0, y: h - $0.1) }
        ctx.addLines(between: points)
        ctx.closePath()
        ctx.setLineJoin(.round)
        ctx.setLineWidth(1.5)
        ctx.setStrokeColor(.white)
        ctx.setFillColor(.black)
        ctx.drawPath(using: .fillStroke)
    }
}
