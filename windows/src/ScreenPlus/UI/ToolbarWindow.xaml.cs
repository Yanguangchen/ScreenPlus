using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ScreenPlus.Capture;

namespace ScreenPlus.UI;

/// <summary>
/// The floating recording toolbar: always on top, never takes focus from the app you're recording,
/// and never appears in recordings or screenshots.
/// </summary>
internal partial class ToolbarWindow : Window
{
    private readonly AppModel _model;
    private readonly DispatcherTimer _clock;
    private DispatcherTimer? _countdown;
    private int _count;

    public ToolbarWindow(AppModel model)
    {
        InitializeComponent();
        _model = model;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(0.5), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher);
        SourceInitialized += (_, _) =>
        {
            NativeWindow.MakeNonActivatingToolWindow(this);
            NativeWindow.ExcludeFromCapture(this);
        };
        model.PropertyChanged += OnModelChanged;
        UpdateState();
    }

    /// <summary>Shows the toolbar at the bottom centre of the screen with the pointer, above the taskbar.</summary>
    public void ShowAtBottomOfScreen()
    {
        Show();
        Position();
        // Moving to a screen with different scaling resizes the window; place it again once that's done.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Position);
    }

    private void Position()
    {
        var screen = Screens.WithCursor();
        var scale = NativeWindow.Scale(this);
        var width = (int)(ActualWidth * scale);
        var height = (int)(ActualHeight * scale);
        NativeWindow.MoveTopmost(this, screen.WorkLeft + (screen.WorkWidth - width) / 2, screen.WorkBottom - height);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.Phase)) UpdateState();
    }

    private void UpdateState()
    {
        var phase = _model.Phase;
        var counting = _countdown != null;
        IdlePanel.Visibility = Show(phase is not (Phase.Starting or Phase.Recording or Phase.Processing) && !counting);
        CountdownPanel.Visibility = Show(counting && phase is not (Phase.Starting or Phase.Recording));
        RecordingPanel.Visibility = Show(phase == Phase.Recording);
        BusyText.Visibility = Show(phase is Phase.Starting or Phase.Processing);
        BusyText.Text = phase == Phase.Starting ? "Starting…" : "Finishing…";

        if (phase == Phase.Recording)
        {
            Tick();
            _clock.Start();
        }
        else
        {
            _clock.Stop();
        }
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void Tick()
    {
        var elapsed = DateTime.Now - _model.RecordingSince;
        var seconds = Math.Max(0, (int)elapsed.TotalSeconds);
        ElapsedText.Text = $"{seconds / 60}:{seconds % 60:00}";
        RecordingDot.Opacity = (int)(elapsed.TotalSeconds * 2) % 2 == 0 ? 1 : 0.35;
    }

    private void OnRecord(object sender, RoutedEventArgs e)
    {
        _count = 3;
        CountdownText.Text = "3";
        _countdown = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) =>
        {
            if (--_count > 0)
            {
                CountdownText.Text = _count.ToString();
                return;
            }
            StopCountdown();
            _model.StartRecording();
        }, Dispatcher);
        _countdown.Start();
        UpdateState();
    }

    private void StopCountdown()
    {
        _countdown?.Stop();
        _countdown = null;
        UpdateState();
    }

    private void OnCancelCountdown(object sender, RoutedEventArgs e) => StopCountdown();
    private void OnClose(object sender, RoutedEventArgs e) => _model.HideRecorder();
    private void OnOpenEditor(object sender, RoutedEventArgs e) => _model.HideRecorder();
    private void OnStop(object sender, RoutedEventArgs e) => _model.StopRecording();
    private void OnDiscard(object sender, RoutedEventArgs e) => _model.DiscardRecording();

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
