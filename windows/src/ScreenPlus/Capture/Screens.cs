using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.MONITOR;
using static TerraFX.Interop.Windows.Windows;

namespace ScreenPlus.Capture;

/// <summary>A display, in physical pixels (the app is per-monitor DPI aware).</summary>
internal readonly record struct ScreenInfo(nint Handle, int Left, int Top, int Width, int Height,
                                           int WorkLeft, int WorkTop, int WorkWidth, int WorkHeight, double Scale)
{
    public int WorkBottom => WorkTop + WorkHeight;
}

internal static unsafe class Screens
{
    /// <summary>The display the mouse pointer is on.</summary>
    public static ScreenInfo WithCursor()
    {
        POINT point;
        if (!GetCursorPos(&point)) point = default;
        return FromMonitor(MonitorFromPoint(point, (uint)MONITOR_DEFAULTTONEAREST));
    }

    public static ScreenInfo FromMonitor(HMONITOR monitor)
    {
        MONITORINFO info = default;
        info.cbSize = (uint)sizeof(MONITORINFO);
        GetMonitorInfoW(monitor, &info);
        uint dpiX = 96, dpiY = 96;
        if (FAILED(GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dpiX, &dpiY)) || dpiX == 0) dpiX = 96;
        var r = info.rcMonitor;
        var w = info.rcWork;
        return new ScreenInfo(monitor, r.left, r.top, r.right - r.left, r.bottom - r.top,
                              w.left, w.top, w.right - w.left, w.bottom - w.top, dpiX / 96.0);
    }

    /// <summary>Current pointer position in physical screen pixels.</summary>
    public static (int X, int Y)? CursorPosition()
    {
        POINT point;
        return GetCursorPos(&point) ? (point.x, point.y) : null;
    }
}
