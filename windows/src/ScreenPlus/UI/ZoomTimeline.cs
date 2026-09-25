using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenPlus.UI;

/// <summary>Shows where the auto-zooms happen and where playback is; click or drag to scrub.</summary>
internal sealed class ZoomTimeline : FrameworkElement
{
    public event EventHandler<double>? SeekRequested;

    public IReadOnlyList<CameraPath.Segment> Segments
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    } = [];

    public double Duration
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            InvalidateVisual();
        }
    }

    public double ZoomLevel
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            InvalidateVisual();
        }
    } = 2;

    public double Position
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            InvalidateVisual();
        }
    }

    public ZoomTimeline()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        var foreground = TryFindResource("TextFillColorPrimaryBrush") as SolidColorBrush;
        var track = new SolidColorBrush(foreground?.Color ?? Colors.Gray) { Opacity = 0.07 };
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, width, height), 6, 6);

        var scale = Duration > 0 ? width / Duration : 0;
        var accent = (TryFindResource("AccentFillColorDefaultBrush") as SolidColorBrush)?.Color ?? SystemColors.HighlightColor;
        var fill = new LinearGradientBrush(Lighten(accent), accent, 90);
        var label = string.Format(CultureInfo.CurrentCulture, "{0:0.0}×", ZoomLevel);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var segment in Segments)
        {
            var start = Math.Max(0, segment.Start);
            var end = Math.Min(Duration, segment.End);
            var rect = new Rect(start * scale, 4, Math.Max(3, (end - start) * scale), height - 8);
            dc.DrawRoundedRectangle(fill, null, rect, 5, 5);
            var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                         new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold,
                                                      FontStretches.Normal), 10, Brushes.White, dpi);
            if (text.Width + 12 <= rect.Width)
                dc.DrawText(text, new Point(rect.X + 6, rect.Y + (rect.Height - text.Height) / 2));
        }

        var x = Math.Clamp(Position * scale - 1.5, 0, width - 3);
        dc.DrawRoundedRectangle(Brushes.Red, null, new Rect(x, 0, 3, height), 1.5, 1.5);
    }

    private static Color Lighten(Color c) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * 0.25), (byte)(c.G + (255 - c.G) * 0.25), (byte)(c.B + (255 - c.B) * 0.25));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        Scrub(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured) Scrub(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    private void Scrub(double x)
    {
        if (ActualWidth <= 0 || Duration <= 0) return;
        SeekRequested?.Invoke(this, Math.Clamp(x / ActualWidth, 0, 1) * Duration);
    }
}
