using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using ScreenPlus.Rendering;
using SkiaSharp;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;
using static TerraFX.Interop.Windows.STD;
using static TerraFX.Interop.Windows.FILE;

namespace ScreenPlus;

/// <summary>
/// Developer commands, like the Mac app's:
/// <code>
/// ScreenPlus --render &lt;events.json&gt; &lt;output.mp4&gt;
/// ScreenPlus --preview-frame &lt;events.json&gt; &lt;seconds&gt; &lt;output.png&gt;
/// </code>
/// </summary>
internal static class Cli
{
    public static bool Handles(string[] args) => args.Length > 0 && args[0] is "--render" or "--preview-frame" or "--help";

    public static int Run(string[] args)
    {
        // ScreenPlus is a windowed app; borrow the terminal it was started from for output,
        // unless output is already going to a file or pipe.
        if (!IsRedirected()) AttachConsole(ATTACH_PARENT_PROCESS);
        try
        {
            switch (args)
            {
                case ["--render", var events, var output]:
                    return Render(events, output);
                case ["--preview-frame", var events, var time, var output]
                    when double.TryParse(time, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds):
                    return PreviewFrame(events, seconds, output);
                default:
                    Console.WriteLine("Usage:");
                    Console.WriteLine("  ScreenPlus --render <events.json> <output.mp4>");
                    Console.WriteLine("  ScreenPlus --preview-frame <events.json> <seconds> <output.png>");
                    return args is ["--help"] ? 0 : 2;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    private static unsafe bool IsRedirected()
    {
        var handle = GetStdHandle(STD_OUTPUT_HANDLE);
        return handle != HANDLE.NULL && handle != HANDLE.INVALID_VALUE && GetFileType(handle) is FILE_TYPE_DISK or FILE_TYPE_PIPE;
    }

    private static int Render(string events, string output)
    {
        var session = RecordingSession.Load(events);
        using var cursor = CursorArt.CreateDefault();
        var watch = Stopwatch.StartNew();
        var lastReport = -1;
        new Renderer(session, new RenderSettings(), cursor).Render(Path.GetFullPath(output), progress =>
        {
            var percent = (int)(progress * 100);
            if (percent / 10 == lastReport / 10) return;
            lastReport = percent;
            Console.WriteLine($"{percent}%");
        }, CancellationToken.None);
        Console.WriteLine($"Rendered {output} in {watch.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    /// <summary>Grabs one frame the way the live preview shows it.</summary>
    private static unsafe int PreviewFrame(string events, double time, string output)
    {
        var session = RecordingSession.Load(events);
        using var cursor = CursorArt.CreateDefault();
        using var reader = new Media.Mp4Reader(session.VideoPath);
        using var source = new SourceFrames(reader);
        var duration = reader.Duration - source.BaseTime;
        var settings = new RenderSettings();
        if (Environment.GetEnvironmentVariable("SCREENPLUS_BACKGROUND") is { } background)
        {
            settings = settings with
            {
                Background = new ImageBackground(background),
                BackgroundBlur = double.TryParse(Environment.GetEnvironmentVariable("SCREENPLUS_BLUR"), NumberStyles.Float,
                                                 CultureInfo.InvariantCulture, out var blur) ? blur : 0,
            };
        }
        using var composer = new FrameComposer(session, settings, cursor, duration, outputWidth: 1280, maxBlurSamples: 6);
        var frame = source.At(time) ?? throw new InvalidDataException("The recording has no frames.");

        var stride = composer.Width * 4;
        var pixels = NativeMemory.Alloc((nuint)(stride * composer.Height));
        try
        {
            using var scratch = new ComposerScratch();
            composer.Render(frame, time, (nint)pixels, stride, scratch);
            var info = new SKImageInfo(composer.Width, composer.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var image = SKImage.FromPixelCopy(info, (nint)pixels, stride)!;
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(output, png.ToArray());
        }
        finally
        {
            NativeMemory.Free(pixels);
        }
        var segments = string.Join(", ", composer.Path.Segments.Select(s => $"{s.Start:F1}-{s.End:F1}s"));
        Console.WriteLine($"zoom segments: [{segments}]");
        return 0;
    }
}
