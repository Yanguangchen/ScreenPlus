import AVKit
import SwiftUI

/// AppKit's player view. (SwiftUI's `VideoPlayer` crashes when loading `_AVKit_SwiftUI` in this app.)
struct PlayerView: NSViewRepresentable {
    let player: AVPlayer

    func makeNSView(context: Context) -> AVPlayerView {
        let view = AVPlayerView()
        view.player = player
        view.controlsStyle = .inline
        view.showsFullScreenToggleButton = true
        view.videoGravity = .resizeAspect
        return view
    }

    func updateNSView(_ view: AVPlayerView, context: Context) {
        if view.player !== player { view.player = player }
    }
}

/// Editor: live preview on the left, settings and export on the right.
struct PreviewView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        HStack(spacing: 0) {
            VStack(spacing: 14) {
                PlayerView(player: model.player)
                    .aspectRatio(model.previewSize.width / model.previewSize.height, contentMode: .fit)
                    .clipShape(RoundedRectangle(cornerRadius: 10))
                    .shadow(color: .black.opacity(0.25), radius: 12, y: 4)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)

                ZoomTimeline(segments: model.segments, duration: model.duration,
                             zoomLevel: model.settings.zoomLevel, player: model.player) { model.seek(to: $0) }
            }
            .padding(20)
            .background(Color(nsColor: .underPageBackgroundColor))

            Divider()

            Sidebar()
                .frame(width: 290)
        }
    }
}

/// Shows where the auto-zooms happen; click or drag to scrub.
struct ZoomTimeline: View {
    let segments: [CameraPath.Segment]
    let duration: Double
    let zoomLevel: Double
    let player: AVPlayer
    let seek: (Double) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Label("Zooms", systemImage: "plus.magnifyingglass")
                    .font(.caption.weight(.medium))
                    .foregroundStyle(.secondary)
                Spacer()
                Text("\(segments.count) auto-zoom\(segments.count == 1 ? "" : "s")")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }

            GeometryReader { geo in
                let width = geo.size.width
                let scale = duration > 0 ? width / duration : 0
                TimelineView(.periodic(from: .now, by: 1.0 / 30.0)) { _ in
                    let now = player.currentTime().seconds.isFinite ? player.currentTime().seconds : 0
                    ZStack(alignment: .leading) {
                        RoundedRectangle(cornerRadius: 6)
                            .fill(Color.primary.opacity(0.07))

                        ForEach(segments.indices, id: \.self) { i in
                            let s = segments[i]
                            let start = max(0, s.start), end = min(duration, s.end)
                            RoundedRectangle(cornerRadius: 5)
                                .fill(Color.accentColor.gradient)
                                .overlay(alignment: .leading) {
                                    Text(String(format: "%.1f×", zoomLevel))
                                        .font(.caption2.weight(.semibold))
                                        .foregroundStyle(.white)
                                        .padding(.leading, 6)
                                        .lineLimit(1)
                                }
                                .clipped()
                                .frame(width: max(3, (end - start) * scale))
                                .offset(x: start * scale)
                                .padding(.vertical, 4)
                        }

                        Capsule()
                            .fill(Color.red)
                            .frame(width: 3)
                            .offset(x: min(max(0, now * scale - 1.5), width - 3))
                    }
                }
                .contentShape(Rectangle())
                .gesture(DragGesture(minimumDistance: 0).onChanged { value in
                    guard width > 0 else { return }
                    seek(value.location.x / width * duration)
                })
            }
            .frame(height: 34)
        }
    }
}

