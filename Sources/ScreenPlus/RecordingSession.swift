import Foundation

/// A cursor position sample. `t` is seconds since the first video frame; `x`/`y` are in
/// source video pixels with a top-left origin.
struct CursorSample: Codable {
    var t: Double
    var x: Double
    var y: Double
}

struct ClickEvent: Codable {
    var t: Double
    var x: Double
    var y: Double
}

/// Rough kind of key, so space/return/delete can sound different. Which key was pressed is never stored.
enum KeyKind: String, Codable {
    case regular, space, enter, delete
}

struct KeyEvent: Codable {
    var t: Double
    var kind: KeyKind
}

/// Everything captured during one recording: the raw video (without cursor) plus the input log.
struct RecordingSession: Codable {
    var videoURL: URL
    var width: Int
    var height: Int
    /// Source pixels per screen point (2 on Retina displays).
    var pixelsPerPoint: Double
    var cursor: [CursorSample]
    var clicks: [ClickEvent]
    /// Key presses; nil for recordings made before keyboard sounds existed.
    var keys: [KeyEvent]?

    func save(to url: URL) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted]
        try encoder.encode(self).write(to: url)
    }
}

/// What sits behind the recorded screen.
enum BackgroundStyle: Equatable {
    case gradient(Int)  // index into GradientPreset.all
    case image(URL)
}

struct GradientPreset {
    typealias RGB = (r: Double, g: Double, b: Double)
    let name: String
    let from: RGB
    let to: RGB

    static let all: [GradientPreset] = [
        GradientPreset(name: "Sunset", from: (0.36, 0.28, 0.85), to: (0.93, 0.45, 0.62)),
        GradientPreset(name: "Ocean", from: (0.05, 0.35, 0.75), to: (0.20, 0.80, 0.85)),
        GradientPreset(name: "Mint", from: (0.10, 0.60, 0.50), to: (0.65, 0.90, 0.55)),
        GradientPreset(name: "Peach", from: (0.98, 0.55, 0.40), to: (0.99, 0.82, 0.55)),
        GradientPreset(name: "Midnight", from: (0.06, 0.07, 0.20), to: (0.30, 0.20, 0.50)),
        GradientPreset(name: "Graphite", from: (0.18, 0.19, 0.22), to: (0.45, 0.47, 0.52)),
    ]
}

struct RenderSettings {
    /// How far to zoom in during activity.
    var zoomLevel: Double = 2.0
    /// Seconds without activity before zooming back out.
    var idleTimeout: Double = 1.5
    /// Only clicks trigger a zoom (otherwise mouse movement does too).
    var clicksOnly: Bool = false
    var motionBlur: Bool = true
    /// Add an audible click at every recorded mouse click.
    var clickSounds: Bool = true
    /// Add a keyboard sound at every recorded key press.
    var keyboardSounds: Bool = true
    /// Cursor size relative to the real cursor.
    var cursorScale: Double = 1.6
    /// How strongly the cursor path is smoothed (higher = smoother, laggier).
    var cursorSmoothing: Double = 0.5
    /// Padding around the screen, as a fraction of output width.
    var padding: Double = 0.05
    var background: BackgroundStyle = .gradient(0)
    /// Blur for image backgrounds, 0...1.
    var backgroundBlur: Double = 0
    var outputWidth: Int = 1920
    var fps: Int = 60
}
