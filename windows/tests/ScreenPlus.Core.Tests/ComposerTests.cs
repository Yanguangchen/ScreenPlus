using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Xunit.Abstractions;

namespace ScreenPlus.Tests;

public unsafe class ComposerTests(ITestOutputHelper output) : IDisposable
{
    private readonly CursorArt _cursor = CursorArt.CreateDefault();
    private readonly RecordingSession _session = TestData.Session();

    public void Dispose() => _cursor.Dispose();

    [Fact]
    public void LaysOutTheScreenInsideThePadding()
    {
        using var composer = new FrameComposer(_session, new RenderSettings(), _cursor, 10, outputWidth: 1920);

        Assert.Equal((1920, 1164), (composer.Width, composer.Height));
        Assert.Equal(SKRectI.Create(96, 96, 1728, 972), composer.Inner);
    }

    [Fact]
    public void SmallScreensAreNotUpscaled()
    {
        var session = TestData.Session(1366, 768, 1);
        using var composer = new FrameComposer(session, new RenderSettings { Padding = 0 }, _cursor, 10, outputWidth: 1920);

        Assert.Equal((1366, 768), (composer.Width, composer.Height));
    }

    [Fact]
    public void RendersBackgroundScreenAndCursor()
    {
        using var composer = new FrameComposer(_session, new RenderSettings(), _cursor, 10, outputWidth: 1920);
        var source = TestData.Frame(_session.Width, _session.Height);
        var pixels = Allocate(composer, out var stride);
        using var scratch = new ComposerScratch();

        composer.Render(source, 0.5, (nint)pixels, stride, scratch);
        TestData.SavePng(pixels, composer.Width, composer.Height, stride, "frame-idle.png");

        // Top-left of the Sunset gradient, and the window in the middle of the screen.
        var corner = TestData.Pixel(pixels, stride, 1, 1);
        Assert.InRange(corner.R, 88, 96);
        Assert.InRange(corner.G, 67, 75);
        Assert.InRange(corner.B, 213, 221);
        var middle = TestData.Pixel(pixels, stride, composer.Inner.MidX, composer.Inner.Top + composer.Inner.Height / 5);
        Assert.Equal((245, 245, 245), (middle.R, middle.G, middle.B));
        // The cursor (white with black outline) sits at the centre of the screen.
        var tip = TestData.Pixel(pixels, stride, composer.Inner.MidX + 6, composer.Inner.MidY + 14);
        Assert.True(tip.R > 240, $"expected the white cursor body, got {tip}");
        Free(pixels);
        source.Release();
    }

    [Fact]
    public void ZoomsInOnActivity()
    {
        using var composer = new FrameComposer(_session, new RenderSettings(), _cursor, 10, outputWidth: 1920);
        var source = TestData.Frame(_session.Width, _session.Height);
        var pixels = Allocate(composer, out var stride);
        using var scratch = new ComposerScratch();

        composer.Render(source, 4.0, (nint)pixels, stride, scratch);
        TestData.SavePng(pixels, composer.Width, composer.Height, stride, "frame-zoomed.png");

        // Zoomed in 2× around the top-right of the window: the red block (top-left of the screen) is out of view.
        var topLeft = TestData.Pixel(pixels, stride, composer.Inner.Left + 30, composer.Inner.Top + 30);
        Assert.False(topLeft.R > 200 && topLeft.G < 50 && topLeft.B < 50);
        Free(pixels);
        source.Release();
    }

    [Fact]
    public void BlursMotion()
    {
        var settings = new RenderSettings();
        using var composer = new FrameComposer(_session, settings, _cursor, 10, outputWidth: 1920);
        var source = TestData.Frame(_session.Width, _session.Height);
        var blurred = Allocate(composer, out var stride);
        var sharp = Allocate(composer, out _);
        using var scratch = new ComposerScratch();

        // 1.9 s: zooming in quickly.
        composer.Render(source, 1.9, (nint)blurred, stride, scratch);
        using (var noBlur = new FrameComposer(_session, settings with { MotionBlur = false }, _cursor, 10, 1920))
            noBlur.Render(source, 1.9, (nint)sharp, stride, scratch);
        TestData.SavePng(blurred, composer.Width, composer.Height, stride, "frame-motion-blur.png");

        long difference = 0;
        for (var y = composer.Inner.Top; y < composer.Inner.Bottom; y++)
        for (var x = composer.Inner.Left; x < composer.Inner.Right; x++)
            difference += Math.Abs(blurred[y * stride + x * 4 + 1] - sharp[y * stride + x * 4 + 1]);
        Assert.True(difference > 100_000, $"motion blur changed only {difference}");

        // 2.5 s with clicks-only zooms: the camera is still and only the cursor streaks.
        using (var cursorOnly = new FrameComposer(_session, settings with { ClicksOnly = true }, _cursor, 10, 1920))
            cursorOnly.Render(source, 2.5, (nint)blurred, stride, scratch);
        TestData.SavePng(blurred, composer.Width, composer.Height, stride, "frame-cursor-blur.png");
        Free(blurred);
        Free(sharp);
        source.Release();
    }

