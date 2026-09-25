using System.Windows;
using System.Windows.Interop;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.GWL;
using static TerraFX.Interop.Windows.SWP;
using static TerraFX.Interop.Windows.Windows;
using static TerraFX.Interop.Windows.WS;

namespace ScreenPlus.UI;

/// <summary>Win32 window tweaks WPF doesn't expose.</summary>
internal static unsafe class NativeWindow
{
    public static HWND Handle(Window window) => (HWND)new WindowInteropHelper(window).EnsureHandle();

    /// <summary>
    /// Keeps the window out of every screen capture, including our own recordings
    /// (the equivalent of NSWindow.sharingType = .none).
    /// </summary>
    public static bool ExcludeFromCapture(Window window, bool exclude = true) =>
        SetWindowDisplayAffinity(Handle(window), (uint)(exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE));

    /// <summary>A floating panel: never takes focus from the app being recorded, and has no taskbar button.</summary>
    public static void MakeNonActivatingToolWindow(Window window)
    {
        var hwnd = Handle(window);
        var style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>Moves the window to a position in physical screen pixels, keeping it on top.</summary>
    public static void MoveTopmost(Window window, int x, int y) =>
        SetWindowPos(Handle(window), HWND.HWND_TOPMOST, x, y, 0, 0, (uint)(SWP_NOSIZE | SWP_NOACTIVATE));

    /// <summary>Physical pixels per DIP for the window's current monitor.</summary>
    public static double Scale(Window window)
    {
        var dpi = GetDpiForWindow(Handle(window));
        return dpi == 0 ? 1 : dpi / 96.0;
    }
}
