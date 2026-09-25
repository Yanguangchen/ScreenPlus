using System.Runtime.InteropServices;
using SkiaSharp;

namespace ScreenPlus;

/// <summary>
/// One decoded source frame: opaque BGRA pixels in native memory, reference counted because
/// the same frame is held by several output frames while rendering in parallel.
/// </summary>
public sealed unsafe class VideoFrame
{
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte* Data { get; private set; }
    /// <summary>Presentation time in seconds, relative to the first frame of the recording.</summary>
    public double Time { get; set; }

    private int _refCount = 1;
    private readonly Lock _lock = new();
    private SKImage? _image;
    private VideoFrame? _half;

    public VideoFrame(int width, int height)
    {
        Width = width;
        Height = height;
        Stride = width * 4;
        Data = (byte*)NativeMemory.AlignedAlloc((nuint)((long)Stride * height), 64);
    }

    public Span<byte> Pixels => new(Data, Stride * Height);

    public VideoFrame AddRef()
    {
        Interlocked.Increment(ref _refCount);
        return this;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) != 0) return;
        _image?.Dispose();
        _image = null;
        _half?.Release();
        _half = null;
        NativeMemory.AlignedFree(Data);
        Data = null;
    }

    /// <summary>The pixels as a Skia image (no copy). Valid while the frame is referenced.</summary>
    public SKImage Image
    {
        get
        {
            lock (_lock)
                return _image ??= SKImage.FromPixels(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque),
                                                     (nint)Data, Stride);
        }
    }

    /// <summary>
    /// The frame at 1/2^level size, built on first use. Drawing a big frame much smaller with plain
    /// bilinear filtering shimmers; a pre-shrunk copy keeps text crisp.
    /// </summary>
    public VideoFrame Level(int level)
    {
        if (level <= 0 || Width < 4 || Height < 4) return this;
        VideoFrame half;
        lock (_lock)
            half = _half ??= Downscale2x();
        return half.Level(level - 1);
    }

    /// <summary>2×2 box filter.</summary>
    private VideoFrame Downscale2x()
    {
        var result = new VideoFrame(Width / 2, Height / 2) { Time = Time };
        var src = Data;
        var dst = result.Data;
        int srcStride = Stride, dstStride = result.Stride, w = result.Width;
        Parallel.For(0, result.Height, y =>
        {
            var a = src + (long)(2 * y) * srcStride;
            var b = a + srcStride;
            var o = dst + (long)y * dstStride;
            for (var x = 0; x < w; x++)
            {
                var i = x * 8;
                o[x * 4 + 0] = (byte)((a[i + 0] + a[i + 4] + b[i + 0] + b[i + 4] + 2) >> 2);
                o[x * 4 + 1] = (byte)((a[i + 1] + a[i + 5] + b[i + 1] + b[i + 5] + 2) >> 2);
                o[x * 4 + 2] = (byte)((a[i + 2] + a[i + 6] + b[i + 2] + b[i + 6] + 2) >> 2);
                o[x * 4 + 3] = 255;
            }
        });
        return result;
    }
}
