using System.IO;
using ScreenPlus.Media;
using ScreenPlus.Rendering;
using TerraFX.Interop.Windows;
using Xunit.Abstractions;
using static TerraFX.Interop.Windows.MF;
using static TerraFX.Interop.Windows.Windows;

namespace ScreenPlus.WindowsTests;

public unsafe class MediaTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WritesAndReadsBackVariableFrameRateVideo(bool preferHardware)
    {
        var folder = SyntheticRecording.Create(preferHardware);
        using var reader = new Mp4Reader(Path.Combine(folder, "raw.mp4"));

        Assert.Equal((SyntheticRecording.Width, SyntheticRecording.Height), (reader.Width, reader.Height));
        Assert.InRange(reader.Duration, SyntheticRecording.Duration - 0.1, SyntheticRecording.Duration + 0.1);

        var times = new List<double>();
        VideoFrame? first = null;
        while (reader.Read() is { } frame)
        {
            times.Add(frame.Time);
            if (first == null) first = frame; else frame.Release();
        }
        var expected = SyntheticRecording.FrameTimes().Count() + 1;
        output.WriteLine($"hardware preferred: {preferHardware}; {times.Count} frames (wrote {expected}); " +
                         $"first {times[0]:F3}s, last {times[^1]:F3}s");
        Assert.Equal(expected, times.Count);
        Assert.Equal(times.Order(), times);
        // The still second is kept as a gap, not filled in.
        Assert.Contains(times, t => t > 2.9 && t < 3.05);
        Assert.DoesNotContain(times, t => t > 2.1 && t < 2.9);

        // The red square top-left and the window survive encoding.
        var red = first!.Data + 60 * first.Stride + 60 * 4;
        Assert.True(red[2] > 200 && red[1] < 60 && red[0] < 60, $"expected red, got {red[2]},{red[1]},{red[0]}");
        var window = first.Data + 200 * first.Stride + 1000 * 4;
        Assert.True(window[0] > 230 && window[1] > 230 && window[2] > 230);
        first.Release();
    }

    [Fact]
    public void ExportsWithZoomCursorAndSound()
    {
        var folder = SyntheticRecording.Create();
        var session = RecordingSession.Load(Path.Combine(folder, "events.json"));
        var outputPath = Path.Combine(SyntheticRecording.ArtifactDirectory, "export.mp4");
        using var cursor = CursorArt.CreateDefault();
        var settings = new RenderSettings { OutputWidth = 1280 };
        var progress = new List<double>();

        new Renderer(session, settings, cursor).Render(outputPath, progress.Add, CancellationToken.None);

        Assert.Equal(1.0, progress[^1]);
        using var reader = new Mp4Reader(outputPath);
        output.WriteLine($"export: {reader.Width}×{reader.Height}, {reader.Duration:F3}s, {new FileInfo(outputPath).Length / 1024} KB");
        Assert.Equal(1280, reader.Width);
        Assert.Equal(776, reader.Height);  // 1280 wide with 5% padding around a 16:9 screen
        Assert.InRange(reader.Duration, SyntheticRecording.Duration - 0.1, SyntheticRecording.Duration + 0.1);
        var frames = 0;
        while (reader.Read() is { } frame)
        {
            if (frames == 100) SyntheticRecording.SavePng((nint)frame.Data, frame.Width, frame.Height, frame.Stride, "export-frame-100.png");
            frame.Release();
            frames++;
        }
        Assert.InRange(frames, 238, 241);  // constant 60 fps
        Assert.True(HasAudioTrack(outputPath), "expected an AAC track with the click and key sounds");
    }

    [Fact]
    public void ExportWithoutSoundsHasNoAudioTrack()
    {
        var folder = SyntheticRecording.Create();
        var session = RecordingSession.Load(Path.Combine(folder, "events.json"));
        var outputPath = Path.Combine(folder, "silent.mp4");
        using var cursor = CursorArt.CreateDefault();
        var settings = new RenderSettings { OutputWidth = 960, ClickSounds = false, KeyboardSounds = false, MotionBlur = false };

        new Renderer(session, settings, cursor).Render(outputPath, null, CancellationToken.None);

        Assert.False(HasAudioTrack(outputPath));
        Assert.Empty(Directory.GetFiles(folder, "*.partial.mp4"));
    }

    [Fact]
    public void CancelledExportLeavesNothingBehind()
    {
        var folder = SyntheticRecording.Create();
        var session = RecordingSession.Load(Path.Combine(folder, "events.json"));
        var outputPath = Path.Combine(folder, "cancelled.mp4");
        using var cursor = CursorArt.CreateDefault();
        using var cancel = new CancellationTokenSource();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new Renderer(session, new RenderSettings(), cursor).Render(outputPath, p => { if (p > 0.2) cancel.Cancel(); }, cancel.Token));

        Assert.False(File.Exists(outputPath));
        Assert.DoesNotContain(Directory.GetFiles(folder, "*.mp4"), f => f.Contains("partial"));
    }

    [Fact]
    public void SeeksToEarlierFrames()
    {
        var folder = SyntheticRecording.Create();
        using var reader = new Mp4Reader(Path.Combine(folder, "raw.mp4"));
        using var source = new SourceFrames(reader);

        var late = source.At(3.5)!;
        Assert.InRange(late.Time, 3.48, 3.502);
        source.Seek(1.0);
        var early = source.At(1.0)!;
        Assert.InRange(early.Time, 0.96, 1.002);
        // During the still second the last frame before it stays up.
        Assert.InRange(source.At(2.5)!.Time, 1.9, 2.0);
    }

    private static bool HasAudioTrack(string path)
    {
        MediaFoundation.Start();
        IMFSourceReader* reader;
        fixed (char* url = path)
            MediaFoundation.Check(MFCreateSourceReaderFromURL(url, null, &reader), "Opening the export");
        try
        {
            IMFMediaType* type;
            var hr = reader->GetNativeMediaType(unchecked((uint)MF_SOURCE_READER_FIRST_AUDIO_STREAM), 0, &type);
            if (FAILED(hr)) return false;
            type->Release();
            return true;
        }
        finally
        {
            reader->Release();
        }
    }
}
