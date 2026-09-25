using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenPlus.Capture;
using ScreenPlus.Rendering;
using ScreenPlus.UI;

namespace ScreenPlus;

internal enum Phase { Idle, Starting, Recording, Processing, Editing, Failed }

/// <summary>The app's state and actions: recording, the editor with its live preview, and export.</summary>
internal sealed class AppModel : INotifyPropertyChanged
{
    /// <summary>Preview renders smaller and with fewer blur samples so it plays back in real time.</summary>
    private const int PreviewWidth = 1280;
    private const int PreviewBlurSamples = 6;

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly InputTracker _tracker = new();
    private readonly CursorArt _cursor = CursorArt.CreateDefault();
    private ScreenRecorder? _recorder;
    private string? _folder;
    private CancellationTokenSource? _previewDebounce;
    private CancellationTokenSource? _export;
    private int _previewVersion;
    private int _framePending;

    public MainWindow? MainWindow { get; set; }
    public ToolbarWindow? Toolbar { get; set; }
    public TrayIcon? Tray { get; set; }

    // MARK: State

    public Phase Phase
    {
        get;
        private set
        {
            field = value;
            Changed();
            Changed(nameof(IsRecording));
            Changed(nameof(IsBusy));
        }
    }

    public string FailureMessage { get; private set => Set(ref field, value); } = "";
    public DateTime RecordingSince { get; private set => Set(ref field, value); }

    public bool IsRecording => Phase == Phase.Recording;
    public bool IsBusy => Phase is Phase.Starting or Phase.Recording or Phase.Processing || ExportProgress != null;

