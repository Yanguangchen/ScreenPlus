using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace ScreenPlus;

/// <summary>
/// Builds one output frame (zoom, cursor, motion blur, background) from one source frame.
///
/// Shared by the live preview and the exporter, so what you preview is what you export.
/// Immutable after construction, so several threads can render at once, each with its own
/// <see cref="ComposerScratch"/>.
/// </summary>
public sealed unsafe class FrameComposer : IDisposable
{
    public CameraPath Path { get; }
    /// <summary>Output size in pixels.</summary>
    public int Width { get; }
    public int Height { get; }
    /// <summary>Where the recorded screen sits inside the output.</summary>
    public SKRectI Inner { get; }

    private readonly RecordingSession _session;
    private readonly RenderSettings _settings;
    private readonly CursorArt _cursor;
    private readonly int _maxBlurSamples;
    private readonly SKImage _background;
    private readonly SKRoundRect _mask;

    private static readonly SKSamplingOptions ScreenSampling = new(SKFilterMode.Linear, SKMipmapMode.None);
    private static readonly SKSamplingOptions CursorSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public FrameComposer(RecordingSession session, RenderSettings settings, CursorArt cursor, double duration,
                         int outputWidth, int maxBlurSamples = 16)
    {
        _session = session;
        _settings = settings;
        _cursor = cursor;
        _maxBlurSamples = maxBlurSamples;
        Path = new CameraPath(session, settings, duration);

        var outW = Math.Min(outputWidth, Math.Max(session.Width, 640)) & ~1;
        var pad = Round(outW * settings.Padding);
        var innerW = outW - pad * 2;
        var innerH = Round((double)innerW * session.Height / session.Width);
        var outH = (innerH + pad * 2) & ~1;
        Width = outW;
        Height = outH;
        // Centred vertically, rounding the same way as the macOS version (which measures from the bottom).
        var fromBottom = Round((outH - innerH) / 2.0);
        Inner = SKRectI.Create(pad, outH - fromBottom - innerH, innerW, innerH);

        var radius = innerW * 0.012f;
        _mask = new SKRoundRect(new SKRect(Inner.Left, Inner.Top, Inner.Right, Inner.Bottom), radius, radius);
        _background = MakeBackground(Width, Height, Inner, settings);
    }

    public void Dispose()
    {
        _background.Dispose();
        _mask.Dispose();
    }

