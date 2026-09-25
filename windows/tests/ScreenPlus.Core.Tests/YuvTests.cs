using System.Runtime.InteropServices;

namespace ScreenPlus.Tests;

public unsafe class YuvTests
{
    [Theory]
    [InlineData(255, 255, 255, 235, 128, 128)]
    [InlineData(0, 0, 0, 16, 128, 128)]
    [InlineData(255, 0, 0, 63, 102, 240)]
    [InlineData(0, 0, 255, 32, 240, 118)]
    public void ConvertsKnownColorsWithBt709(byte r, byte g, byte b, byte y, byte u, byte v)
    {
        var bgra = new byte[4 * 4];
        for (var i = 0; i < 4; i++) (bgra[i * 4], bgra[i * 4 + 1], bgra[i * 4 + 2], bgra[i * 4 + 3]) = (b, g, r, 255);
        var nv12 = new byte[6];
        fixed (byte* src = bgra)
        fixed (byte* dst = nv12)
            Yuv.BgraToNv12(src, 8, 2, 2, dst, 2, dst + 4, 2);

        Assert.All(nv12[..4], value => Assert.InRange(value, y - 1, y + 1));
        Assert.InRange(nv12[4], u - 1, u + 1);
        Assert.InRange(nv12[5], v - 1, v + 1);
    }

    [Fact]
    public void RoundTripsSmoothImages()
    {
        const int w = 64, h = 48;
        var bgra = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            bgra[i] = (byte)(x * 4);
            bgra[i + 1] = (byte)(y * 5);
            bgra[i + 2] = (byte)(255 - x * 2 - y);
            bgra[i + 3] = 255;
        }
        var nv12 = new byte[w * h * 3 / 2];
        var back = new byte[bgra.Length];
        fixed (byte* src = bgra)
        fixed (byte* yuv = nv12)
        fixed (byte* dst = back)
        {
            Yuv.BgraToNv12(src, w * 4, w, h, yuv, w, yuv + w * h, w);
            Yuv.Nv12ToBgra(yuv, w, yuv + w * h, w, w, h, dst, w * 4);
        }

        for (var i = 0; i < bgra.Length; i++)
            Assert.InRange(back[i] - bgra[i], -6, 6);
    }

    [Fact]
    public void DownscalesWhileConverting()
    {
        const int w = 8, h = 4;
        var bgra = new byte[w * h * 4];
        // Left half white, right half black.
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            bgra.AsSpan((y * w + x) * 4, 4).Fill(x < w / 2 ? (byte)255 : (byte)0);
        var nv12 = new byte[4 * 2 * 3 / 2];
        fixed (byte* src = bgra)
        fixed (byte* yuv = nv12)
            Yuv.BgraToNv12(src, w * 4, w, h, yuv, 4, yuv + 8, 4, factor: 2);

        Assert.Equal([235, 235, 16, 16, 235, 235, 16, 16], nv12[..8]);
    }

    [Fact]
    public void Bt601FullRangeDecodes()
    {
        byte[] nv12 = [255, 255, 255, 255, 128, 128];
        var bgra = new byte[16];
        fixed (byte* yuv = nv12)
        fixed (byte* dst = bgra)
            Yuv.Nv12ToBgra(yuv, 2, yuv + 4, 2, 2, 2, dst, 8, YuvMatrix.Bt601, fullRange: true);

        Assert.All(bgra, value => Assert.Equal(255, value));
    }

    [Fact]
    public void FrameLevelsHalveTheSize()
    {
        var frame = TestData.Frame(640, 360);
        var half = frame.Level(1);
        var quarter = frame.Level(2);

        Assert.Equal((320, 180), (half.Width, half.Height));
        Assert.Equal((160, 90), (quarter.Width, quarter.Height));
        Assert.Same(half, frame.Level(1));
        // Box-filtered: the top-left red block stays red.
        var p = TestData.Pixel(quarter.Data, quarter.Stride, 5, 3);
        Assert.Equal((0, 0, 255), (p.B, p.G, p.R));
        frame.Release();
    }
}