struct Sidebar: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    section("Zoom") {
                        SettingSlider(title: "Zoom level", value: $model.settings.zoomLevel, range: 1.25...4,
                                      format: "%.1f×")
                        SettingSlider(title: "Zoom out after", value: $model.settings.idleTimeout, range: 0.5...5,
                                      format: "%.1fs")
                        Toggle("Zoom on clicks only", isOn: $model.settings.clicksOnly)
                    }
                    section("Cursor") {
                        SettingSlider(title: "Size", value: $model.settings.cursorScale, range: 1...3, format: "%.1f×")
                        SettingSlider(title: "Smoothing", value: $model.settings.cursorSmoothing, range: 0...1,
                                      format: "%.0f%%", displayScale: 100)
                    }
                    section("Background") {
                        BackgroundPicker()
                    }
                    section("Look") {
                        SettingSlider(title: "Padding", value: $model.settings.padding, range: 0...0.15,
                                      format: "%.0f%%", displayScale: 100)
                        Toggle("Motion blur", isOn: $model.settings.motionBlur)
                    }
                    section("Sound") {
                        Toggle("Mouse click sounds", isOn: $model.settings.clickSounds)
                        Toggle("Keyboard sounds", isOn: $model.settings.keyboardSounds)
                        Text("\(model.session?.clicks.count ?? 0) clicks · \(model.session?.keys?.count ?? 0) key presses in this recording")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        if !model.hasKeyboardAccess {
                            VStack(alignment: .leading, spacing: 6) {
                                Text("To record typing, allow ScreenPlus in Accessibility settings. Only the timing of key presses is saved, never what you type.")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                    .fixedSize(horizontal: false, vertical: true)
                                Button("Open Accessibility Settings") { model.openAccessibilitySettings() }
                                    .controlSize(.small)
                            }
                        }
                    }
                }
                .padding(20)
            }

            Divider()
            exportArea
                .padding(20)
        }
        .disabled(model.exportProgress != nil)
    }

    @ViewBuilder
    private var exportArea: some View {
        VStack(spacing: 10) {
            if let progress = model.exportProgress {
                ProgressView(value: progress) {
                    Text("Exporting… \(Int(progress * 100))%").font(.callout)
                }
                Button("Cancel", role: .cancel) { model.cancelExport() }
                    .disabled(false)
            } else {
                Button {
                    model.export()
                } label: {
                    Label("Export MP4…", systemImage: "square.and.arrow.up")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .keyboardShortcut("e", modifiers: .command)

                if let url = model.exportedURL {
                    Button {
                        model.revealInFinder(url)
                    } label: {
                        Label("Exported — Show in Finder", systemImage: "checkmark.circle.fill")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.bordered)
                    .tint(.green)
                }

                HStack {
                    Button("New Recording") { model.showRecorder() }
                    Spacer()
                    Button("Open…") { model.openRecording() }
                }
                .controlSize(.small)
                .padding(.top, 4)
            }
        }
    }

    private func section<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(title.uppercased())
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
            content()
        }
    }
}

/// Gradient presets plus a custom image swatch.
struct BackgroundPicker: View {
    @EnvironmentObject var model: AppModel
    @State private var thumbnail: NSImage?
    private let columns = Array(repeating: GridItem(.flexible(), spacing: 8), count: 4)

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            LazyVGrid(columns: columns, spacing: 8) {
                ForEach(GradientPreset.all.indices, id: \.self) { i in
                    let preset = GradientPreset.all[i]
                    swatch(selected: model.settings.background == .gradient(i), help: preset.name) {
                        LinearGradient(colors: [color(preset.from), color(preset.to)],
                                       startPoint: .topLeading, endPoint: .bottomTrailing)
                    } action: {
                        model.settings.background = .gradient(i)
                    }
                }

                swatch(selected: isImage, help: isImage ? "Change image…" : "Use your own image…") {
                    if let thumbnail, isImage {
                        Image(nsImage: thumbnail).resizable().scaledToFill()
                    } else {
                        ZStack {
                            Color.primary.opacity(0.06)
                            Image(systemName: "photo.badge.plus")
                                .font(.system(size: 15))
                                .foregroundStyle(.secondary)
                        }
                    }
                } action: {
                    model.chooseBackgroundImage()
                }
            }

            if case .image(let url) = model.settings.background {
                HStack {
                    Text(url.lastPathComponent)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Spacer()
                    Button("Remove") { model.settings.background = .gradient(0) }
                        .controlSize(.small)
                }
                SettingSlider(title: "Blur", value: $model.settings.backgroundBlur, range: 0...1,
                              format: "%.0f%%", displayScale: 100)
            }
        }
        .onAppear(perform: loadThumbnail)
        .onChange(of: model.settings.background) { loadThumbnail() }
    }

    private var isImage: Bool {
        if case .image = model.settings.background { return true }
        return false
    }

    private func loadThumbnail() {
        if case .image(let url) = model.settings.background {
            thumbnail = NSImage(contentsOf: url)
        } else {
            thumbnail = nil
        }
    }

    private func color(_ rgb: GradientPreset.RGB) -> Color {
        Color(red: rgb.r, green: rgb.g, blue: rgb.b)
    }

    private func swatch<Content: View>(selected: Bool, help: String, @ViewBuilder content: () -> Content,
                                       action: @escaping () -> Void) -> some View {
        Button(action: action) {
            content()
                .frame(height: 38)
                .frame(maxWidth: .infinity)
                .clipShape(RoundedRectangle(cornerRadius: 7))
                .overlay(
                    RoundedRectangle(cornerRadius: 7)
                        .strokeBorder(selected ? Color.accentColor : Color.primary.opacity(0.12),
                                      lineWidth: selected ? 2.5 : 1)
                )
                .contentShape(RoundedRectangle(cornerRadius: 7))
        }
        .buttonStyle(.plain)
        .help(help)
    }
}

struct SettingSlider: View {
    let title: String
    @Binding var value: Double
    let range: ClosedRange<Double>
    let format: String
    var displayScale: Double = 1

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(title)
                Spacer()
                Text(String(format: format, value * displayScale))
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
            }
            .font(.callout)
            Slider(value: $value, in: range)
                .controlSize(.small)
        }
    }
}