    /// <summary>Renders the output frame for time <paramref name="t"/> into <paramref name="destination"/> (BGRA, Width × Height).</summary>
    public void Render(VideoFrame source, double t, nint destination, int destinationStride, ComposerScratch scratch)
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info, destination, destinationStride)
            ?? throw new InvalidOperationException("Could not create a drawing surface.");
        var canvas = surface.Canvas;
        canvas.DrawImage(_background, 0, 0, SKSamplingOptions.Default);

        var pulse = Path.ClickPulse(t);
        var state = Path.StateAt(t);
        canvas.Save();
        canvas.ClipRoundRect(_mask, SKClipOperation.Intersect, antialias: true);
        canvas.Translate(Inner.Left, Inner.Top);

        if (!_settings.MotionBlur)
        {
            DrawScreen(canvas, source, state);
            DrawCursor(canvas, state, pulse, null);
            canvas.Restore();
            return;
        }

        // Motion blur = average several sub-frames across the shutter interval.
        // The number of sub-frames adapts to how far things move, so still frames stay cheap.
        // One output frame's worth of recording time: sped-up video blurs more, slow motion less.
        var shutter = _settings.Speed / _settings.Fps;
        var a = Path.StateAt(t - shutter / 2);
        var b = Path.StateAt(t + shutter / 2);
        var (camera, cursor) = MotionInPixels(a, b);
        var samples = Math.Min(_maxBlurSamples, Math.Max(1, (int)(Math.Max(camera, cursor) / 1.5) + 1));
        var states = new CameraState[samples];
        for (var i = 0; i < samples; i++)
            states[i] = samples == 1 ? state : Path.StateAt(t - shutter / 2 + shutter * (i + 0.5) / samples);

        if (samples == 1)
        {
            DrawScreen(canvas, source, state);
            DrawCursor(canvas, state, pulse, null);
        }
        else if (camera < 0.5)
        {
            // Only the cursor moves: draw the screen once, then average just the cursor in a layer.
            // layer = Σ cursorᵢ/n, and layer-over-screen is exactly the average of cursorᵢ-over-screen.
            DrawScreen(canvas, source, state);
            var bounds = CursorRect(states[0], pulse);
            foreach (var s in states) bounds.Union(CursorRect(s, pulse));
            bounds.Inflate(2, 2);
            using var paint = new SKPaint();
            paint.ColorF = new SKColorF(0, 0, 0, 1f / samples);
            paint.BlendMode = SKBlendMode.Plus;
            canvas.SaveLayer(bounds, null);
            foreach (var s in states) DrawCursor(canvas, s, pulse, paint);
            canvas.Restore();
        }
        else
        {
            using var averaged = RenderBlurred(source, states, pulse, scratch);
            canvas.DrawImage(averaged, 0, 0, SKSamplingOptions.Default);
        }
        canvas.Restore();
    }

    /// <summary>Renders one sub-frame per camera state and averages them, at inner-rect size.</summary>
    private SKImage RenderBlurred(VideoFrame source, CameraState[] states, double pulse, ComposerScratch scratch)
    {
        var samples = states.Length;
        var count = Inner.Width * Inner.Height * 4;
        if (!ParallelBlur)
        {
            scratch.Ensure(Inner.Width, Inner.Height, 1);
            var canvas = scratch.Surfaces[0].Canvas;
            for (var i = 0; i < samples; i++)
            {
                // No need to clear: the screen always covers the whole layer.
                DrawScreen(canvas, source, states[i]);
                DrawCursor(canvas, states[i], pulse, null);
                Accumulate(scratch.Pixels[0], scratch.Sums, count, first: i == 0);
            }
        }
        else
        {
            // Preview: render the sub-frames side by side on all cores, then add them up.
            scratch.Ensure(Inner.Width, Inner.Height, samples);
            Parallel.For(0, samples, i =>
            {
                var canvas = scratch.Surfaces[i].Canvas;
                DrawScreen(canvas, source, states[i]);
                DrawCursor(canvas, states[i], pulse, null);
            });
            for (var i = 0; i < samples; i++)
                Accumulate(scratch.Pixels[i], scratch.Sums, count, first: i == 0);
        }
        Average(scratch.Sums, scratch.Pixels[0], count, samples);
        // A fresh wrapper each time: Skia may cache per image, and the pixels just changed.
        return SKImage.FromPixels(scratch.Info, (nint)scratch.Pixels[0], Inner.Width * 4)!;
    }

    /// <summary>
    /// Renders sub-frames on several threads. For the live preview, which shows one frame at a time;
    /// export renders several frames at once instead.
    /// </summary>
    public bool ParallelBlur { get; init; }

    /// <summary>Visible part of the screen for a camera state, with the inner rect's top-left at the canvas origin.</summary>
    private void DrawScreen(SKCanvas canvas, VideoFrame source, CameraState state)
    {
        var (originX, originY, scale) = View(state);
        double W = _session.Width;

        // The decoded frame may not match the recorded size exactly. When drawing it at less than
        // half size, use a pre-shrunk copy so text doesn't shimmer.
        var drawScale = scale * W / source.Width;
        var frame = source.Level(drawScale < 0.25 ? 2 : drawScale < 0.5 ? 1 : 0);
        canvas.Save();
        canvas.Scale((float)scale);
        canvas.Translate((float)-originX, (float)-originY);
        canvas.Scale((float)(W / frame.Width));
        canvas.DrawImage(frame.Image, 0, 0, ScreenSampling);
        canvas.Restore();
    }

    /// <summary>The cursor, scaled with the zoom and "pressed" on click.</summary>
    private void DrawCursor(SKCanvas canvas, CameraState state, double pulse, SKPaint? paint) =>
        canvas.DrawImage(_cursor.Image, CursorRect(state, pulse), CursorSampling, paint);

    private SKRect CursorRect(CameraState state, double pulse)
    {
        var (originX, originY, scale) = View(state);
        var size = _cursor.PointSize;
        var cursorH = size.Height * _settings.CursorScale * (1 - 0.18 * pulse) * _session.PixelsPerPoint * scale;
        var cursorW = cursorH * size.Width / size.Height;
        var hotX = _cursor.HotSpot.X / size.Width * cursorW;
        var hotY = _cursor.HotSpot.Y / size.Height * cursorH;
        var px = (state.CursorX - originX) * scale;
        var py = (state.CursorY - originY) * scale;
        return SKRect.Create((float)(px - hotX), (float)(py - hotY), (float)cursorW, (float)cursorH);
    }

    /// <summary>Top-left of the visible part of the screen, in source pixels, and output pixels per source pixel.</summary>
    private (double OriginX, double OriginY, double Scale) View(CameraState state)
    {
        double viewW = _session.Width / state.Zoom, viewH = _session.Height / state.Zoom;
        return (state.CenterX - viewW / 2, state.CenterY - viewH / 2, Inner.Width / viewW);
    }

    /// <summary>Rough on-screen movement between two camera states, in output pixels: of the view, and of the cursor.</summary>
    private (double Camera, double Cursor) MotionInPixels(CameraState a, CameraState b)
    {
        double W = _session.Width;
        double scaleA = Inner.Width / (W / a.Zoom), scaleB = Inner.Width / (W / b.Zoom);
        var pan = Hypot(a.CenterX - b.CenterX, a.CenterY - b.CenterY) * Math.Max(scaleA, scaleB);
        var zoom = Math.Abs(Math.Log(a.Zoom) - Math.Log(b.Zoom)) * Inner.Width;
        var cursor = Hypot(a.CursorX - b.CursorX, a.CursorY - b.CursorY) * Math.Max(scaleA, scaleB);
        return (pan + zoom, cursor);
    }

    private static void Accumulate(byte* src, ushort* sums, int count, bool first)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var n = Vector<byte>.Count;
            var half = n / 2;
            for (; i <= count - n; i += n)
            {
                Vector.Widen(Unsafe.ReadUnaligned<Vector<byte>>(src + i), out Vector<ushort> lo, out Vector<ushort> hi);
                if (!first)
                {
                    lo += Unsafe.ReadUnaligned<Vector<ushort>>(sums + i);
                    hi += Unsafe.ReadUnaligned<Vector<ushort>>(sums + i + half);
                }
                Unsafe.WriteUnaligned(sums + i, lo);
                Unsafe.WriteUnaligned(sums + i + half, hi);
            }
        }
        for (; i < count; i++)
            sums[i] = (ushort)((first ? 0 : sums[i]) + src[i]);
    }

    /// <summary>
    /// dst = round(sum / n), computed as ((sum + n/2) · ⌈65536/n⌉) >> 16, which is exact for
    /// sums of up to 16 bytes.
    /// </summary>
    private static void Average(ushort* sums, byte* dst, int count, int n)
    {
        var reciprocal = (uint)((65536 + n - 1) / n);
        var halfN = (uint)(n / 2);
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var width = Vector<byte>.Count;
            var half = width / 2;
            var r = new Vector<uint>(reciprocal);
            var h = new Vector<uint>(halfN);
            for (; i <= count - width; i += width)
            {
                var lo = Scale(Unsafe.ReadUnaligned<Vector<ushort>>(sums + i), r, h);
                var hi = Scale(Unsafe.ReadUnaligned<Vector<ushort>>(sums + i + half), r, h);
                Unsafe.WriteUnaligned(dst + i, Vector.Narrow(lo, hi));
            }
        }
        for (; i < count; i++)
            dst[i] = (byte)(((sums[i] + halfN) * reciprocal) >> 16);

        static Vector<ushort> Scale(Vector<ushort> v, Vector<uint> r, Vector<uint> h)
        {
            Vector.Widen(v, out Vector<uint> a, out Vector<uint> b);
            a = Vector.ShiftRightLogical((a + h) * r, 16);
            b = Vector.ShiftRightLogical((b + h) * r, 16);
            return Vector.Narrow(a, b);
        }
    }

    // MARK: Static layers

    /// <summary>
    /// Background + drop shadow never change, so render them once. They're blended in linear light,
    /// as Core Image does on macOS, so the shadow and gradient look the same.
    /// </summary>
    private static SKImage MakeBackground(int w, int h, SKRectI inner, RenderSettings settings)
    {
        using var linear = SKColorSpace.CreateSrgbLinear();
        using var srgb = SKColorSpace.CreateSrgb();
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.RgbaF16, SKAlphaType.Premul, linear))
            ?? throw new InvalidOperationException("Could not create the background surface.");
        var canvas = surface.Canvas;
        DrawBackdrop(canvas, w, h, settings, srgb);

        var radius = inner.Width * 0.012f;
        var shadowRect = new SKRect(inner.Left, inner.Top, inner.Right, inner.Bottom);
        shadowRect.Offset(0, inner.Height * 0.012f);
        using (var shadow = new SKPaint())
        {
            shadow.IsAntialias = true;
            shadow.ColorF = new SKColorF(0, 0, 0, 0.45f);
            shadow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, inner.Width * 0.015f);
            canvas.DrawRoundRect(shadowRect, radius, radius, shadow);
        }

        // Back to 8-bit sRGB, which is what every frame is composed in.
        var rowBytes = w * 4;
        var pixels = NativeMemory.AlignedAlloc((nuint)(rowBytes * h), 64);
        try
        {
            using var snapshot = surface.Snapshot();
            snapshot.ReadPixels(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul, srgb), (nint)pixels, rowBytes);
            return SKImage.FromPixelCopy(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque), (nint)pixels, rowBytes)!;
        }
        finally
        {
            NativeMemory.AlignedFree(pixels);
        }
    }

    /// <summary>The gradient or custom image behind the screen, filling the whole canvas.</summary>
    private static void DrawBackdrop(SKCanvas canvas, int w, int h, RenderSettings settings, SKColorSpace srgb)
    {
        if (settings.Background is ImageBackground { Path: var path } && LoadImage(path) is { } image)
        {
            using (image)
            {
                // Aspect-fill: scale to cover the canvas, centred, then crop.
                var scale = Math.Max((double)w / image.Width, (double)h / image.Height);
                double dw = image.Width * scale, dh = image.Height * scale;
                var dest = SKRect.Create((float)((w - dw) / 2), (float)((h - dh) / 2), (float)dw, (float)dh);
                using var paint = new SKPaint();
                if (settings.BackgroundBlur > 0)
                {
                    var sigma = (float)(settings.BackgroundBlur * w * 0.03);
                    paint.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
                }
                canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            }
            return;
        }

        var preset = GradientPreset.At(settings.Background is GradientBackground g ? g.Index : 0);
        using var gradient = MakeGradient(w, h, preset, srgb);
        canvas.DrawImage(gradient, 0, 0, SKSamplingOptions.Default);
    }

    /// <summary>
    /// Top-left to bottom-right gradient, interpolated in linear light like Core Image, with a
    /// little dither so dark gradients don't band.
    /// </summary>
    private static SKImage MakeGradient(int w, int h, GradientPreset preset, SKColorSpace srgb)
    {
        const int steps = 2048;
        var lut = new float[(steps + 1) * 3];
        for (var i = 0; i <= steps; i++)
        {
            var u = (double)i / steps;
            lut[i * 3 + 0] = (float)(ToSrgb(Lerp(ToLinear(preset.From.B), ToLinear(preset.To.B), u)) * 255);
            lut[i * 3 + 1] = (float)(ToSrgb(Lerp(ToLinear(preset.From.G), ToLinear(preset.To.G), u)) * 255);
            lut[i * 3 + 2] = (float)(ToSrgb(Lerp(ToLinear(preset.From.R), ToLinear(preset.To.R), u)) * 255);
        }

        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque, srgb);
        var bitmap = new SKBitmap(info);
        var pixels = (byte*)bitmap.GetPixels();
        var rowBytes = bitmap.RowBytes;
        double lengthSquared = (double)w * w + (double)h * h;
        Parallel.For(0, h, y =>
        {
            var row = pixels + (long)y * rowBytes;
            for (var x = 0; x < w; x++)
            {
                var u = Math.Clamp(((x + 0.5) * w + (y + 0.5) * h) / lengthSquared, 0, 1);
                var li = (int)(u * steps + 0.5) * 3;
                var dither = Dither(x, y);
                row[x * 4 + 0] = ToByte(lut[li + 0] + dither);
                row[x * 4 + 1] = ToByte(lut[li + 1] + dither);
                row[x * 4 + 2] = ToByte(lut[li + 2] + dither);
                row[x * 4 + 3] = 255;
            }
        });
        bitmap.SetImmutable();
        var image = SKImage.FromBitmap(bitmap)!;
        bitmap.Dispose();
        return image;
    }

    /// <summary>Loads an image file with its EXIF orientation applied, or null if it can't be read.</summary>
    public static SKImage? LoadImage(string path)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec is null) return null;
            var info = codec.Info.WithColorType(SKColorType.Rgba8888).WithAlphaType(SKAlphaType.Premul);
            if (info.Width <= 0 || info.Height <= 0) return null;
            using var bitmap = new SKBitmap(info);
            var result = codec.GetPixels(info, bitmap.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
            bitmap.SetImmutable();
            return Orient(bitmap, codec.EncodedOrigin);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static SKImage Orient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        float w = bitmap.Width, h = bitmap.Height;
        SKMatrix? matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => null,
        };
        if (matrix is null) return SKImage.FromBitmap(bitmap)!;

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var info = bitmap.Info.WithSize(swap ? bitmap.Height : bitmap.Width, swap ? bitmap.Width : bitmap.Height);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Could not rotate the image.");
        using var image = SKImage.FromBitmap(bitmap)!;
        surface.Canvas.SetMatrix(matrix.Value);
        surface.Canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default);
        return surface.Snapshot();
    }

    private static float Dither(int x, int y)
    {
        var n = (uint)(x * 73856093) ^ (uint)(y * 19349663);
        n = (n ^ (n >> 13)) * 0x5bd1e995;
        n ^= n >> 15;
        return (n & 0xFFFF) / 65536f - 0.5f;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
    private static double Lerp(double a, double b, double u) => a + (b - a) * u;
    private static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    private static double ToSrgb(double l) => l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);
}

