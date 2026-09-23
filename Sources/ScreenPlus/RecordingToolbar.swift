import AppKit
import SwiftUI

/// A borderless, always-on-top panel that hosts the floating recording toolbar.
/// It never activates the app, so the app you're recording stays focused.
final class ToolbarPanel: NSPanel {
    init<Content: View>(rootView: Content) {
        super.init(contentRect: NSRect(x: 0, y: 0, width: 420, height: 96),
                   styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isFloatingPanel = true
        level = .statusBar
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        backgroundColor = .clear
        isOpaque = false
        hasShadow = false  // SwiftUI draws a softer one
        isMovableByWindowBackground = true
        hidesOnDeactivate = false
        sharingType = .none  // never show up in screen captures

        let host = NSHostingView(rootView: rootView)
        host.frame = contentRect(forFrameRect: frame)
        host.autoresizingMask = [.width, .height]
        contentView = host
    }

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

@MainActor
final class ToolbarController {
    private var panel: ToolbarPanel?

    func show(model: AppModel) {
        let panel = self.panel ?? ToolbarPanel(rootView: RecordingToolbar().environmentObject(model))
        self.panel = panel

        // Bottom-center of the screen with the mouse, above the Dock.
        let mouse = NSEvent.mouseLocation
        if let screen = NSScreen.screens.first(where: { NSMouseInRect(mouse, $0.frame, false) }) ?? NSScreen.main {
            let area = screen.visibleFrame
            panel.setFrameOrigin(NSPoint(x: area.midX - panel.frame.width / 2, y: area.minY + 24))
        }
        panel.orderFrontRegardless()
    }

    func hide() {
        panel?.orderOut(nil)
    }
}

// MARK: - Toolbar UI

struct RecordingToolbar: View {
    @EnvironmentObject var model: AppModel
    @State private var countdown: Int?
    @State private var countdownTask: Task<Void, Never>?

    var body: some View {
        HStack(spacing: 6) {
            if model.isRecording {
                recordingControls
            } else if let countdown {
                countdownControls(countdown)
            } else {
                idleControls
            }
        }
        .padding(.horizontal, 8)
        .frame(height: 52)
        .toolbarGlass()
        .shadow(color: .black.opacity(0.25), radius: 18, y: 8)
        .animation(.spring(response: 0.35, dampingFraction: 0.8), value: model.isRecording)
        .animation(.spring(response: 0.35, dampingFraction: 0.8), value: countdown)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    // Idle: close · source · record · library
    private var idleControls: some View {
        Group {
            ToolbarIconButton(systemImage: "xmark", help: "Close toolbar") { model.hideRecorder() }

            divider

            HStack(spacing: 6) {
                Image(systemName: "display")
                Text("Screen")
            }
            .font(.system(size: 13, weight: .medium))
            .foregroundStyle(.primary.opacity(0.85))
            .padding(.horizontal, 10)
            .help("Records the screen the pointer is on")

            KeyboardAccessIndicator()

            Button(action: startCountdown) {
                HStack(spacing: 7) {
                    Circle().fill(.white).frame(width: 9, height: 9)
                    Text("Record").font(.system(size: 13, weight: .semibold))
                }
                .foregroundStyle(.white)
                .padding(.horizontal, 16)
                .frame(height: 36)
                .background(Capsule().fill(Color.red.gradient))
                .contentShape(Capsule())
            }
            .buttonStyle(PressableButtonStyle())

            divider

            ToolbarIconButton(systemImage: "rectangle.stack", help: "Open editor & recordings") {
                model.hideRecorder()
            }
        }
    }

    private func countdownControls(_ value: Int) -> some View {
        Group {
            Text("\(value)")
                .font(.system(size: 22, weight: .bold, design: .rounded).monospacedDigit())
                .contentTransition(.numericText(countsDown: true))
                .frame(width: 44)
            Text("Get ready…")
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(.secondary)
                .padding(.trailing, 6)
            ToolbarIconButton(systemImage: "xmark", help: "Cancel") {
                countdownTask?.cancel()
                countdown = nil
            }
        }
    }

    private var recordingControls: some View {
        Group {
            if case .recording(let since) = model.phase {
                TimelineView(.periodic(from: since, by: 0.5)) { context in
                    let elapsed = context.date.timeIntervalSince(since)
                    let seconds = Int(elapsed)
                    HStack(spacing: 8) {
                        Circle()
                            .fill(Color.red)
                            .frame(width: 10, height: 10)
                            .opacity(Int(elapsed * 2) % 2 == 0 ? 1 : 0.35)
                            .animation(.easeInOut(duration: 0.4), value: Int(elapsed * 2))
                        Text(String(format: "%d:%02d", seconds / 60, seconds % 60))
                            .font(.system(size: 15, weight: .semibold, design: .rounded).monospacedDigit())
                    }
                    .padding(.horizontal, 12)
                }
            }

            divider

            ToolbarIconButton(systemImage: "trash", help: "Discard recording") { model.discardRecording() }

            Button { model.stopRecording() } label: {
                HStack(spacing: 7) {
                    RoundedRectangle(cornerRadius: 2.5).fill(.white).frame(width: 10, height: 10)
                    Text("Stop").font(.system(size: 13, weight: .semibold))
                }
                .foregroundStyle(.white)
                .padding(.horizontal, 16)
                .frame(height: 36)
                .background(Capsule().fill(Color.red.gradient))
                .contentShape(Capsule())
            }
            .buttonStyle(PressableButtonStyle())
            .help("Stop recording (⇧⌘R)")
        }
    }

    private var divider: some View {
        Rectangle()
            .fill(.primary.opacity(0.15))
            .frame(width: 1, height: 22)
            .padding(.horizontal, 2)
    }

    private func startCountdown() {
        countdownTask?.cancel()
        countdownTask = Task {
            for n in stride(from: 3, through: 1, by: -1) {
                countdown = n
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                if Task.isCancelled { return }
            }
            countdown = nil
            model.startRecording()
        }
    }
}

/// Shows whether typing can be captured for keyboard sounds; orange means Accessibility access is missing.
struct KeyboardAccessIndicator: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        TimelineView(.periodic(from: .now, by: 2)) { _ in
            let granted = model.hasKeyboardAccess
            ToolbarIconButton(
                systemImage: "keyboard",
                help: granted
                    ? "Keyboard sounds on: typing is captured (timing only, never the keys)"
                    : "Keyboard sounds need Accessibility access. Click to allow, then relaunch ScreenPlus."
            ) {
                if !granted { model.requestKeyboardAccess() }
            }
            .overlay(alignment: .topTrailing) {
                if !granted {
                    Circle()
                        .fill(Color.orange)
                        .frame(width: 8, height: 8)
                        .offset(x: -6, y: 6)
                }
            }
            .opacity(granted ? 1 : 0.75)
        }
    }
}

struct ToolbarIconButton: View {
    let systemImage: String
    let help: String
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: systemImage)
                .font(.system(size: 13, weight: .semibold))
                .frame(width: 34, height: 34)
                .background(Circle().fill(.primary.opacity(hovering ? 0.12 : 0)))
                .contentShape(Circle())
        }
        .buttonStyle(PressableButtonStyle())
        .foregroundStyle(.primary.opacity(0.85))
        .onHover { hovering = $0 }
        .help(help)
    }
}

struct PressableButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? 0.94 : 1)
            .animation(.spring(response: 0.2, dampingFraction: 0.7), value: configuration.isPressed)
    }
}

extension View {
    /// Liquid Glass on macOS 26+, frosted material before that.
    @ViewBuilder
    func toolbarGlass() -> some View {
        if #available(macOS 26.0, *) {
            self.glassEffect(.regular, in: Capsule())
        } else {
            self
                .background(.ultraThinMaterial, in: Capsule())
                .overlay(Capsule().strokeBorder(.white.opacity(0.25), lineWidth: 0.5))
        }
    }
}

/// Hands back the NSWindow hosting a SwiftUI view.
struct WindowAccessor: NSViewRepresentable {
    let onWindow: (NSWindow) -> Void

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async {
            if let window = view.window { onWindow(window) }
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {}
}
