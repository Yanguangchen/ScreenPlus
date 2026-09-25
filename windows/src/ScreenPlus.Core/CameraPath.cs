namespace ScreenPlus;

/// <summary>Camera + cursor state at one instant, in source pixels (top-left origin).</summary>
public record struct CameraState(double CenterX, double CenterY, double Zoom, double CursorX, double CursorY);

/// <summary>
/// A critically damped spring. Much nicer than easing curves because it handles targets
/// that change mid-animation (e.g. the cursor keeps moving while we zoom).
/// </summary>
public struct Spring(double value, double omega)
{
    public double Value = value;
    public double Velocity = 0;
    /// <summary>Stiffness as angular frequency; settle time ≈ 4.6 / omega seconds.</summary>
    public double Omega = omega;
    /// <summary>1 = critically damped (no overshoot).</summary>
    public double Damping = 1;

    public void Step(double target, double dt)
    {
        var accel = Omega * Omega * (target - Value) - 2 * Damping * Omega * Velocity;
        Velocity += accel * dt;  // semi-implicit Euler: stable at our small dt
        Value += Velocity * dt;
    }
}

/// <summary>
/// Precomputes the whole camera animation for a recording.
///
/// Because we render after recording, the path can "see the future": zooms start slightly
/// before the click that triggered them.
/// </summary>
public sealed class CameraPath
{
    public readonly record struct Segment(double Start, double End, double FocusX, double FocusY);

    private const double Dt = 1.0 / 240.0;
    /// <summary>Start zooming this long before the activity that triggered it.</summary>
    private const double Lead = 0.35;
    /// <summary>Zoom segments closer than this are merged, to avoid a quick out-and-in.</summary>
    private const double MergeGap = 0.8;
    /// <summary>Mouse travel (in points) that counts as "activity".</summary>
    private const double MovementThreshold = 12.0;

    public double Width { get; }
    public double Height { get; }
    public IReadOnlyList<Segment> Segments { get; }

    private readonly CameraState[] _states;
    private readonly double[] _clickTimes;

    public CameraPath(RecordingSession session, RenderSettings settings, double duration)
    {
        double W = session.Width, H = session.Height;
        Width = W;
        Height = H;
        duration = double.IsFinite(duration) ? Math.Max(0, duration) : 0;
        _clickTimes = session.Clicks.Select(c => c.T).Order().ToArray();

        var cursor = session.Cursor.OrderBy(s => s.T).ToArray();
        (double x, double y) RawCursor(double t) => Interpolate(cursor, t) ?? (W / 2, H / 2);

        // 1. Activity timestamps: clicks, plus meaningful mouse movement.
        var activity = session.Clicks.Select(c => (t: c.T, x: c.X, y: c.Y)).ToList();
        if (!settings.ClicksOnly && cursor.Length > 0)
        {
            var anchor = cursor[0];
            var threshold = MovementThreshold * session.PixelsPerPoint;
            foreach (var s in cursor.Skip(1))
            {
                if (Hypot(s.X - anchor.X, s.Y - anchor.Y) > threshold)
                {
                    activity.Add((s.T, s.X, s.Y));
                    anchor = s;
                }
            }
        }
        // Ignore the last second: that's usually the mouse heading to the Stop button.
        activity = activity.Where(a => a.t >= 0 && a.t < duration - 1.0).OrderBy(a => a.t).ToList();

        // 2. Merge activity into zoom segments.
        var segments = new List<Segment>();
        foreach (var a in activity)
        {
            var start = Math.Max(0, a.t - Lead);
            var end = a.t + settings.IdleTimeout;
            if (segments.Count > 0 && start <= segments[^1].End + MergeGap)
                segments[^1] = segments[^1] with { End = Math.Max(segments[^1].End, end) };
            else
                segments.Add(new Segment(start, end, a.x, a.y));
        }
        Segments = segments;

        // 3. Simulate springs over the whole timeline.
        var cursorOmega = 40 * Math.Pow(0.2, settings.CursorSmoothing);
        var first = RawCursor(0);
        var zoomSpring = new Spring(0, 6);  // animates log(zoom) so in/out feel symmetric
        var cx = new Spring(W / 2, 4.5);
        var cy = new Spring(H / 2, 4.5);
        var curX = new Spring(first.x, cursorOmega);
        var curY = new Spring(first.y, cursorOmega);

        double targetX = W / 2, targetY = H / 2;
        var segIndex = 0;
        int? activeSegment = null;
        var count = (int)(duration / Dt) + 2;
        _states = new CameraState[count];

        for (var i = 0; i < count; i++)
        {
            var t = i * Dt;
            var raw = RawCursor(t);
            curX.Step(raw.x, Dt);
            curY.Step(raw.y, Dt);

            while (segIndex < segments.Count && segments[segIndex].End < t) segIndex++;
            var zoomed = segIndex < segments.Count && segments[segIndex].Start <= t;
            var targetZoom = zoomed ? settings.ZoomLevel : 1;

            if (zoomed)
            {
                double viewW = W / targetZoom, viewH = H / targetZoom;
                if (activeSegment != segIndex)
                {
                    activeSegment = segIndex;
                    targetX = segments[segIndex].FocusX;
                    targetY = segments[segIndex].FocusY;
                }
                // Dead zone: only move the camera once the cursor nears the edge of the view.
                double halfX = viewW * 0.3, halfY = viewH * 0.3;
                if (curX.Value > targetX + halfX) targetX = curX.Value - halfX;
                if (curX.Value < targetX - halfX) targetX = curX.Value + halfX;
                if (curY.Value > targetY + halfY) targetY = curY.Value - halfY;
                if (curY.Value < targetY - halfY) targetY = curY.Value + halfY;
                targetX = Math.Min(Math.Max(targetX, viewW / 2), W - viewW / 2);
                targetY = Math.Min(Math.Max(targetY, viewH / 2), H - viewH / 2);
            }
            else
            {
                activeSegment = null;
                targetX = W / 2;
                targetY = H / 2;
            }

            zoomSpring.Step(Math.Log(targetZoom), Dt);
            cx.Step(targetX, Dt);
            cy.Step(targetY, Dt);

            _states[i] = new CameraState(cx.Value, cy.Value, Math.Exp(zoomSpring.Value), curX.Value, curY.Value);
        }
    }

