namespace ScreenPlus;

public enum YuvMatrix { Bt601, Bt709 }

/// <summary>
/// Conversions between BGRA and NV12 (what H.264 encoders and decoders work with).
/// ScreenPlus writes BT.709 limited range, and reads whatever the file says.
/// </summary>
public static unsafe class Yuv
{
    /// <summary>
    /// BGRA → NV12, BT.709 limited range. <paramref name="factor"/> &gt; 1 box-filters the image
    /// down by that factor on the way (for screens too big for the H.264 encoder).
    /// </summary>
    public static void BgraToNv12(byte* bgra, int bgraStride, int width, int height,
                                  byte* yPlane, int yStride, byte* uvPlane, int uvStride, int factor = 1)
    {
        // Output size; both even.
        var w = width / factor & ~1;
        var h = height / factor & ~1;
        var rows = h / 2;
        var chunk = Math.Max(1, rows / (Environment.ProcessorCount * 4));
        Parallel.For(0, (rows + chunk - 1) / chunk, c =>
        {
            var end = Math.Min(rows, (c + 1) * chunk);
            for (var row = c * chunk; row < end; row++)
            {
                if (factor == 1) ConvertRowPair(bgra, bgraStride, w, row, yPlane, yStride, uvPlane, uvStride);
                else ConvertRowPairScaled(bgra, bgraStride, w, row, factor, yPlane, yStride, uvPlane, uvStride);
            }
        });
    }

    // BT.709 limited range, 16.16 fixed point.
    private const int YR = 11966, YG = 40254, YB = 4064;
    private const int UR = -6597, UG = -22187, UB = 28784;
    private const int VR = 28784, VG = -26147, VB = -2637;

    private static void ConvertRowPair(byte* bgra, int stride, int w, int row,
                                       byte* yPlane, int yStride, byte* uvPlane, int uvStride)
    {
        var s0 = bgra + (long)(2 * row) * stride;
        var s1 = s0 + stride;
        var y0 = yPlane + (long)(2 * row) * yStride;
        var y1 = y0 + yStride;
        var uv = uvPlane + (long)row * uvStride;
        for (var x = 0; x < w; x += 2)
        {
            var i = x * 4;
            int b00 = s0[i], g00 = s0[i + 1], r00 = s0[i + 2];
            int b01 = s0[i + 4], g01 = s0[i + 5], r01 = s0[i + 6];
            int b10 = s1[i], g10 = s1[i + 1], r10 = s1[i + 2];
            int b11 = s1[i + 4], g11 = s1[i + 5], r11 = s1[i + 6];
            y0[x] = Luma(r00, g00, b00);
            y0[x + 1] = Luma(r01, g01, b01);
            y1[x] = Luma(r10, g10, b10);
            y1[x + 1] = Luma(r11, g11, b11);
            int r = r00 + r01 + r10 + r11, g = g00 + g01 + g10 + g11, b = b00 + b01 + b10 + b11;
            uv[x] = Chroma(UR * r + UG * g + UB * b);
            uv[x + 1] = Chroma(VR * r + VG * g + VB * b);
        }
    }

    private static void ConvertRowPairScaled(byte* bgra, int stride, int w, int row, int factor,
                                             byte* yPlane, int yStride, byte* uvPlane, int uvStride)
    {
        var y0 = yPlane + (long)(2 * row) * yStride;
        var uv = uvPlane + (long)row * uvStride;
        var n = factor * factor;
        for (var x = 0; x < w; x += 2)
        {
            int rs = 0, gs = 0, bs = 0;
            for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
            {
                int r = 0, g = 0, b = 0;
                for (var sy = 0; sy < factor; sy++)
                {
                    var p = bgra + (long)((2 * row + dy) * factor + sy) * stride + (x + dx) * factor * 4;
                    for (var sx = 0; sx < factor; sx++, p += 4)
                    {
                        b += p[0];
                        g += p[1];
                        r += p[2];
                    }
                }
                r = (r + n / 2) / n;
                g = (g + n / 2) / n;
                b = (b + n / 2) / n;
                (y0 + dy * yStride)[x + dx] = Luma(r, g, b);
                rs += r;
                gs += g;
                bs += b;
            }
            uv[x] = Chroma(UR * rs + UG * gs + UB * bs);
            uv[x + 1] = Chroma(VR * rs + VG * gs + VB * bs);
        }
    }

    private static byte Luma(int r, int g, int b) => (byte)(((YR * r + YG * g + YB * b + 32768) >> 16) + 16);

    /// <summary>Chroma from a sum of four pixels.</summary>
    private static byte Chroma(int sum4) => (byte)(((sum4 + 131072) >> 18) + 128);

    /// <summary>NV12 → opaque BGRA.</summary>
    public static void Nv12ToBgra(byte* yPlane, int yStride, byte* uvPlane, int uvStride, int width, int height,
                                  byte* bgra, int bgraStride, YuvMatrix matrix = YuvMatrix.Bt709, bool fullRange = false)
    {
        // Coefficients in 16.16 fixed point.
        double ky, kr, kgu, kgv, kb;
        if (matrix == YuvMatrix.Bt709)
            (kr, kgu, kgv, kb) = fullRange ? (1.5748, 0.187324, 0.468124, 1.8556) : (1.792741, 0.213249, 0.532909, 2.112402);
        else
            (kr, kgu, kgv, kb) = fullRange ? (1.402, 0.344136, 0.714136, 1.772) : (1.596027, 0.391762, 0.812968, 2.017232);
        ky = fullRange ? 1.0 : 1.164384;
        int cy = (int)(ky * 65536 + 0.5), cr = (int)(kr * 65536 + 0.5), cgu = (int)(kgu * 65536 + 0.5),
            cgv = (int)(kgv * 65536 + 0.5), cb = (int)(kb * 65536 + 0.5);
        var yOffset = fullRange ? 0 : 16;

        var rows = height / 2;
        var chunk = Math.Max(1, rows / (Environment.ProcessorCount * 4));
        Parallel.For(0, (rows + chunk - 1) / chunk, c =>
        {
            var end = Math.Min(rows, (c + 1) * chunk);
            for (var row = c * chunk; row < end; row++)
            {
                for (var half = 0; half < 2; half++)
                {
                    var yRow = 2 * row + half;
                    var ys = yPlane + (long)yRow * yStride;
                    var uvs = uvPlane + (long)row * uvStride;
                    var o = bgra + (long)yRow * bgraStride;
                    for (var x = 0; x < width; x++)
                    {
                        var yy = (ys[x] - yOffset) * cy + 32768;
                        var u = uvs[x & ~1] - 128;
                        var v = uvs[(x & ~1) + 1] - 128;
                        o[0] = Clamp((yy + cb * u) >> 16);
                        o[1] = Clamp((yy - cgu * u - cgv * v) >> 16);
                        o[2] = Clamp((yy + cr * v) >> 16);
                        o[3] = 255;
                        o += 4;
                    }
                }
            }
        });
        // Odd heights: repeat the last full row.
        if ((height & 1) == 1 && height > 1)
            Buffer.MemoryCopy(bgra + (long)(height - 2) * bgraStride, bgra + (long)(height - 1) * bgraStride,
                              bgraStride, (long)width * 4);
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
