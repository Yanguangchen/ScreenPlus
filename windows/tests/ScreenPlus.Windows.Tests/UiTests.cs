using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenPlus.UI;
using Xunit.Abstractions;

namespace ScreenPlus.WindowsTests;

/// <summary>
/// Loads every window with real data and saves pictures of them, so layout and XAML mistakes show up
/// on CI (the PNGs are uploaded with the test results).
/// </summary>
public class UiTests(ITestOutputHelper output)
{
    [Fact]
    public void WindowsLoadAndRender()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Run();
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "the UI test hung");
        if (failure != null) throw new Xunit.Sdk.XunitException($"UI failed: {failure}");
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new App();
        app.InitializeComponent();
        app.ThemeMode = ThemeMode.Light;

        var model = new AppModel();
        var main = new MainWindow(model);
        var toolbar = new ToolbarWindow(model);
        model.MainWindow = main;
        model.Toolbar = toolbar;

        main.Show();
        Pump(0.5);
        Save(main, "ui-home.png");

        toolbar.ShowAtBottomOfScreen();
        Pump(0.3);
        Save(toolbar, "ui-toolbar.png");

        var folder = SyntheticRecording.Create();
        var session = RecordingSession.Load(Path.Combine(folder, "events.json"));
        var opening = model.OpenEditor(session, folder);
        PumpUntil(() => opening.IsCompleted, TimeSpan.FromSeconds(30));
        opening.GetAwaiter().GetResult();
        Assert.Equal(Phase.Editing, model.Phase);
        model.Player!.Pause();
        model.Seek(1.6);
        PumpUntil(() => model.PreviewImage != null, TimeSpan.FromSeconds(10));
        Pump(1.0);
        Assert.NotNull(model.PreviewImage);
        Assert.Single(model.Segments);
        Save(main, "ui-editor.png");

        // Changing a setting rebuilds the preview.
        model.ZoomLevel = 3;
        model.Padding = 0.1;
        model.SelectGradient(3);
        Pump(1.0);
        Save(main, "ui-editor-settings.png");

        app.ThemeMode = ThemeMode.Dark;
        Pump(0.6);
        Save(main, "ui-editor-dark.png");

        model.Fail("Couldn't start recording: this is what an error looks like.");
        Pump(0.3);
        Save(main, "ui-failed.png");

        model.Shutdown();
        toolbar.Close();
        main.Close();
        output.WriteLine($"saved UI pictures to {SyntheticRecording.ArtifactDirectory}");
    }

    private static void Pump(double seconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) Pump(0.05);
    }

    private static void Save(Window window, string name)
    {
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(SyntheticRecording.ArtifactDirectory, name));
        encoder.Save(file);
    }
}
