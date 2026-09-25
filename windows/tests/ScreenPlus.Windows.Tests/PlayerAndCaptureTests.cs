using System.IO;
using ScreenPlus.Capture;
using ScreenPlus.Media;
using ScreenPlus.Rendering;
using Xunit.Abstractions;

namespace ScreenPlus.WindowsTests;

public unsafe class PlayerAndCaptureTests(ITestOutputHelper output)
{
    [Fact]
    public void PreviewShowsFramesPlaysAndSeeks()
    {
        var folder = SyntheticRecording.Create();
        var session = RecordingSession.Load(Path.Combine(folder, "events.json"));
        using var cursor = CursorArt.CreateDefault();
        using var player = new PreviewPlayer(session);
        using var frames = new SemaphoreSlim(0);
        player.FrameReady += () => frames.Release();

        Assert.InRange(player.Duration, 3.9, 4.1);
        player.SetComposer(new FrameComposer(session, new RenderSettings(), cursor, player.Duration, 1280, 6) { ParallelBlur = true });
        Assert.True(frames.Wait(TimeSpan.FromSeconds(10)), "the preview never showed a frame");

        player.Seek(1.6);
        Assert.True(frames.Wait(TimeSpan.FromSeconds(10)), "the preview didn't redraw after seeking");
        var copied = false;
        player.CopyFrame((pixels, width, height, stride) =>
        {
            copied = true;
            Assert.Equal(1280, width);
            SyntheticRecording.SavePng(pixels, width, height, stride, "preview-1.6s.png");
        });
        Assert.True(copied);

        // Seeking backwards goes through the decoder's keyframes.
        player.Seek(0.2);
        Assert.True(frames.Wait(TimeSpan.FromSeconds(10)), "the preview didn't redraw after seeking back");

        player.Seek(1.0);
        player.Play();
        Thread.Sleep(700);
        var position = player.Position;
        player.Pause();
        output.WriteLine($"played from 1.0 s for 0.7 s, now at {position:F3} s");
        Assert.InRange(position, 1.3, 1.9);

        // Settings changes swap the composer while running.
        player.SetComposer(new FrameComposer(session, new RenderSettings { ZoomLevel = 3, Padding = 0 }, cursor, player.Duration, 1280, 6));
        Assert.True(frames.Wait(TimeSpan.FromSeconds(10)), "the preview didn't redraw with new settings");
    }

    [Fact]
    public void RecordsTheScreen()
    {
        output.WriteLine($"Windows.Graphics.Capture supported: {ScreenRecorder.IsSupported}");
        if (!ScreenRecorder.IsSupported) return;

        var path = Path.Combine(SyntheticRecording.ArtifactDirectory, "capture.mp4");
        using var recorder = new ScreenRecorder();
        try
        {
            recorder.Start(path);
        }
        catch (RecorderException e)
        {
            // CI machines may have no desktop to capture; that's not a failure of the recorder.
            output.WriteLine($"Screen capture isn't available here: {e.Message} {e.InnerException}");
            return;
        }
        output.WriteLine($"recording {recorder.Screen} as {recorder.Width}×{recorder.Height}");
        Thread.Sleep(2000);
        var result = recorder.Stop();

        using var reader = new Mp4Reader(result.Path);
        output.WriteLine($"captured {reader.Width}×{reader.Height}, {reader.Duration:F2}s, first frame at {result.FirstFrameHostTime:F3}");
        Assert.Equal((result.Width, result.Height), (reader.Width, reader.Height));
        Assert.InRange(reader.Duration, 1.5, 3);
        var frame = reader.Read();
        Assert.NotNull(frame);
        SyntheticRecording.SavePng((nint)frame!.Data, frame.Width, frame.Height, frame.Stride, "capture-first-frame.png");
        frame.Release();
        // Frame times and input times share the performance-counter clock.
        Assert.InRange(InputTracker.Now() - result.FirstFrameHostTime, 0, 10);
    }

    [Fact]
    public void TracksThePointer()
    {
        var tracker = new InputTracker();
        tracker.Start();
        Thread.Sleep(300);
        var events = tracker.Stop();

        output.WriteLine($"{events.Count} pointer samples in 0.3 s");
        Assert.All(events, e => Assert.Equal(InputTracker.Kind.Move, e.Kind));
        if (Screens.CursorPosition() != null) Assert.InRange(events.Count, 15, 45);  // ~120 Hz
        Assert.Equal(events.OrderBy(e => e.Time), events);
    }

    [Fact]
    public void DescribesTheScreen()
    {
        var screen = Screens.WithCursor();
        output.WriteLine(screen.ToString());
        Assert.True(screen.Width > 0 && screen.Height > 0);
        Assert.InRange(screen.Scale, 1, 4);
    }
}
