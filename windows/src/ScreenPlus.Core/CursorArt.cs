using SkiaSharp;

namespace ScreenPlus;

/// <summary>The cursor bitmap we draw on top of the video.</summary>
public sealed class CursorArt : IDisposable
{
    /// <summary>High-resolution raster, so the cursor stays sharp when zoomed in.</summary>
    public SKImage Image { get; }
    /// <summary>Size of the cursor in screen points (DIPs).</summary>
    public SKSize PointSize { get; }
    /// <summary>In points, top-left origin.</summary>
    public SKPoint HotSpot { get; }

    public CursorArt(SKImage image, SKSize pointSize, SKPoint hotSpot)
    {
        Image = image;
        PointSize = pointSize;
        HotSpot = hotSpot;
    }

    public void Dispose() => Image.Dispose();

    /// <summary>The standard Windows arrow: white with a black outline and a soft shadow, drawn as vectors.</summary>
    public static CursorArt CreateDefault()
    {
        var size = new SKSize(17, 23);
        var tip = new SKPoint(1.5f, 1.5f);
        const float scale = 8;

        SKPoint[] points = [new(0, 0), new(0, 16), new(4, 12), new(7, 18), new(9.5f, 17), new(6.5f, 11), new(12, 11)];
        using var builder = new SKPathBuilder();
        builder.MoveTo(points[0]);
        foreach (var p in points.Skip(1)) builder.LineTo(p);
        builder.Close();
        using var path = builder.Detach();

        var info = new SKImageInfo((int)(size.Width * scale), (int)(size.Height * scale), SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        canvas.Translate(tip.X, tip.Y);

        using (var shadow = new SKPaint())
        {
            shadow.IsAntialias = true;
            shadow.Color = new SKColor(0, 0, 0, 90);
            shadow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0.9f);  // in points: follows the canvas scale
            canvas.Save();
            canvas.Translate(0.6f, 1.0f);
            canvas.DrawPath(path, shadow);
            canvas.Restore();
        }
        using (var fill = new SKPaint())
        {
            fill.IsAntialias = true;
            fill.Color = SKColors.White;
            canvas.DrawPath(path, fill);
        }
        using (var stroke = new SKPaint())
        {
            stroke.IsAntialias = true;
            stroke.Style = SKPaintStyle.Stroke;
            stroke.StrokeWidth = 1.1f;
            stroke.StrokeJoin = SKStrokeJoin.Round;
            stroke.Color = SKColors.Black;
            canvas.DrawPath(path, stroke);
        }

        return new CursorArt(surface.Snapshot(), size, tip);
    }
}
