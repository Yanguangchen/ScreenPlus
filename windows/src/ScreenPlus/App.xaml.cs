using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ScreenPlus.Capture;
using ScreenPlus.UI;

namespace ScreenPlus;

public partial class App : Application
{
    internal AppModel Model { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ComApartment.EnsureMultithreaded();

#pragma warning disable WPF0001  // the Windows 11 look; still "experimental" in WPF
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        // The main window is hidden while the floating toolbar is up; that must not quit the app.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandledException;

        Model = new AppModel();
        var main = new MainWindow(Model);
        Model.MainWindow = main;
        Model.Toolbar = new ToolbarWindow(Model);
        Model.Tray = new TrayIcon(Model);
        main.Closed += (_, _) => Shutdown();

        // On launch we start in the floating toolbar, like the Mac app.
        Model.ShowRecorder();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Model?.Shutdown();
        Model?.Tray?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Trace.WriteLine($"ScreenPlus: unhandled exception: {e.Exception}");
        e.Handled = true;
        Model?.Fail(e.Exception.Message);
        Model?.HideRecorder();
    }
}