    [Fact]
    public void UsesAnImageBackground()
    {
        var imagePath = Path.Combine(Directory.CreateTempSubdirectory().FullName, "bg.png");
        using (var surface = SKSurface.Create(new SKImageInfo(400, 300)))
        {
            surface.Canvas.Clear(SKColors.Orange);
            using var paint = new SKPaint { Color = SKColors.DarkGreen };
            surface.Canvas.DrawCircle(200, 150, 100, paint);
            using var data = surface.Snapshot().Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(imagePath, data.ToArray());
        }
        var settings = new RenderSettings { Background = new ImageBackground(imagePath), BackgroundBlur = 0.4, Padding = 0.1 };
        using var composer = new FrameComposer(_session, settings, _cursor, 10, outputWidth: 1280);
        var source = TestData.Frame(_session.Width, _session.Height);
        var pixels = Allocate(composer, out var stride);
        using var scratch = new ComposerScratch();

        composer.Render(source, 0.5, (nint)pixels, stride, scratch);
        TestData.SavePng(pixels, composer.Width, composer.Height, stride, "frame-image-background.png");

        var corner = TestData.Pixel(pixels, stride, 2, 2);
        Assert.True(corner.R > 200 && corner.B < 80, $"expected orange, got {corner}");
        Free(pixels);
        source.Release();
    }

    [Fact]
    public void MissingBackgroundImageFallsBackToTheGradient()
    {
        var settings = new RenderSettings { Background = new ImageBackground("/does/not/exist.png") };
        using var composer = new FrameComposer(_session, settings, _cursor, 10, outputWidth: 640);
        Assert.Equal(640, composer.Width);
    }

    [Fact]
    public void RendersFastEnough()
    {
        using var composer = new FrameComposer(_session, new RenderSettings(), _cursor, 10, outputWidth: 1920);
        using var preview = new FrameComposer(_session, new RenderSettings(), _cursor, 10, outputWidth: 1280, maxBlurSamples: 6)
        {
            ParallelBlur = true,
        };
        // Only the cursor moves at 2.5 s when zooming on clicks only.
        using var cursorOnly = new FrameComposer(_session, new RenderSettings { ClicksOnly = true }, _cursor, 10, outputWidth: 1920);
        var source = TestData.Frame(_session.Width, _session.Height);
        var pixels = Allocate(composer, out var stride);
        using var scratch = new ComposerScratch();
        composer.Render(source, 1.9, (nint)pixels, stride, scratch);  // warm up

        foreach (var (name, c, t) in new[] { ("export", composer, 0.5), ("export", composer, 1.9), ("export", composer, 4.0),
                                             ("preview", preview, 0.5), ("preview", preview, 1.9), ("preview", preview, 4.0),
                                             ("cursor only", cursorOnly, 2.5) })
        {
            var watch = Stopwatch.StartNew();
            const int n = 5;
            for (var i = 0; i < n; i++) c.Render(source, t + i / 60.0, (nint)pixels, stride, scratch);
            output.WriteLine($"{name} t={t}: {watch.Elapsed.TotalMilliseconds / n:F1} ms/frame");
        }
        Free(pixels);
        source.Release();
    }

    private static byte* Allocate(FrameComposer composer, out int stride)
    {
        stride = composer.Width * 4;
        return (byte*)NativeMemory.AllocZeroed((nuint)(stride * composer.Height));
    }

    private static void Free(byte* pixels) => NativeMemory.Free(pixels);
}
