import SwiftUI

struct ContentView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        Group {
            switch model.phase {
            case .editing:
                PreviewView()
            default:
                home
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .padding(32)
            }
        }
        .frame(minWidth: 820, minHeight: 540)
        .background(WindowAccessor { model.registerMainWindow($0) })
    }

    @ViewBuilder
    private var home: some View {
        switch model.phase {
        case .idle, .editing:
            VStack(spacing: 16) {
                Image(systemName: "record.circle")
                    .font(.system(size: 56))
                    .foregroundStyle(.red)
                Text("Record your screen. ScreenPlus zooms in where you work and out when you stop.")
                    .multilineTextAlignment(.center)
                    .foregroundStyle(.secondary)
                Button("New Recording") { model.showRecorder() }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .keyboardShortcut("n")
                Button("Open Previous Recording…") { model.openRecording() }
                    .buttonStyle(.link)
                Text("Opens the floating recording toolbar. ScreenPlus windows never appear in recordings.")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }

        case .starting:
            ProgressView("Starting…")

        case .recording(let since):
            VStack(spacing: 16) {
                TimelineView(.periodic(from: since, by: 1)) { context in
                    let seconds = Int(context.date.timeIntervalSince(since))
                    Label(String(format: "%d:%02d", seconds / 60, seconds % 60), systemImage: "record.circle.fill")
                        .font(.system(size: 32, weight: .semibold, design: .rounded).monospacedDigit())
                        .foregroundStyle(.red)
                }
                Button("Stop Recording") { model.stopRecording() }
                    .buttonStyle(.borderedProminent)
                    .tint(.red)
                    .controlSize(.large)
                    .keyboardShortcut("r", modifiers: [.command, .shift])
            }

        case .processing:
            ProgressView("Preparing preview…")

        case .failed(let message):
            VStack(spacing: 12) {
                Image(systemName: "exclamationmark.triangle")
                    .font(.system(size: 40))
                    .foregroundStyle(.orange)
                Text(message)
                    .multilineTextAlignment(.center)
                HStack {
                    Button("Open System Settings") { model.openSystemSettings() }
                    Button("Back") { model.phase = model.session == nil ? .idle : .editing }
                        .buttonStyle(.borderedProminent)
                }
            }
        }
    }
}
