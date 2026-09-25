namespace ScreenPlus;

/// <summary>
/// Synthesized mouse-click and keyboard sounds, and the audio tracks built from them.
///
/// Everything is generated in code (no audio assets). Randomness is seeded so a recording
/// always sounds the same on every preview and export.
/// </summary>
public static class InputSounds
{
    public const int SampleRate = 48_000;

    public readonly record struct Hit(double Time, float[] Sound);

    // MARK: Events → hits

    public static List<Hit> ClickHits(RecordingSession session) =>
        session.Clicks.Select(c => new Hit(c.T, MouseClick)).ToList();

    public static List<Hit> KeyHits(RecordingSession session) =>
        (session.Keys ?? []).Select((key, index) => new Hit(key.T, key.Kind switch
        {
            KeyKind.Space => SpaceKey,
            KeyKind.Enter => EnterKey,
            KeyKind.Delete => DeleteKey,
            _ => RegularKeys[(index * 7 + 3) % RegularKeys.Length],
        })).ToList();

    /// <summary>
    /// Moves hits onto the output timeline of a sped-up or slowed-down video. The sounds themselves keep
    /// their length and pitch: a click still sounds like a click.
    /// </summary>
    public static List<Hit> AtSpeed(IEnumerable<Hit> hits, double speed) =>
        hits.Select(h => h with { Time = h.Time / speed }).ToList();

    // MARK: Sounds

    /// <summary>Sharp press "snap" followed by a softer release, like a real mouse button.</summary>
    public static readonly float[] MouseClick = MakeMouseClick();

    /// <summary>A handful of slightly different mechanical key sounds so typing doesn't sound robotic.</summary>
    public static readonly float[][] RegularKeys = Enumerable.Range(0, 6).Select(i =>
    {
        double v = i;
        return KeySound(seed: (uint)(100 + i), thock: 210 + v * 14, plate: 1_750 + v * 90,
                        release: 0.085 + v * 0.004, peak: 0.55f + (i % 3) * 0.05f);
    }).ToArray();

    public static readonly float[] SpaceKey = KeySound(seed: 7, thock: 140, plate: 1_300, release: 0.11, peak: 0.7f, length: 0.24);
    public static readonly float[] EnterKey = KeySound(seed: 8, thock: 170, plate: 1_500, release: 0.1, peak: 0.75f, length: 0.22);
    public static readonly float[] DeleteKey = KeySound(seed: 9, thock: 195, plate: 1_650, release: 0.09, peak: 0.6f);

    private static float[] MakeMouseClick()
    {
        var output = Buffer(seconds: 0.13);
        var rng = new Noise(0xC11C);
        Mix(output, 0, 1.0, t =>
        {
            var snap = rng.HighPassed() * Math.Exp(-t * 2_600) * 1.4;
            var ping = Math.Sin(2 * Math.PI * 5_200 * t) * Math.Exp(-t * 1_900) * 0.8;
            var tick = Math.Sin(2 * Math.PI * 2_700 * t) * Math.Exp(-t * 1_000) * 0.6;
            var body = Math.Sin(2 * Math.PI * 950 * t) * Math.Exp(-t * 380) * 0.35;
            return snap + ping + tick + body;
        });
        Mix(output, 0.075, 0.5, t =>
        {
            var snap = rng.HighPassed() * Math.Exp(-t * 3_200) * 1.2;
            var ping = Math.Sin(2 * Math.PI * 4_600 * t) * Math.Exp(-t * 2_200) * 0.7;
            var body = Math.Sin(2 * Math.PI * 1_100 * t) * Math.Exp(-t * 500) * 0.3;
            return snap + ping + body;
        });
        return Normalized(output, peak: 0.95f);
    }

