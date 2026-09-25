using SkiaSharp;

namespace ScreenPlus.Tests;

internal static class TestData
{
    /// <summary>Where tests drop images for a human to look at.</summary>
    public static string OutputDirectory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("SCREENPLUS_TEST_OUTPUT")
                ?? Path.Combine(Path.GetTempPath(), "screenplus-tests");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static RecordingSession Session(int width = 2560, int height = 1440, double pixelsPerPoint = 2,
                                           double duration = 10)
    {
        var session = new RecordingSession
        {
            Width = width,
            Height = height,
            PixelsPerPoint = pixelsPerPoint,
            VideoPath = Path.Combine(Path.GetTempPath(), "raw.mp4"),
        };
        // The mouse rests, moves to a button, clicks, types, and heads for the Stop button at the end.
        for (var t = 0.0; t <= duration; t += 1.0 / 120)
        {
            double x, y;
            if (t < 2) (x, y) = (width * 0.5, height * 0.5);
            else if (t < 3) (x, y) = (width * (0.5 + 0.2 * (t - 2)), height * (0.5 - 0.2 * (t - 2)));
            else if (t < duration - 1) (x, y) = (width * 0.7, height * 0.3);
            else (x, y) = (width * 0.7, height * (0.3 + 0.6 * (t - (duration - 1))));
            session.Cursor.Add(new CursorSample(t, x, y));
        }
        session.Clicks.Add(new ClickEvent(3.2, width * 0.7, height * 0.3));
        session.Keys = [new KeyEvent(4.0, KeyKind.Regular), new KeyEvent(4.1, KeyKind.Space), new KeyEvent(4.3, KeyKind.Enter)];
        return session;
    }

    /// <summary>A fake desktop: wallpaper, a window with "text" lines, and a coloured block per quadrant.</summary>
    public static unsafe VideoFrame Frame(int width, int height, double time = 0)
    {
        var frame = new VideoFrame(width, height) { Time = time };
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info, (nint)frame.Data, frame.Stride)!;
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(40, 60, 90));
        using var paint = new SKPaint { IsAntialias = true };

        paint.Color = new SKColor(245, 245, 245);
        var window = SKRect.Create(width * 0.1f, height * 0.1f, width * 0.8f, height * 0.75f);
        canvas.DrawRoundRect(window, 12, 12, paint);
        paint.Color = new SKColor(225, 225, 230);
        canvas.DrawRect(SKRect.Create(window.Left, window.Top, window.Width, height * 0.05f), paint);

        paint.Color = new SKColor(30, 30, 30);
        for (var i = 0; i < 20; i++)
        {
            var y = window.Top + height * 0.08f + i * height * 0.03f;
            var len = window.Width * (0.3f + 0.6f * ((i * 37) % 10) / 10f);
            canvas.DrawRect(SKRect.Create(window.Left + width * 0.03f, y, len, height * 0.008f), paint);
        }

        SKColor[] colors = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow];
        for (var q = 0; q < 4; q++)
        {
            paint.Color = colors[q];
            var x = q % 2 == 0 ? width * 0.02f : width * 0.9f;
            var y = q < 2 ? height * 0.02f : height * 0.9f;
            canvas.DrawRect(SKRect.Create(x, y, width * 0.08f, height * 0.08f), paint);
        }
        return frame;
    }

    public static unsafe void SavePng(byte* bgra, int width, int height, int stride, string name)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var image = SKImage.FromPixelCopy(info, (nint)bgra, stride)!;
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(OutputDirectory, name), data.ToArray());
    }

    public static unsafe (byte B, byte G, byte R) Pixel(byte* bgra, int stride, int x, int y)
    {
        var p = bgra + (long)y * stride + x * 4;
        return (p[0], p[1], p[2]);
    }
}
