using System.IO;
using System.Runtime.InteropServices;
using ScreenPlus.Media;
using SkiaSharp;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ScreenPlus.WindowsTests;

/// <summary>Builds a fake recording (raw.mp4 + events.json) the way the recorder would.</summary>
internal static unsafe class SyntheticRecording
{
    public const int Width = 1280, Height = 720;
    public const double Duration = 4;

    /// <summary>Where tests drop files for a human to look at (uploaded by CI).</summary>
    public static string ArtifactDirectory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("SCREENPLUS_TEST_OUTPUT")
                ?? Path.Combine(Path.GetTempPath(), "screenplus-windows-tests");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string ThumbnailDirectory
    {
        get
        {
            var dir = Path.Combine(ArtifactDirectory, "thumbs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Frames arrive at a variable rate like real captures: 30 fps for two seconds, nothing for a second
    /// (a still screen), then 60 fps.
    /// </summary>
    public static IEnumerable<double> FrameTimes()
    {
        for (var t = 0.0; t < 2; t += 1.0 / 30) yield return t;
        for (var t = 3.0; t < Duration; t += 1.0 / 60) yield return t;
    }

    public static string Create(bool preferHardware = true)
    {
        var folder = Directory.CreateTempSubdirectory("screenplus-test-").FullName;
        var video = Path.Combine(folder, "raw.mp4");
        using (var writer = Mp4Writer.Create(video, new Mp4Writer.Options(Width, Height, 60, 8_000_000, 60, Audio: false), preferHardware))
        {
            var frame = new VideoFrame(Width, Height);
            var nv12 = (byte*)NativeMemory.Alloc((nuint)writer.FrameBytes);
            try
            {
                long last = 0;
                foreach (var t in FrameTimes())
                {
                    Draw(frame, t);
                    Yuv.BgraToNv12(frame.Data, frame.Stride, Width, Height, nv12, Width, nv12 + Width * Height, Width);
                    last = (long)(t * 10_000_000);
                    writer.WriteVideo(nv12, last, 10_000_000 / 60);
                }
                // Like the recorder: hold the last frame until the end.
                writer.WriteVideo(nv12, (long)(Duration * 10_000_000), 10_000_000 / 60);
                writer.Finish();
            }
            finally
            {
                NativeMemory.Free(nv12);
                frame.Release();
            }
        }

        var session = new RecordingSession { VideoPath = video, Width = Width, Height = Height, PixelsPerPoint = 1.25, Keys = [] };
        for (var t = 0.0; t <= Duration; t += 1.0 / 120)
        {
            // Rest, move to the top right, click there, then head down to the toolbar at the end.
            var u = Math.Clamp((t - 0.5) / 0.8, 0, 1);
            var x = Width * (0.4 + 0.35 * u);
            var y = Height * (0.6 - 0.35 * u) + (t > Duration - 1 ? (t - (Duration - 1)) * Height * 0.3 : 0);
            session.Cursor.Add(new CursorSample(t, x, y));
        }
        session.Clicks.Add(new ClickEvent(1.5, Width * 0.75, Height * 0.25));
        session.Keys.Add(new KeyEvent(2.0, KeyKind.Regular));
        session.Keys.Add(new KeyEvent(2.2, KeyKind.Space));
        session.Keys.Add(new KeyEvent(2.4, KeyKind.Enter));
        session.Save(Path.Combine(folder, "events.json"));
        return folder;
    }

    /// <summary>A fake desktop with a window whose content scrolls, so frames differ.</summary>
    public static void Draw(VideoFrame frame, double t)
    {
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info, (nint)frame.Data, frame.Stride)!;
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(32, 72, 120));
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(246, 246, 246) };
        var window = SKRect.Create(frame.Width * 0.1f, frame.Height * 0.1f, frame.Width * 0.8f, frame.Height * 0.75f);
        canvas.DrawRoundRect(window, 10, 10, paint);
        paint.Color = new SKColor(30, 30, 30);
        var offset = (float)(t * 40 % 30);
        for (var i = 0; i < 16; i++)
        {
            var y = window.Top + 40 + i * 30 - offset;
            if (y < window.Top + 20 || y > window.Bottom - 20) continue;
            canvas.DrawRect(SKRect.Create(window.Left + 30, y, window.Width * (0.3f + (i * 37 % 10) / 16f), 8), paint);
        }
        paint.Color = SKColors.Red;
        canvas.DrawRect(SKRect.Create(20, 20, 80, 80), paint);
        paint.Color = SKColors.Lime;
        canvas.DrawRect(SKRect.Create(frame.Width - 100, frame.Height - 100, 80, 80), paint);
    }

    public static void SavePng(nint pixels, int width, int height, int stride, string name)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var image = SKImage.FromPixelCopy(info, pixels, stride)!;
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(ArtifactDirectory, name), png.ToArray());

        // A small JPEG too, for a quick look.
        var scale = Math.Min(1.0, 480.0 / width);
        var small = new SKImageInfo((int)(width * scale), (int)(height * scale), SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(small)!;
        surface.Canvas.DrawImage(image, SKRect.Create(small.Width, small.Height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var jpeg = surface.Snapshot().Encode(SKEncodedImageFormat.Jpeg, 55);
        File.WriteAllBytes(Path.Combine(ThumbnailDirectory, Path.ChangeExtension(name, ".jpg")), jpeg.ToArray());
    }
}
