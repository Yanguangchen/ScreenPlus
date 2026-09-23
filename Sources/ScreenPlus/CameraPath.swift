import Foundation

/// Camera + cursor state at one instant, in source pixels (top-left origin).
struct CameraState {
    var centerX: Double
    var centerY: Double
    var zoom: Double
    var cursorX: Double
    var cursorY: Double
}

/// A critically damped spring. Much nicer than easing curves because it handles targets
/// that change mid-animation (e.g. the cursor keeps moving while we zoom).
struct Spring {
    var value: Double
    var velocity: Double = 0
    var omega: Double  // stiffness as angular frequency; settle time ≈ 4.6 / omega seconds
    var damping: Double = 1  // 1 = critically damped (no overshoot)

    mutating func step(to target: Double, dt: Double) {
        let accel = omega * omega * (target - value) - 2 * damping * omega * velocity
        velocity += accel * dt  // semi-implicit Euler: stable at our small dt
        value += velocity * dt
    }
}

/// Precomputes the whole camera animation for a recording.
///
/// Because we render after recording, the path can "see the future": zooms start slightly
/// before the click that triggered them.
final class CameraPath {
    struct Segment {
        var start: Double
        var end: Double
        var focusX: Double
        var focusY: Double
    }

    private static let dt = 1.0 / 240.0
    /// Start zooming this long before the activity that triggered it.
    private static let lead = 0.35
    /// Zoom segments closer than this are merged, to avoid a quick out-and-in.
    private static let mergeGap = 0.8
    /// Mouse travel (in points) that counts as "activity".
    private static let movementThreshold = 12.0

    let width: Double
    let height: Double
    let segments: [Segment]
    private let states: [CameraState]
    private let clickTimes: [Double]

    init(session: RecordingSession, settings: RenderSettings, duration: Double) {
        let W = Double(session.width), H = Double(session.height)
        width = W
        height = H
        clickTimes = session.clicks.map(\.t).sorted()

        let cursor = session.cursor.sorted { $0.t < $1.t }
        func rawCursor(at t: Double) -> (x: Double, y: Double) {
            CameraPath.interpolate(cursor, at: t) ?? (W / 2, H / 2)
        }

        // 1. Activity timestamps: clicks, plus meaningful mouse movement.
        var activity: [(t: Double, x: Double, y: Double)] = session.clicks.map { ($0.t, $0.x, $0.y) }
        if !settings.clicksOnly, var anchor = cursor.first {
            let threshold = Self.movementThreshold * session.pixelsPerPoint
            for s in cursor.dropFirst() where hypot(s.x - anchor.x, s.y - anchor.y) > threshold {
                activity.append((s.t, s.x, s.y))
                anchor = s
            }
        }
        // Ignore the last second: that's usually the mouse heading to the Stop button.
        activity = activity.filter { $0.t >= 0 && $0.t < duration - 1.0 }.sorted { $0.t < $1.t }

        // 2. Merge activity into zoom segments.
        var segments: [Segment] = []
        for a in activity {
            let start = max(0, a.t - Self.lead)
            let end = a.t + settings.idleTimeout
            if let last = segments.last, start <= last.end + Self.mergeGap {
                segments[segments.count - 1].end = max(last.end, end)
            } else {
                segments.append(Segment(start: start, end: end, focusX: a.x, focusY: a.y))
            }
        }
        self.segments = segments

        // 3. Simulate springs over the whole timeline.
        let cursorOmega = 40 * pow(0.2, settings.cursorSmoothing)
        let start = rawCursor(at: 0)
        var zoomSpring = Spring(value: 0, omega: 6)  // animates log(zoom) so in/out feel symmetric
        var cx = Spring(value: W / 2, omega: 4.5)
        var cy = Spring(value: H / 2, omega: 4.5)
        var curX = Spring(value: start.x, omega: cursorOmega)
        var curY = Spring(value: start.y, omega: cursorOmega)

        var targetX = W / 2, targetY = H / 2
        var segIndex = 0
        var activeSegment: Int?
        let count = Int(duration / Self.dt) + 2
        var states: [CameraState] = []
        states.reserveCapacity(count)

        for i in 0..<count {
            let t = Double(i) * Self.dt
            let raw = rawCursor(at: t)
            curX.step(to: raw.x, dt: Self.dt)
            curY.step(to: raw.y, dt: Self.dt)

            while segIndex < segments.count, segments[segIndex].end < t { segIndex += 1 }
            let zoomed = segIndex < segments.count && segments[segIndex].start <= t
            let targetZoom = zoomed ? settings.zoomLevel : 1

            if zoomed {
                let viewW = W / targetZoom, viewH = H / targetZoom
                if activeSegment != segIndex {
                    activeSegment = segIndex
                    targetX = segments[segIndex].focusX
                    targetY = segments[segIndex].focusY
                }
                // Dead zone: only move the camera once the cursor nears the edge of the view.
                let halfX = viewW * 0.3, halfY = viewH * 0.3
                if curX.value > targetX + halfX { targetX = curX.value - halfX }
                if curX.value < targetX - halfX { targetX = curX.value + halfX }
                if curY.value > targetY + halfY { targetY = curY.value - halfY }
                if curY.value < targetY - halfY { targetY = curY.value + halfY }
                targetX = min(max(targetX, viewW / 2), W - viewW / 2)
                targetY = min(max(targetY, viewH / 2), H - viewH / 2)
            } else {
                activeSegment = nil
                targetX = W / 2
                targetY = H / 2
            }

            zoomSpring.step(to: log(targetZoom), dt: Self.dt)
            cx.step(to: targetX, dt: Self.dt)
            cy.step(to: targetY, dt: Self.dt)

            states.append(CameraState(centerX: cx.value, centerY: cy.value, zoom: exp(zoomSpring.value),
                                      cursorX: curX.value, cursorY: curY.value))
        }
        self.states = states
    }

