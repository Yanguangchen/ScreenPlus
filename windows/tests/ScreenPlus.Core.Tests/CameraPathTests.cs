namespace ScreenPlus.Tests;

public class CameraPathTests
{
    private readonly RecordingSession _session = TestData.Session();

    [Fact]
    public void MovementAndClicksMergeIntoOneZoom()
    {
        var path = new CameraPath(_session, new RenderSettings(), duration: 10);

        var segment = Assert.Single(path.Segments);
        // Starts a little before the mouse starts moving (at 2 s), ends 1.5 s after the click.
        Assert.InRange(segment.Start, 1.6, 1.75);
        Assert.Equal(3.2 + 1.5, segment.End, 3);
    }

    [Fact]
    public void ClicksOnlyIgnoresMovement()
    {
        var path = new CameraPath(_session, new RenderSettings { ClicksOnly = true }, duration: 10);

        var segment = Assert.Single(path.Segments);
        Assert.Equal(3.2 - 0.35, segment.Start, 3);
        Assert.Equal(_session.Width * 0.7, segment.FocusX);
    }

    [Fact]
    public void TheLastSecondNeverZooms()
    {
        // The mouse heads down to the Stop button during the final second.
        var path = new CameraPath(_session, new RenderSettings(), duration: 10);

        Assert.All(path.Segments, s => Assert.True(s.Start < 9));
    }

    [Fact]
    public void ZoomsInAndBackOut()
    {
        var path = new CameraPath(_session, new RenderSettings { ZoomLevel = 2.5 }, duration: 10);

        Assert.Equal(1, path.StateAt(0).Zoom, 3);
        Assert.Equal(2.5, path.StateAt(4.0).Zoom, 1);
        Assert.Equal(1, path.StateAt(8.5).Zoom, 2);
    }

    [Fact]
    public void TheViewNeverLeavesTheScreen()
    {
        var path = new CameraPath(_session, new RenderSettings { ZoomLevel = 4 }, duration: 10);

        for (var t = -1.0; t < 11; t += 0.01)
        {
            var s = path.StateAt(t);
            double viewW = _session.Width / s.Zoom, viewH = _session.Height / s.Zoom;
            Assert.InRange(s.Zoom, 1, 4.001);
            Assert.InRange(s.CenterX, viewW / 2 - 1e-6, _session.Width - viewW / 2 + 1e-6);
            Assert.InRange(s.CenterY, viewH / 2 - 1e-6, _session.Height - viewH / 2 + 1e-6);
        }
    }

    [Fact]
    public void CursorFollowsTheMouseSmoothly()
    {
        var path = new CameraPath(_session, new RenderSettings(), duration: 10);

        var s = path.StateAt(5);
        Assert.Equal(_session.Width * 0.7, s.CursorX, 0);
        Assert.Equal(_session.Height * 0.3, s.CursorY, 0);
        // Mid-move, the smoothed cursor lags behind the real one.
        Assert.True(path.StateAt(2.5).CursorX < _session.Width * 0.6);
    }

    [Fact]
    public void ClickPulseDecays()
    {
        var path = new CameraPath(_session, new RenderSettings(), duration: 10);

        Assert.Equal(0, path.ClickPulse(3.19));
        Assert.Equal(1, path.ClickPulse(3.2), 6);
        Assert.Equal(0.5, path.ClickPulse(3.3), 6);
        Assert.Equal(0, path.ClickPulse(3.5));
    }

    [Fact]
    public void HandlesEmptyRecordings()
    {
        var empty = new RecordingSession { Width = 1920, Height = 1080 };
        var path = new CameraPath(empty, new RenderSettings(), duration: 0);

        Assert.Empty(path.Segments);
        var s = path.StateAt(1);
        Assert.Equal(960, s.CenterX);
        Assert.Equal(1, s.Zoom);
    }
}
