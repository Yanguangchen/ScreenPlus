using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenPlus.UI;

/// <summary>Editor: live preview on the left, settings and export on the right.</summary>
internal partial class EditorView : UserControl
{
    private AppModel? _model;
    private readonly List<Border> _swatches = [];
    private Border? _imageSwatch;

    public EditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_model != null) _model.PropertyChanged -= OnModelChanged;
            _model = e.NewValue as AppModel;
            if (_model == null) return;
            _model.PropertyChanged += OnModelChanged;
            BuildSwatches();
            Refresh();
        };
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppModel.Position):
            case nameof(AppModel.Duration):
                UpdateTime();
                break;
            case nameof(AppModel.ExportProgress):
                ExportText.Text = $"Exporting… {(int)((_model?.ExportProgress ?? 0) * 100)}%";
                break;
            case nameof(AppModel.Settings):
            case nameof(AppModel.Segments):
                Refresh();
                break;
        }
    }

    private void Refresh()
    {
        if (_model == null) return;
        Timeline.Segments = _model.Segments;
        Timeline.ZoomLevel = _model.ZoomLevel;
        var count = _model.Segments.Count;
        ZoomCount.Text = $"{count} auto-zoom{(count == 1 ? "" : "s")}";
        ImageName.Text = _model.BackgroundImagePath is { } path ? Path.GetFileName(path) : "";
        UpdateSwatches();
        UpdateTime();
    }

    private void UpdateTime()
    {
        if (_model == null) return;
        Timeline.Duration = _model.Duration;
        Timeline.Position = _model.Position;
        TimeText.Text = $"{Format(_model.Position)} / {Format(Math.Round(_model.Duration))}";
    }

    private static string Format(double seconds)
    {
        var s = Math.Max(0, (int)seconds);
        return $"{s / 60}:{s % 60:00}";
    }

    // MARK: Background swatches

    private void BuildSwatches()
    {
        if (_swatches.Count > 0) return;
        for (var i = 0; i < GradientPreset.All.Count; i++)
        {
            var preset = GradientPreset.All[i];
            var fill = new LinearGradientBrush(ToColor(preset.From), ToColor(preset.To), new Point(0, 0), new Point(1, 1));
            var index = i;
            var swatch = MakeSwatch(new Border { Background = fill }, preset.Name, () => _model?.SelectGradient(index));
            _swatches.Add(swatch);
        }
        _imageSwatch = MakeSwatch(new TextBlock
        {
            Text = "",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
        }, "Use your own image…", () => _model?.ChooseBackgroundImage());
    }

    private Border MakeSwatch(UIElement content, string tooltip, Action action)
    {
        var swatch = new Border
        {
            Height = 38,
            Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0x10, 0x80, 0x80, 0x80)),
            Child = content,
            ToolTip = tooltip,
            Cursor = Cursors.Hand,
            ClipToBounds = true,
        };
        if (content is Border inner) inner.CornerRadius = new CornerRadius(6);
        swatch.MouseLeftButtonUp += (_, _) => action();
        Swatches.Children.Add(swatch);
        return swatch;
    }

    private void UpdateSwatches()
    {
        if (_model == null) return;
        var accent = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? SystemColors.HighlightBrush;
        var neutral = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        for (var i = 0; i < _swatches.Count; i++)
        {
            var selected = _model.GradientIndex == i;
            _swatches[i].BorderBrush = selected ? accent : neutral;
            _swatches[i].BorderThickness = new Thickness(selected ? 2.5 : 1);
        }
        if (_imageSwatch == null) return;
        var hasImage = _model.HasBackgroundImage;
        _imageSwatch.BorderBrush = hasImage ? accent : neutral;
        _imageSwatch.BorderThickness = new Thickness(hasImage ? 2.5 : 1);
        _imageSwatch.ToolTip = hasImage ? "Change image…" : "Use your own image…";
        _imageSwatch.Child = hasImage && Thumbnail(_model.BackgroundImagePath!) is { } thumbnail
            ? new Image { Source = thumbnail, Stretch = Stretch.UniformToFill }
            : new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.7,
            };
    }

    private static BitmapImage? Thumbnail(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelHeight = 76;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;  // e.g. a format WPF can't decode; the swatch shows the icon instead
        }
    }

    private static Color ToColor(Rgb rgb) =>
        Color.FromRgb((byte)Math.Round(rgb.R * 255), (byte)Math.Round(rgb.G * 255), (byte)Math.Round(rgb.B * 255));

    // MARK: Preview

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) =>
        Preview.Clip = new RectangleGeometry(new Rect(e.NewSize), 10, 10);

    private void OnPreviewClicked(object sender, MouseButtonEventArgs e) => _model?.TogglePlay();
    private void OnPlayPause(object sender, RoutedEventArgs e) => _model?.TogglePlay();
    private void OnSeek(object? sender, double seconds) => _model?.Seek(seconds);

    // MARK: Actions

    private void OnRemoveImage(object sender, RoutedEventArgs e) => _model?.RemoveBackgroundImage();
    private void OnExport(object sender, RoutedEventArgs e) => _model?.Export();
    private void OnCancelExport(object sender, RoutedEventArgs e) => _model?.CancelExport();
    private void OnNewRecording(object sender, RoutedEventArgs e) => _model?.ShowRecorder();
    private void OnOpen(object sender, RoutedEventArgs e) => _model?.OpenRecording();

    private void OnShowExported(object sender, RoutedEventArgs e)
    {
        if (_model?.ExportedPath is { } path) AppModel.RevealInExplorer(path);
    }
}