/// <summary>Per-thread working memory for <see cref="FrameComposer.Render"/>.</summary>
public sealed unsafe class ComposerScratch : IDisposable
{
    internal SKImageInfo Info { get; private set; }
    internal List<SKSurface> Surfaces { get; } = [];
    internal List<nint> Buffers { get; } = [];
    internal ushort* Sums { get; private set; }
    internal PixelList Pixels => new(Buffers);

    internal readonly struct PixelList(List<nint> buffers)
    {
        public byte* this[int index] => (byte*)buffers[index];
    }

    /// <summary>Makes sure there are <paramref name="count"/> sub-frame buffers of the given size.</summary>
    internal void Ensure(int width, int height, int count)
    {
        if (Info.Width != width || Info.Height != height) Free();
        Info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bytes = (nuint)width * (nuint)height * 4;
        if (Sums == null) Sums = (ushort*)NativeMemory.AlignedAlloc(bytes * 2, 64);
        while (Buffers.Count < count)
        {
            var pixels = (nint)NativeMemory.AlignedAlloc(bytes, 64);
            Buffers.Add(pixels);
            Surfaces.Add(SKSurface.Create(Info, pixels, width * 4)
                         ?? throw new InvalidOperationException("Could not create a drawing surface."));
        }
    }

    private void Free()
    {
        foreach (var surface in Surfaces) surface.Dispose();
        foreach (var buffer in Buffers) NativeMemory.AlignedFree((void*)buffer);
        Surfaces.Clear();
        Buffers.Clear();
        if (Sums != null) NativeMemory.AlignedFree(Sums);
        Sums = null;
    }

    public void Dispose() => Free();
}
