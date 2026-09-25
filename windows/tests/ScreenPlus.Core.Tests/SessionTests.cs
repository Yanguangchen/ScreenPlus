namespace ScreenPlus.Tests;

public class SessionTests
{
    // What the macOS app writes (Swift's JSONEncoder escapes slashes and pads colons).
    private const string MacJson = """
        {
          "videoURL" : "file:\/\/\/Users\/me\/Movies\/ScreenPlus\/Recording%202026-09-23%20at%2010.00.00\/raw.mov",
          "width" : 2940,
          "height" : 1912,
          "pixelsPerPoint" : 2,
          "cursor" : [
            { "t" : 0.0041, "x" : 1470, "y" : 956.5 },
            { "t" : 0.0124, "x" : 1471.25, "y" : 957 }
          ],
          "clicks" : [ { "y" : 200, "x" : 100, "t" : 1.5 } ],
          "keys" : [ { "t" : 2, "kind" : "space" }, { "kind" : "regular", "t" : 2.1 }, { "t" : 2.2, "kind" : "delete" } ]
        }
        """;

    [Fact]
    public void ReadsRecordingsFromTheMacApp()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "events.json");
        File.WriteAllText(path, MacJson);

        var session = RecordingSession.Load(path);

        Assert.Equal(2940, session.Width);
        Assert.Equal(1912, session.Height);
        Assert.Equal(2, session.PixelsPerPoint);
        Assert.Equal(2, session.Cursor.Count);
        Assert.Equal(new CursorSample(0.0124, 1471.25, 957), session.Cursor[1]);
        Assert.Equal(new ClickEvent(1.5, 100, 200), Assert.Single(session.Clicks));
        Assert.Equal([KeyKind.Space, KeyKind.Regular, KeyKind.Delete], session.Keys!.Select(k => k.Kind));
        Assert.EndsWith("raw.mov", session.VideoPath);
    }

    [Fact]
    public void OlderRecordingsHaveNoKeys()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "events.json");
        File.WriteAllText(path, """{"videoURL":"raw.mov","width":100,"height":50,"pixelsPerPoint":1,"cursor":[],"clicks":[]}""");

        Assert.Null(RecordingSession.Load(path).Keys);
    }

    [Fact]
    public void RoundTripsAndPrefersTheVideoNextToTheEventsFile()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        var session = TestData.Session();
        session.VideoPath = Path.Combine(Path.GetTempPath(), "somewhere-else", "raw.mp4");
        var events = Path.Combine(folder, "events.json");
        session.Save(events);
        File.WriteAllBytes(Path.Combine(folder, "raw.mp4"), []);

        var loaded = RecordingSession.Load(events);

        Assert.Equal(Path.Combine(folder, "raw.mp4"), loaded.VideoPath);
        Assert.Equal(session.Cursor, loaded.Cursor);
        Assert.Equal(session.Clicks, loaded.Clicks);
        Assert.Equal(session.Keys, loaded.Keys);
        var json = File.ReadAllText(events);
        Assert.Contains("\"videoURL\"", json);
        Assert.Contains("\"kind\": \"space\"", json);
    }

    [Fact]
    public void VideoUrlIsAFileUrl()
    {
        var session = new RecordingSession { VideoPath = Path.Combine(Path.GetTempPath(), "Recording 1", "raw.mp4") };

        Assert.StartsWith("file:///", session.VideoUrl);
        Assert.Contains("Recording%201", session.VideoUrl);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "Recording 1", "raw.mp4"), session.VideoPath);
    }
}
