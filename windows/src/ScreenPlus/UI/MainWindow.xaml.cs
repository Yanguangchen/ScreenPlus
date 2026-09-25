using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace ScreenPlus.UI;

internal partial class MainWindow : Window
{
    private readonly AppModel _model;
    private readonly DispatcherTimer _clock;

    public MainWindow(AppModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher);
        model.PropertyChanged += OnModelChanged;
        UpdatePhase();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.Phase)) UpdatePhase();
    }

    private void UpdatePhase()
    {
        var phase = _model.Phase;
        Editor.Visibility = phase == Phase.Editing ? Visibility.Visible : Visibility.Collapsed;
        Home.Visibility = phase == Phase.Editing ? Visibility.Collapsed : Visibility.Visible;
        IdlePanel.Visibility = phase == Phase.Idle ? Visibility.Visible : Visibility.Collapsed;
        BusyPanel.Visibility = phase is Phase.Starting or Phase.Processing ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = phase == Phase.Starting ? "Starting…" : "Preparing preview…";
        RecordingPanel.Visibility = phase == Phase.Recording ? Visibility.Visible : Visibility.Collapsed;
        FailedPanel.Visibility = phase == Phase.Failed ? Visibility.Visible : Visibility.Collapsed;
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

    private void Tick()
    {
        var seconds = Math.Max(0, (int)(DateTime.Now - _model.RecordingSince).TotalSeconds);
        RecordingTime.Text = $"{seconds / 60}:{seconds % 60:00}";
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.N when ctrl && !_model.IsBusy:
                _model.ShowRecorder();
                break;
            case Key.O when ctrl && !_model.IsBusy:
                _model.OpenRecording();
                break;
            case Key.E when ctrl && _model.Phase == Phase.Editing:
                _model.Export();
                break;
            case Key.R when ctrl && shift:
                if (_model.IsRecording) _model.StopRecording(); else _model.StartRecording();
                break;
            case Key.Space when _model.Phase == Phase.Editing && Keyboard.FocusedElement is not (ButtonBase or TextBoxBase):
                _model.TogglePlay();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window mid-recording would lose the recording; hide instead.
        if (_model.IsRecording)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void OnNewRecording(object sender, RoutedEventArgs e) => _model.ShowRecorder();
    private void OnOpen(object sender, RoutedEventArgs e) => _model.OpenRecording();
    private void OnStop(object sender, RoutedEventArgs e) => _model.StopRecording();
    private void OnBack(object sender, RoutedEventArgs e) => _model.Back();
}