    /// <summary>Camera state at time <paramref name="t"/>, clamped so the view never leaves the recorded screen.</summary>
    public CameraState StateAt(double t)
    {
        var f = double.IsFinite(t) ? Math.Max(0, Math.Min(t / Dt, _states.Length - 1)) : 0;
        var i = (int)f;
        var a = _states[i];
        var b = _states[Math.Min(i + 1, _states.Length - 1)];
        var u = f - i;
        double Mix(double x, double y) => x + (y - x) * u;

        var s = new CameraState(Mix(a.CenterX, b.CenterX), Mix(a.CenterY, b.CenterY),
                                Math.Max(1, Mix(a.Zoom, b.Zoom)),
                                Mix(a.CursorX, b.CursorX), Mix(a.CursorY, b.CursorY));
        double viewW = Width / s.Zoom, viewH = Height / s.Zoom;
        s.CenterX = Math.Min(Math.Max(s.CenterX, viewW / 2), Width - viewW / 2);
        s.CenterY = Math.Min(Math.Max(s.CenterY, viewH / 2), Height - viewH / 2);
        return s;
    }

    /// <summary>1 right at a click, decaying to 0 — used to "press" the cursor.</summary>
    public double ClickPulse(double t)
    {
        const double duration = 0.2;
        // Last click at or before t.
        int lo = 0, hi = _clickTimes.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_clickTimes[mid] <= t) lo = mid + 1; else hi = mid;
        }
        if (lo == 0) return 0;
        var last = _clickTimes[lo - 1];
        return t - last < duration ? 1 - (t - last) / duration : 0;
    }

    private static (double x, double y)? Interpolate(CursorSample[] samples, double t)
    {
        if (samples.Length == 0) return null;
        CursorSample first = samples[0], last = samples[^1];
        if (t <= first.T) return (first.X, first.Y);
        if (t >= last.T) return (last.X, last.Y);
        // Binary search for the first sample after t.
        int lo = 0, hi = samples.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (samples[mid].T <= t) lo = mid + 1; else hi = mid;
        }
        CursorSample a = samples[lo - 1], b = samples[lo];
        var u = b.T > a.T ? (t - a.T) / (b.T - a.T) : 1;
        return (a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u);
    }

    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);
}
