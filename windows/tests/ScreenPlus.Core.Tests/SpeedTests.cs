using System.Runtime.InteropServices;

namespace ScreenPlus.Tests;

public unsafe class SpeedTests
{
    [Theory]
    [InlineData(0.1, "10× slower")]
    [InlineData(0.125, "8× slower")]
    [InlineData(1.0 / 6, "6× slower")]
    [InlineData(0.5, "2× slower")]
    [InlineData(1, "Normal speed")]
    [InlineData(2, "2× faster")]
    [InlineData(10, "10× faster")]
    public void DescribesSpeeds(double speed, string text) => Assert.Equal(text, RenderSettings.DescribeSpeed(speed));

    [Fact]
    public void OffersTenTimesEitherWay()
    {
        Assert.Equal([0.1, 0.125, 1.0 / 6, 0.25, 0.5, 1, 2, 4, 6, 8, 10], RenderSettings.SpeedChoices);
        Assert.Equal(10, new RenderSettings { Speed = 50 }.Speed);
        Assert.Equal(0.1, new RenderSettings { Speed = 0.01 }.Speed);
        Assert.Equal(1, new RenderSettings().Speed);
    }

    [Fact]
    public void SoundsMoveToTheOutputTimeline()
    {
        var hits = InputSounds.ClickHits(TestData.Session());  // one click at 3.2 s

        Assert.Equal(0.8, Assert.Single(InputSounds.AtSpeed(hits, 4)).Time, 6);
        Assert.Equal(6.4, Assert.Single(InputSounds.AtSpeed(hits, 0.5)).Time, 6);
        Assert.Same(InputSounds.MouseClick, InputSounds.AtSpeed(hits, 4)[0].Sound);  // same sound, not stretched
    }

    [Fact]
    public void FasterVideoGetsMoreMotionBlur()
    {
        var session = TestData.Session();
        using var cursor = CursorArt.CreateDefault();
        using var scratch = new ComposerScratch();
        byte[] Render(double speed)
        {
            using var composer = new FrameComposer(session, new RenderSettings { Speed = speed, ClicksOnly = true }, cursor, 10, 960);
            var source = TestData.Frame(session.Width, session.Height);
            var pixels = new byte[composer.Width * composer.Height * 4];
            fixed (byte* p = pixels) composer.Render(source, 2.5, (nint)p, composer.Width * 4, scratch);
            source.Release();
            return pixels;
        }

        // At 2.5 s the cursor is moving: sped up, its streak covers more of the frame.
        var normal = Render(1);
        var fast = Render(8);
        var changed = normal.Where((b, i) => Math.Abs(b - fast[i]) > 8).Count();
        Assert.True(changed > 500, $"only {changed} bytes differ");
    }
}