    public RenderSettings Settings
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Changed();
            foreach (var name in SettingNames) Changed(name);
            Player?.SetSounds(value.ClickSounds, value.KeyboardSounds);
            SchedulePreviewUpdate();
        }
    } = new();

    // Editor state
    public RecordingSession? Session { get; private set => Set(ref field, value); }
    public double Duration { get; private set => Set(ref field, value); }
    public IReadOnlyList<CameraPath.Segment> Segments { get; private set => Set(ref field, value); } = [];
    public PreviewPlayer? Player { get; private set => Set(ref field, value); }
    public WriteableBitmap? PreviewImage { get; private set => Set(ref field, value); }
    public double Position { get; private set => Set(ref field, value); }
    public bool IsPlaying { get; private set => Set(ref field, value); }

    public double? ExportProgress
    {
        get;
        private set
        {
            field = value;
            Changed();
            Changed(nameof(IsExporting));
            Changed(nameof(IsBusy));
        }
    }

    public bool IsExporting => ExportProgress != null;
    public string? ExportedPath { get; private set => Set(ref field, value); }

    public string SessionSummary => Session is { } s
        ? $"{s.Clicks.Count} clicks · {s.Keys?.Count ?? 0} key presses in this recording"
        : "";

    // MARK: Settings for binding

    private static readonly string[] SettingNames =
    [
        nameof(ZoomLevel), nameof(IdleTimeout), nameof(ClicksOnly), nameof(CursorScale), nameof(CursorSmoothing),
        nameof(Padding), nameof(MotionBlur), nameof(ClickSounds), nameof(KeyboardSounds), nameof(BackgroundBlur),
        nameof(BackgroundImagePath), nameof(HasBackgroundImage), nameof(GradientIndex),
    ];

    public double ZoomLevel { get => Settings.ZoomLevel; set => Settings = Settings with { ZoomLevel = value }; }
    public double IdleTimeout { get => Settings.IdleTimeout; set => Settings = Settings with { IdleTimeout = value }; }
    public bool ClicksOnly { get => Settings.ClicksOnly; set => Settings = Settings with { ClicksOnly = value }; }
    public double CursorScale { get => Settings.CursorScale; set => Settings = Settings with { CursorScale = value }; }
    public double CursorSmoothing { get => Settings.CursorSmoothing; set => Settings = Settings with { CursorSmoothing = value }; }
    public double Padding { get => Settings.Padding; set => Settings = Settings with { Padding = value }; }
    public bool MotionBlur { get => Settings.MotionBlur; set => Settings = Settings with { MotionBlur = value }; }
    public bool ClickSounds { get => Settings.ClickSounds; set => Settings = Settings with { ClickSounds = value }; }
    public bool KeyboardSounds { get => Settings.KeyboardSounds; set => Settings = Settings with { KeyboardSounds = value }; }
    public double BackgroundBlur { get => Settings.BackgroundBlur; set => Settings = Settings with { BackgroundBlur = value }; }
    public string? BackgroundImagePath => (Settings.Background as ImageBackground)?.Path;
    public bool HasBackgroundImage => Settings.Background is ImageBackground;
    public int GradientIndex => Settings.Background is GradientBackground g ? g.Index : -1;

    public void SelectGradient(int index) => Settings = Settings with { Background = new GradientBackground(index) };

    // MARK: Windows

    /// <summary>Hides the main window and shows the floating recording toolbar.</summary>
    public void ShowRecorder()
    {
        if (IsBusy) return;
        Player?.Pause();
        MainWindow?.Hide();
        Toolbar?.ShowAtBottomOfScreen();
    }

    /// <summary>Hides the toolbar and brings back the main window (home or editor).</summary>
    public void HideRecorder()
    {
        if (IsRecording) return;
        Toolbar?.Hide();
        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (MainWindow == null) return;
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    // MARK: Recording

    public async void StartRecording()
    {
        if (IsBusy) return;
        if (!ScreenRecorder.IsSupported)
        {
            Fail("Screen recording needs Windows 10 version 2004 or later.");
            HideRecorder();
            return;
        }

        Player?.Pause();
        // Everything happens from the toolbar while recording (it's the one window that never gets captured).
        MainWindow?.Hide();
        if (Toolbar?.IsVisible != true) Toolbar?.ShowAtBottomOfScreen();
        var folder = Path.Combine(RecordingsRoot, $"Recording {Timestamp()}");
        _folder = folder;
        Phase = Phase.Starting;
        var recorder = new ScreenRecorder();
        try
        {
            Directory.CreateDirectory(folder);
            await Task.Run(() => recorder.Start(Path.Combine(folder, "raw.mp4")));
            _recorder = recorder;
            _tracker.Start();
            RecordingSince = DateTime.Now;
            Phase = Phase.Recording;
            Tray?.Show();
        }
        catch (Exception e)
        {
            recorder.Dispose();
            TryDeleteFolder(folder);
            Fail(e.Message);
            HideRecorder();
        }
    }

    public async void StopRecording()
    {
        if (!IsRecording || _folder is not { } folder || _recorder is not { } recorder) return;
        var events = _tracker.Stop();
        _recorder = null;
        Phase = Phase.Processing;
        Tray?.Hide();
        Toolbar?.Hide();
        ShowMainWindow();

        try
        {
            var result = await Task.Run(recorder.Stop);
            var session = MakeSession(result, events, recorder.Screen);
            session.Save(Path.Combine(folder, "events.json"));
            await OpenEditor(session, folder);
        }
        catch (Exception e)
        {
            Fail(e.Message);
        }
        finally
        {
            recorder.Dispose();
        }
    }

    /// <summary>Stops recording and throws the footage away; the toolbar stays up for another take.</summary>
    public async void DiscardRecording()
    {
        if (!IsRecording || _folder is not { } folder || _recorder is not { } recorder) return;
        _tracker.Stop();
        _recorder = null;
        Phase = Phase.Processing;
        Tray?.Hide();
        await Task.Run(() =>
        {
            try { recorder.Stop(); } catch (Exception) { }
            recorder.Dispose();
        });
        TryDeleteFolder(folder);
        Phase = Session == null ? Phase.Idle : Phase.Editing;
    }

    /// <summary>Converts raw input events (performance-counter time, physical screen pixels) into video time and video pixels.</summary>
    private static RecordingSession MakeSession(ScreenRecorder.Result result, List<InputTracker.RawEvent> events, ScreenInfo screen)
    {
        var scale = (double)result.Width / screen.Width;
        var session = new RecordingSession
        {
            VideoPath = result.Path,
            Width = result.Width,
            Height = result.Height,
            PixelsPerPoint = screen.Scale * scale,
            Keys = [],
        };
        foreach (var e in events)
        {
            var t = e.Time - result.FirstFrameHostTime;
            double x = (e.X - screen.Left) * scale, y = (e.Y - screen.Top) * scale;
            switch (e.Kind)
            {
                case InputTracker.Kind.Move: session.Cursor.Add(new CursorSample(t, x, y)); break;
                case InputTracker.Kind.Click: session.Clicks.Add(new ClickEvent(t, x, y)); break;
                case InputTracker.Kind.Key: session.Keys.Add(new KeyEvent(t, e.Key)); break;
            }
        }
        return session;
    }

    // MARK: Editor / live preview

    /// <summary>Lets you pick an earlier recording (its events.json or raw video) and open it in the editor.</summary>
    public async void OpenRecording()
    {
        if (IsBusy) return;
        var dialog = new OpenFileDialog
        {
            Title = "Open a ScreenPlus recording",
            Filter = "ScreenPlus recordings|events.json;raw.mp4;raw.mov|All files|*.*",
            InitialDirectory = Directory.Exists(RecordingsRoot) ? RecordingsRoot : null,
        };
        if (dialog.ShowDialog(MainWindow?.IsVisible == true ? MainWindow : null) != true) return;

        var events = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, "events.json");
        try
        {
            var session = RecordingSession.Load(events);
            Toolbar?.Hide();
            ShowMainWindow();
            await OpenEditor(session, Path.GetDirectoryName(events)!);
        }
        catch (Exception e)
        {
            Fail($"Could not open the recording: {e.Message}");
        }
    }

    public async Task OpenEditor(RecordingSession session, string folder)
    {
        ClosePlayer();
        Phase = Phase.Processing;
        var player = await Task.Run(() => new PreviewPlayer(session));
        Session = session;
        _folder = folder;
        Duration = player.Duration;
        ExportedPath = null;
        Position = 0;
        Changed(nameof(SessionSummary));
        player.FrameReady += OnFrameReady;
        player.PlayingChanged += playing => _dispatcher.BeginInvoke(() => IsPlaying = playing);
        player.SetSounds(Settings.ClickSounds, Settings.KeyboardSounds);
        Player = player;
        await UpdatePreview();
        Phase = Phase.Editing;
        player.Play();
    }

    private void ClosePlayer()
    {
        if (Player is not { } player) return;
        Player = null;
        player.FrameReady -= OnFrameReady;
        player.Dispose();
        PreviewImage = null;
        IsPlaying = false;
    }

    private void SchedulePreviewUpdate()
    {
        if (Session == null) return;
        _previewDebounce?.Cancel();
        var debounce = _previewDebounce = new CancellationTokenSource();
        _ = Task.Delay(60, debounce.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _ = UpdatePreview();  // coalesce slider drags
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Applies the current settings to the preview.</summary>
    private async Task UpdatePreview()
    {
        if (Session is not { } session || Player is not { } player) return;
        var settings = Settings;
        var duration = Duration;
        var version = ++_previewVersion;
        try
        {
            var composer = await Task.Run(() => new FrameComposer(session, settings, _cursor, duration, PreviewWidth, PreviewBlurSamples)
            {
                ParallelBlur = true,
            });
            if (version != _previewVersion || Player != player)
            {
                composer.Dispose();
                return;
            }
            Segments = composer.Path.Segments;
            player.SetComposer(composer);
        }
        catch (Exception e)
        {
            Trace.WriteLine($"ScreenPlus: preview update failed: {e}");
        }
    }

    /// <summary>Copies the preview's newest frame into the image on screen (at most once per UI frame).</summary>
    private void OnFrameReady()
    {
        if (Interlocked.Exchange(ref _framePending, 1) == 1) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            Interlocked.Exchange(ref _framePending, 0);
            if (Player is not { } player) return;
            player.CopyFrame((pixels, width, height, stride) =>
            {
                if (PreviewImage is not { } image || image.PixelWidth != width || image.PixelHeight != height)
                    PreviewImage = image = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                image.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride * height, stride);
            });
            Position = player.Position;
        });
    }

    public void TogglePlay() => Player?.TogglePlay();

    public void Seek(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, Duration);
        Position = seconds;
        Player?.Seek(seconds);
    }

    // MARK: Export

    public async void Export()
    {
        if (Session is not { } session || ExportProgress != null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export video",
            Filter = "MP4 video|*.mp4",
            FileName = $"ScreenPlus {Timestamp()}.mp4",
            InitialDirectory = _folder,
            AddExtension = true,
            DefaultExt = ".mp4",
        };
        if (dialog.ShowDialog(MainWindow) != true) return;
        var path = dialog.FileName;

        Player?.Pause();
        ExportProgress = 0;
        ExportedPath = null;
        var cancel = _export = new CancellationTokenSource();
        var renderer = new Renderer(session, Settings, _cursor);
        try
        {
            await Task.Run(() => renderer.Render(path, progress => _dispatcher.BeginInvoke(() =>
            {
                if (ExportProgress != null && !cancel.IsCancellationRequested) ExportProgress = progress;
            }), cancel.Token));
            ExportProgress = null;
            ExportedPath = path;
        }
        catch (OperationCanceledException)
        {
            ExportProgress = null;
        }
        catch (Exception e)
        {
            ExportProgress = null;
            Fail(e.Message);
        }
    }

    public void CancelExport()
    {
        _export?.Cancel();
        ExportProgress = null;
    }

    // MARK: Misc

    public void Fail(string message)
    {
        FailureMessage = message;
        Phase = Phase.Failed;
    }

    public void Back() => Phase = Session == null ? Phase.Idle : Phase.Editing;

    public void CloseEditor()
    {
        ClosePlayer();
        Session = null;
        Phase = Phase.Idle;
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Trace.WriteLine($"ScreenPlus: couldn't open Explorer: {e.Message}");
        }
    }

    /// <summary>Lets the user pick an image file as the background.</summary>
    public void ChooseBackgroundImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a background image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(MainWindow) != true) return;
        using (var image = FrameComposer.LoadImage(dialog.FileName))
        {
            if (image == null)
            {
                Fail($"Couldn't open {Path.GetFileName(dialog.FileName)} as an image.");
                return;
            }
        }
        Settings = Settings with { Background = new ImageBackground(dialog.FileName) };
    }

    public void RemoveBackgroundImage() => Settings = Settings with { Background = new GradientBackground(0) };

    public void Shutdown()
    {
        _export?.Cancel();
        if (_recorder != null)
        {
            _tracker.Stop();
            _recorder.Dispose();
            _recorder = null;
        }
        ClosePlayer();
        _cursor.Dispose();
    }

    public static string RecordingsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenPlus");

    private static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd 'at' HH.mm.ss");

    private static void TryDeleteFolder(string folder)
    {
        try { Directory.Delete(folder, recursive: true); } catch (Exception) { }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed(name);
    }

    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
