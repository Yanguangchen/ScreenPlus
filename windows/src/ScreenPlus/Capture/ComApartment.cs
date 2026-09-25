using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace ScreenPlus.Capture;

internal static unsafe class ComApartment
{
    private static readonly Lazy<bool> Joined = new(() =>
    {
        CO_MTA_USAGE_COOKIE cookie;
        return SUCCEEDED(CoIncrementMTAUsage(&cookie));
    });

    /// <summary>
    /// Lets any thread use Windows Runtime and Media Foundation objects without its own COM setup:
    /// threads that haven't initialized COM join the process-wide multithreaded apartment.
    /// </summary>
    public static void EnsureMultithreaded() => _ = Joined.Value;
}
