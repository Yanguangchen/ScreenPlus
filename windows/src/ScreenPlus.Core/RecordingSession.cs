using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenPlus;

/// <summary>
/// A cursor position sample. <c>T</c> is seconds since the first video frame; <c>X</c>/<c>Y</c> are in
/// source video pixels with a top-left origin.
/// </summary>
public record struct CursorSample
{
    [JsonPropertyName("t")] public double T { get; set; }
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }

    public CursorSample(double t, double x, double y) => (T, X, Y) = (t, x, y);
}

public record struct ClickEvent
{
    [JsonPropertyName("t")] public double T { get; set; }
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }

    public ClickEvent(double t, double x, double y) => (T, X, Y) = (t, x, y);
}

/// <summary>Rough kind of key, so space/return/delete can sound different. Which key was pressed is never stored.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KeyKind>))]
public enum KeyKind
{
    [JsonStringEnumMemberName("regular")] Regular,
    [JsonStringEnumMemberName("space")] Space,
    [JsonStringEnumMemberName("enter")] Enter,
    [JsonStringEnumMemberName("delete")] Delete,
}

public record struct KeyEvent
{
    [JsonPropertyName("t")] public double T { get; set; }
    [JsonPropertyName("kind")] public KeyKind Kind { get; set; }

    public KeyEvent(double t, KeyKind kind) => (T, Kind) = (t, kind);
}

/// <summary>
/// Everything captured during one recording: the raw video (without cursor) plus the input log.
/// Saved as <c>events.json</c>, in the same format as the macOS app.
/// </summary>
public sealed class RecordingSession
{
    /// <summary>The raw video as a file URL (the macOS app writes the same field).</summary>
    [JsonPropertyName("videoURL")] public string VideoUrl { get; set; } = "";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    /// <summary>Source pixels per screen point (1.5 at 150% display scaling).</summary>
    [JsonPropertyName("pixelsPerPoint")] public double PixelsPerPoint { get; set; } = 1;
    [JsonPropertyName("cursor")] public List<CursorSample> Cursor { get; set; } = [];
    [JsonPropertyName("clicks")] public List<ClickEvent> Clicks { get; set; } = [];
    /// <summary>Key presses; null for recordings made before keyboard sounds existed.</summary>
    [JsonPropertyName("keys")] public List<KeyEvent>? Keys { get; set; }

    /// <summary>The raw video as a local path.</summary>
    [JsonIgnore]
    public string VideoPath
    {
        get => Uri.TryCreate(VideoUrl, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : VideoUrl;
        set => VideoUrl = new Uri(Path.GetFullPath(value)).AbsoluteUri;
    }

    public static RecordingSession Load(string eventsPath)
    {
        var session = JsonSerializer.Deserialize(File.ReadAllBytes(eventsPath), SessionJson.Default.RecordingSession)
            ?? throw new InvalidDataException($"{Path.GetFileName(eventsPath)} is not a ScreenPlus recording.");
        if (session.Width <= 0 || session.Height <= 0)
            throw new InvalidDataException($"{Path.GetFileName(eventsPath)} has no video size.");

        // Recordings may have been moved; prefer the video next to the events file.
        var folder = Path.GetDirectoryName(Path.GetFullPath(eventsPath))!;
        foreach (var name in new[] { "raw.mp4", "raw.mov" })
        {
            var local = Path.Combine(folder, name);
            if (File.Exists(local))
            {
                session.VideoPath = local;
                break;
            }
        }
        return session;
    }

    public void Save(string path) =>
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, SessionJson.Default.RecordingSession));
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RecordingSession))]
internal sealed partial class SessionJson : JsonSerializerContext;

/// <summary>What sits behind the recorded screen.</summary>
public abstract record BackgroundStyle;

/// <summary>A gradient; the index is into <see cref="GradientPreset.All"/>.</summary>
public sealed record GradientBackground(int Index) : BackgroundStyle;

public sealed record ImageBackground(string Path) : BackgroundStyle;

public readonly record struct Rgb(double R, double G, double B);

public sealed record GradientPreset(string Name, Rgb From, Rgb To)
{
    public static readonly IReadOnlyList<GradientPreset> All =
    [
        new("Sunset", new(0.36, 0.28, 0.85), new(0.93, 0.45, 0.62)),
        new("Ocean", new(0.05, 0.35, 0.75), new(0.20, 0.80, 0.85)),
        new("Mint", new(0.10, 0.60, 0.50), new(0.65, 0.90, 0.55)),
        new("Peach", new(0.98, 0.55, 0.40), new(0.99, 0.82, 0.55)),
        new("Midnight", new(0.06, 0.07, 0.20), new(0.30, 0.20, 0.50)),
        new("Graphite", new(0.18, 0.19, 0.22), new(0.45, 0.47, 0.52)),
    ];

    public static GradientPreset At(int index) => All[Math.Clamp(index, 0, All.Count - 1)];
}

/// <summary>Editor settings. Immutable, so a snapshot can be handed to background renderers.</summary>
public sealed record RenderSettings
{
    /// <summary>How far to zoom in during activity.</summary>
    public double ZoomLevel { get; init; } = 2.0;
    /// <summary>Seconds without activity before zooming back out.</summary>
    public double IdleTimeout { get; init; } = 1.5;
    /// <summary>Only clicks trigger a zoom (otherwise mouse movement does too).</summary>
    public bool ClicksOnly { get; init; }
    public bool MotionBlur { get; init; } = true;
    /// <summary>Add an audible click at every recorded mouse click.</summary>
    public bool ClickSounds { get; init; } = true;
    /// <summary>Add a keyboard sound at every recorded key press.</summary>
    public bool KeyboardSounds { get; init; } = true;
    /// <summary>Cursor size relative to the real cursor.</summary>
    public double CursorScale { get; init; } = 1.6;
    /// <summary>How strongly the cursor path is smoothed (higher = smoother, laggier).</summary>
    public double CursorSmoothing { get; init; } = 0.5;
    /// <summary>Padding around the screen, as a fraction of output width.</summary>
    public double Padding { get; init; } = 0.05;
    public BackgroundStyle Background { get; init; } = new GradientBackground(0);
    /// <summary>Blur for image backgrounds, 0...1.</summary>
    public double BackgroundBlur { get; init; }
    public int OutputWidth { get; init; } = 1920;
    public int Fps { get; init; } = 60;

    /// <summary>
    /// Playback speed of the exported video: 2 plays twice as fast, 0.5 at half speed.
    /// Times in the recording (and the editor's timeline) stay as recorded; output time = recording time / speed.
    /// </summary>
    public double Speed
    {
        get;
        init => field = Math.Clamp(value, MinSpeed, MaxSpeed);
    } = 1;

    public const double MinSpeed = 0.1, MaxSpeed = 10;

    /// <summary>The speeds the editor offers: up to 10× slower or faster.</summary>
    public static readonly IReadOnlyList<double> SpeedChoices = [0.1, 0.125, 1.0 / 6, 0.25, 0.5, 1, 2, 4, 6, 8, 10];

    /// <summary>"10× slower", "Normal", "4× faster"…</summary>
    public static string DescribeSpeed(double speed) =>
        Math.Abs(speed - 1) < 1e-6 ? "Normal speed"
        : speed > 1 ? $"{speed:0.#}× faster"
        : $"{1 / speed:0.#}× slower";
}
