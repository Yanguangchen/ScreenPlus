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
        model.SpeedIndex = RenderSettings.SpeedChoices.ToList().IndexOf(2);
        Assert.Equal(2, model.Settings.Speed);
        Assert.Equal("Exported video: 0:02", model.OutputLengthText);
        Pump(1.0);
        Save(main, "ui-editor-settings.png");

        app.ThemeMode = ThemeMode.Dark;
        _dark = true;
        Pump(0.6);
        Save(main, "ui-editor-dark.png");

        model.Fail("Couldn't start recording: this is what an error looks like.");
        Pump(0.3);
        Save(main, "ui-failed.png");
        model.Back();
        app.ThemeMode = ThemeMode.Light;
        _dark = false;

        RecordThroughTheToolbar(model, main, toolbar);
        output.WriteLine("recording flow done; shutting down");

        model.Shutdown();
        output.WriteLine("model shut down");
        toolbar.Close();
        main.Close();
        output.WriteLine($"saved UI pictures to {SyntheticRecording.ArtifactDirectory}");
    }

    /// <summary>The whole flow a user goes through: Record, 3-2-1, record, Stop, edit.</summary>
    private void RecordThroughTheToolbar(AppModel model, MainWindow main, ToolbarWindow toolbar)
    {
        if (!Capture.ScreenRecorder.IsSupported)
        {
            output.WriteLine("screen capture unsupported here; skipping the recording flow");
            return;
        }
        using var tray = new TrayIcon(model);
        model.Tray = tray;
        var existing = Directory.Exists(AppModel.RecordingsRoot) ? Directory.GetDirectories(AppModel.RecordingsRoot).ToHashSet() : [];

        model.ShowRecorder();
        Pump(0.3);
        Assert.False(main.IsVisible);
        var record = (System.Windows.Controls.Button)toolbar.FindName("RecordButton");
        record.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump(0.5);
        Save(toolbar, "ui-toolbar-countdown.png");
        PumpUntil(() => model.Phase is Phase.Recording or Phase.Failed, TimeSpan.FromSeconds(15));
        if (model.Phase == Phase.Failed)
        {
            output.WriteLine($"recording couldn't start here: {model.FailureMessage}");
            return;
        }
        Pump(1.5);
        Save(toolbar, "ui-toolbar-recording.png");

        var stop = (System.Windows.Controls.Button)toolbar.FindName("StopButton");
        stop.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        PumpUntil(() => model.Phase is Phase.Editing or Phase.Failed, TimeSpan.FromSeconds(30));
        Assert.True(model.Phase == Phase.Editing, $"after stopping: {model.Phase} {model.FailureMessage}");
        Assert.True(main.IsVisible);
        var session = model.Session!;
        output.WriteLine($"recorded {session.Width}×{session.Height} at {session.PixelsPerPoint} px/pt, " +
                         $"{model.Duration:F2}s, {session.Cursor.Count} pointer samples");
        Assert.InRange(model.Duration, 1.0, 5.0);
        Assert.True(session.Cursor.Count > 0 || Capture.Screens.CursorPosition() == null);
        Assert.True(File.Exists(session.VideoPath));
        Pump(1.0);
        Save(main, "ui-editor-recording.png");
        output.WriteLine("saved the editor after recording; closing it");

        model.CloseEditor();  // releases the video file
        output.WriteLine("closed the editor");
        foreach (var folder in Directory.GetDirectories(AppModel.RecordingsRoot).Where(f => !existing.Contains(f)))
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
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

    private static bool _dark;

    private static void Save(Window window, string name)
    {
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(SyntheticRecording.ArtifactDirectory, name)))
            encoder.Save(file);

        // A small JPEG too, for a quick look. Windows 11 shows its Mica backdrop through the
        // window, which a snapshot can't capture; put a similar colour behind (except for the toolbar,
        // which is see-through around the pill).
        var scale = Math.Min(1.0, 720.0 / width);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var backdrop = window is ToolbarWindow ? Color.FromRgb(90, 110, 140) : _dark ? Color.FromRgb(32, 32, 32) : Color.FromRgb(243, 243, 243);
            dc.DrawRectangle(new SolidColorBrush(backdrop), null, new Rect(0, 0, width * scale, height * scale));
            dc.DrawImage(bitmap, new Rect(0, 0, width * scale, height * scale));
        }
        var small = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96, 96, PixelFormats.Pbgra32);
        small.Render(visual);
        var jpeg = new JpegBitmapEncoder { QualityLevel = 60 };
        jpeg.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(small, PixelFormats.Bgr24, null, 0)));
        using var thumb = File.Create(Path.Combine(SyntheticRecording.ThumbnailDirectory, Path.ChangeExtension(name, ".jpg")));
        jpeg.Save(thumb);
    }
}