    /// Camera state at time `t`, clamped so the view never leaves the recorded screen.
    func state(at t: Double) -> CameraState {
        let f = max(0, min(t / Self.dt, Double(states.count - 1)))
        let i = Int(f)
        let a = states[i], b = states[min(i + 1, states.count - 1)]
        let u = f - Double(i)
        func mix(_ x: Double, _ y: Double) -> Double { x + (y - x) * u }

        var s = CameraState(centerX: mix(a.centerX, b.centerX), centerY: mix(a.centerY, b.centerY),
                            zoom: max(1, mix(a.zoom, b.zoom)),
                            cursorX: mix(a.cursorX, b.cursorX), cursorY: mix(a.cursorY, b.cursorY))
        let viewW = width / s.zoom, viewH = height / s.zoom
        s.centerX = min(max(s.centerX, viewW / 2), width - viewW / 2)
        s.centerY = min(max(s.centerY, viewH / 2), height - viewH / 2)
        return s
    }

    /// 1 right at a click, decaying to 0 — used to "press" the cursor.
    func clickPulse(at t: Double) -> Double {
        let duration = 0.2
        guard let last = clickTimes.last(where: { $0 <= t }), t - last < duration else { return 0 }
        return 1 - (t - last) / duration
    }

    private static func interpolate(_ samples: [CursorSample], at t: Double) -> (x: Double, y: Double)? {
        guard let first = samples.first, let last = samples.last else { return nil }
        if t <= first.t { return (first.x, first.y) }
        if t >= last.t { return (last.x, last.y) }
        // Binary search for the first sample after t.
        var lo = 0, hi = samples.count - 1
        while lo < hi {
            let mid = (lo + hi) / 2
            if samples[mid].t <= t { lo = mid + 1 } else { hi = mid }
        }
        let a = samples[lo - 1], b = samples[lo]
        let u = b.t > a.t ? (t - a.t) / (b.t - a.t) : 1
        return (a.x + (b.x - a.x) * u, a.y + (b.y - a.y) * u)
    }
}
