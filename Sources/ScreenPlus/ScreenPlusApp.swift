import AppKit
import AVFoundation
import SwiftUI

@main
struct Main {
    static func main() {
        // Developer mode: re-render a saved recording without the UI.
        //   ScreenPlus --render <events.json> <output.mp4>
        let args = CommandLine.arguments
        if args.count == 4, args[1] == "--render" {
            let cursor = MainActor.assumeIsolated { AppModel.cursorArt() }
            CLI.render(events: URL(fileURLWithPath: args[2]), output: URL(fileURLWithPath: args[3]), cursor: cursor)
        }
        //   ScreenPlus --preview-frame <events.json> <seconds> <output.png>
        if args.count == 5, args[1] == "--preview-frame", let t = Double(args[3]) {
            let cursor = MainActor.assumeIsolated { AppModel.cursorArt() }
            CLI.previewFrame(events: URL(fileURLWithPath: args[2]), time: t, output: URL(fileURLWithPath: args[4]),
                             cursor: cursor)
        }
        ScreenPlusApp.main()
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    /// The main window is hidden while the floating toolbar is up; that must not quit the app.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
}

struct ScreenPlusApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var model = AppModel()

    var body: some Scene {
        Window("ScreenPlus", id: "main") {
            ContentView()
                .environmentObject(model)
        }
        .defaultSize(width: 1180, height: 760)
        .windowResizability(.contentMinSize)
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("New Recording") { model.showRecorder() }
                    .keyboardShortcut("n")
                    .disabled(model.isBusy)
                Button(model.isRecording ? "Stop Recording" : "Start Recording") {
                    model.isRecording ? model.stopRecording() : model.startRecording()
                }
                .keyboardShortcut("r", modifiers: [.command, .shift])
                Button("Open Recording…") { model.openRecording() }
                    .keyboardShortcut("o")
                    .disabled(model.isBusy)
            }
        }

        // Lets you stop recording from the menu bar without switching windows.
        MenuBarExtra(isInserted: .constant(model.isRecording)) {
            Button("Stop Recording") { model.stopRecording() }
        } label: {
            Image(systemName: "record.circle.fill")
        }
    }
}

enum CLI {
    /// Grabs one frame through the same video composition the live preview uses.
    static func previewFrame(events: URL, time: Double, output: URL, cursor: CursorArt) -> Never {
        let semaphore = DispatchSemaphore(value: 0)
        var exitCode: Int32 = 0
        Task.detached {
            do {
                let session = try JSONDecoder().decode(RecordingSession.self, from: Data(contentsOf: events))
                // Same asset setup as the app's live preview: video + click track.
                let video = AVURLAsset(url: session.videoURL)
                let duration = try await video.load(.duration).seconds
                let (asset, _) = try await InputSounds.combine(video: video, audio: [])
                var settings = RenderSettings()
                if let path = ProcessInfo.processInfo.environment["SCREENPLUS_BACKGROUND"] {
                    settings.background = .image(URL(fileURLWithPath: path))
                    settings.backgroundBlur = Double(ProcessInfo.processInfo.environment["SCREENPLUS_BLUR"] ?? "") ?? 0
                }
                let composer = FrameComposer(session: session, settings: settings, cursor: cursor,
                                             duration: duration, outputWidth: 1280, maxBlurSamples: 6)
                let composition = try await AVMutableVideoComposition.videoComposition(with: asset) { request in
                    request.finish(with: composer.frame(source: request.sourceImage,
                                                        time: request.compositionTime.seconds), context: nil)
                }
                composition.renderSize = composer.canvas.size
                composition.frameDuration = CMTime(value: 1, timescale: 60)
                let generator = AVAssetImageGenerator(asset: asset)
                generator.videoComposition = composition
                generator.requestedTimeToleranceBefore = .zero
                generator.requestedTimeToleranceAfter = .zero
                let (image, _) = try await generator.image(at: CMTime(seconds: time, preferredTimescale: 600))
                let rep = NSBitmapImageRep(cgImage: image)
                try rep.representation(using: .png, properties: [:])!.write(to: output)
                print("zoom segments: \(composer.path.segments.map { String(format: "%.1f-%.1fs", $0.start, $0.end) })")
            } catch {
                print("Error: \(error)")
                exitCode = 1
            }
            semaphore.signal()
        }
        semaphore.wait()
        exit(exitCode)
    }

    static func render(events: URL, output: URL, cursor: CursorArt) -> Never {
        let semaphore = DispatchSemaphore(value: 0)
        var exitCode: Int32 = 0
        Task.detached {
            do {
                let session = try JSONDecoder().decode(RecordingSession.self, from: Data(contentsOf: events))
                let start = Date()
                try await Renderer(session: session, settings: RenderSettings(), cursor: cursor)
                    .render(to: output) { _ in }
                print("Rendered \(output.path) in \(String(format: "%.1f", Date().timeIntervalSince(start)))s")
            } catch {
                print("Error: \(error.localizedDescription)")
                exitCode = 1
            }
            semaphore.signal()
        }
        semaphore.wait()
        exit(exitCode)
    }
}