    /// <summary>
    /// Key press: a click of the switch, the "thock" of bottoming out, and a ring from the plate;
    /// then a quieter key release.
    /// </summary>
    private static float[] KeySound(uint seed, double thock, double plate, double release, float peak, double length = 0.2)
    {
        var output = Buffer(seconds: length);
        var rng = new Noise(seed);
        Mix(output, 0, 1.0, t =>
        {
            var click = rng.HighPassed() * Math.Exp(-t * 1_500) * 0.9;
            var bottom = Math.Sin(2 * Math.PI * thock * t) * Math.Exp(-t * 55) * 0.9;
            var ring = Math.Sin(2 * Math.PI * plate * t) * Math.Exp(-t * 420) * 0.3;
            return click + bottom + ring;
        });
        Mix(output, release, 0.35, t =>
        {
            var click = rng.HighPassed() * Math.Exp(-t * 2_000) * 0.8;
            var bottom = Math.Sin(2 * Math.PI * thock * 1.25 * t) * Math.Exp(-t * 80) * 0.6;
            return click + bottom;
        });
        return Normalized(output, peak);
    }

    // MARK: Synthesis helpers

    private static float[] Buffer(double seconds) => new float[(int)(SampleRate * seconds)];

    private static void Mix(float[] output, double offset, double gain, Func<double, double> voice)
    {
        var start = (int)(offset * SampleRate);
        for (var i = start; i < output.Length; i++)
            output[i] += (float)(voice((double)(i - start) / SampleRate) * gain);
    }

    private static float[] Normalized(float[] samples, float peak)
    {
        var maxValue = samples.Length == 0 ? 1 : samples.Max(MathF.Abs);
        if (maxValue <= 0) return samples;
        return samples.Select(s => s / maxValue * peak).ToArray();
    }

    /// <summary>Deterministic white noise, with a first-order high-pass for a brighter "snap".</summary>
    private struct Noise(uint seed)
    {
        private uint _state = unchecked(seed * 2_654_435_761u) | 1;
        private double _previous = 0;

        public double Next()
        {
            _state = unchecked(_state * 1_664_525u + 1_013_904_223u);
            return (double)(_state >> 8) / (1 << 24) * 2 - 1;
        }

        public double HighPassed()
        {
            var x = Next();
            var result = (x - _previous) * 0.5;
            _previous = x;
            return result;
        }
    }
}

/// <summary>
/// A mono track of sound hits placed on a timeline, mixed on demand. Used both for streaming
/// preview audio and for the exported audio track.
/// </summary>
public sealed class SoundTrack
{
    private readonly (long Start, float[] Sound)[] _hits;
    private readonly int _longest;

    public SoundTrack(IEnumerable<InputSounds.Hit> hits, double duration)
    {
        _hits = hits.Where(h => h.Time >= 0 && h.Time < duration)
            .Select(h => (Start: (long)(h.Time * InputSounds.SampleRate), h.Sound))
            .OrderBy(h => h.Start)
            .ToArray();
        _longest = _hits.Length == 0 ? 0 : _hits.Max(h => h.Sound.Length);
    }

    public bool IsEmpty => _hits.Length == 0;

    /// <summary>Adds the track's samples for <c>[startSample, startSample + destination.Length)</c> into <paramref name="destination"/>.</summary>
    public void MixInto(long startSample, Span<float> destination)
    {
        if (_hits.Length == 0 || destination.IsEmpty) return;
        var end = startSample + destination.Length;
        // First hit that can still be sounding at startSample.
        long from = startSample - _longest;
        int lo = 0, hi = _hits.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_hits[mid].Start < from) lo = mid + 1; else hi = mid;
        }
        for (var i = lo; i < _hits.Length && _hits[i].Start < end; i++)
        {
            var (start, sound) = _hits[i];
            var a = Math.Max(startSample, start);
            var b = Math.Min(end, start + sound.Length);
            for (var s = a; s < b; s++)
                destination[(int)(s - startSample)] += sound[s - start];
        }
    }

    /// <summary>Clamps mixed samples so overlapping hits can't clip, and converts them to 16-bit PCM.</summary>
    public static void ToPcm16(ReadOnlySpan<float> samples, Span<short> destination)
    {
        for (var i = 0; i < samples.Length; i++)
            destination[i] = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
    }
}
