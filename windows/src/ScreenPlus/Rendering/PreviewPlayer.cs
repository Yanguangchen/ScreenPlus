using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenPlus.Media;
using static TerraFX.Interop.Windows.Windows;

namespace ScreenPlus.Rendering;

/// <summary>
/// The editor's live preview: decodes the recording, composes each frame with the current settings and
/// plays the click and keyboard sounds, all on a background thread. Nothing is written to disk.
/// </summary>
internal sealed unsafe class PreviewPlayer : IDisposable
{
    /// <summary>A new frame is ready; call <see cref="CopyFrame"/>. Raised on the render thread.</summary>
    public event Action? FrameReady;
    /// <summary>Playback started or stopped (including at the end). Raised on any thread.</summary>
    public event Action<bool>? PlayingChanged;

    public double Duration { get; private set; }

    private readonly RecordingSession _session;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _ready = new();
    private readonly Lock _lock = new();
    private readonly Lock _frameLock = new();
    private Exception? _openError;

    // Owned by the render thread.
    private Mp4Reader? _reader;
    private SourceFrames? _source;
    private SoundTrack? _clicks, _keys;
    private AudioOutput? _audio;
    private long _lastIndex = -1;
    private VideoFrame? _lastFrame;
    private FrameComposer? _lastComposer;

    // Shared state, under _lock.
    private FrameComposer? _composer;
    private readonly List<FrameComposer> _retired = [];
    private bool _playing, _dirty = true, _disposed;
    private double _position, _playStart, _lastClock, _lastAudioPosition;
    private long _playStartTicks, _lastAudioTicks;
    private bool _timerRaised;

    // Audio thread.
    private long _audioSample;
    private readonly float[] _mix = new float[AudioOutput.SampleRate];
    private volatile bool _clickSounds = true, _keySounds = true;

    // Finished frames, under _frameLock.
    private nint _front, _back;
    private int _frontWidth, _frontHeight, _backWidth, _backHeight;

    public PreviewPlayer(RecordingSession session)
    {
        _session = session;
        _thread = new Thread(Run) { IsBackground = true, Name = "ScreenPlus preview" };
        _thread.Start();
        _ready.Wait();
        if (_openError != null)
        {
            _thread.Join();
            throw _openError;
        }
    }

    public bool IsPlaying
    {
        get { lock (_lock) return _playing; }
    }

    /// <summary>Current playback position in seconds.</summary>
    public double Position
    {
        get { lock (_lock) return _playing ? ClockLocked() : _position; }
    }

    /// <summary>Uses a new composer (after a settings change). The player takes ownership.</summary>
    public void SetComposer(FrameComposer composer)
    {
        lock (_lock)
        {
            if (_composer != null) _retired.Add(_composer);
            _composer = composer;
            _dirty = true;
        }
        _wake.Set();
    }

    public void SetSounds(bool clicks, bool keys)
    {
        _clickSounds = clicks;
        _keySounds = keys;
    }

    public void Play()
    {
        lock (_lock)
        {
            if (_playing || _disposed) return;
            if (_position >= Duration - 0.05) _position = 0;
            _playing = true;
            StartClockLocked(_position);
            if (!_timerRaised)
            {
                timeBeginPeriod(1);  // smooth 60 fps frame pacing
                _timerRaised = true;
            }
        }
        _wake.Set();
        PlayingChanged?.Invoke(true);
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!_playing) return;
            _position = ClockLocked();
            _playing = false;
            _audio?.Stop();
            _dirty = true;
            LowerTimerLocked();
        }
        _wake.Set();
        PlayingChanged?.Invoke(false);
    }

    public void TogglePlay()
    {
        if (IsPlaying) Pause(); else Play();
    }

    public void Seek(double seconds)
    {
        lock (_lock)
        {
            seconds = Math.Clamp(seconds, 0, Duration);
            if (_playing) StartClockLocked(seconds);
            else _position = seconds;
            _dirty = true;
        }
        _wake.Set();
    }

    /// <summary>Hands the newest frame (BGRA) to <paramref name="copy"/> as (pixels, width, height, stride).</summary>
    public void CopyFrame(Action<nint, int, int, int> copy)
    {
        lock (_frameLock)
        {
            if (_front != 0) copy(_front, _frontWidth, _frontHeight, _frontWidth * 4);
        }
    }

    // MARK: Clock

    private void StartClockLocked(double position)
    {
        _playStart = position;
        _playStartTicks = Stopwatch.GetTimestamp();
        _lastClock = position;
        _lastAudioPosition = 0;
        _lastAudioTicks = _playStartTicks;
        if (_audio != null)
        {
            _audio.Stop();
            _audioSample = (long)(position * AudioOutput.SampleRate);
            _audio.Start(FillAudio);
        }
    }

    /// <summary>
    /// With sound, the clock is what the speakers have actually played (smoothed between the driver's
    /// position updates), so clicks land exactly on screen. Without, it's the wall clock.
    /// </summary>
    private double ClockLocked()
    {
        var now = Stopwatch.GetTimestamp();
        double clock;
        if (_audio == null)
        {
            clock = _playStart + Stopwatch.GetElapsedTime(_playStartTicks, now).TotalSeconds;
        }
        else
        {
            var played = _audio.PlayedSeconds;
            if (played != _lastAudioPosition)
            {
                _lastAudioPosition = played;
                _lastAudioTicks = now;
            }
            var since = played > 0 ? Math.Min(0.05, Stopwatch.GetElapsedTime(_lastAudioTicks, now).TotalSeconds) : 0;
            clock = _playStart + played + since;
        }
        _lastClock = Math.Max(_lastClock, clock);
        return Math.Min(_lastClock, Duration);
    }

    private void LowerTimerLocked()
    {
        if (!_timerRaised) return;
        timeEndPeriod(1);
        _timerRaised = false;
    }

    private void FillAudio(Span<short> output)
    {
        var mix = _mix.AsSpan(0, output.Length);
        mix.Clear();
        if (_clickSounds) _clicks?.MixInto(_audioSample, mix);
        if (_keySounds) _keys?.MixInto(_audioSample, mix);
        SoundTrack.ToPcm16(mix, output);
        _audioSample += output.Length;
    }

    // MARK: Render thread

    private void Run()
    {
        try
        {
            // Media Foundation objects stay on the thread that uses them.
            _reader = new Mp4Reader(_session.VideoPath);
            _source = new SourceFrames(_reader);
            Duration = Math.Max(0, _reader.Duration - _source.BaseTime);
            _clicks = new SoundTrack(InputSounds.ClickHits(_session), Duration);
            _keys = new SoundTrack(InputSounds.KeyHits(_session), Duration);
            _audio = AudioOutput.TryCreate();
        }
        catch (Exception e)
        {
            _openError = e;
            _ready.Set();
            return;
        }
        _ready.Set();

        while (true)
        {
            double t;
            bool playing, dirty, ended = false;
            FrameComposer? composer;
            lock (_lock)
            {
                foreach (var old in _retired) old.Dispose();
                _retired.Clear();
                if (_disposed) break;
                playing = _playing;
                t = playing ? ClockLocked() : _position;
                if (playing && t >= Duration)
                {
                    t = _position = Duration;
                    _playing = playing = false;
                    _audio?.Stop();
                    LowerTimerLocked();
                    ended = true;
                }
                composer = _composer;
                dirty = _dirty;
                _dirty = false;
            }
            if (ended) PlayingChanged?.Invoke(false);

            var fps = 60.0;
            var index = (long)Math.Floor(t * fps + 1e-6);
            if (composer != null && (dirty || index != _lastIndex || composer != _lastComposer))
            {
                try
                {
                    RenderFrame(composer, playing ? index / fps : t);
                    _lastIndex = index;
                    _lastComposer = composer;
                }
                catch (Exception e)
                {
                    Trace.WriteLine($"ScreenPlus: preview frame failed: {e}");
                }
            }

            if (playing)
            {
                var untilNext = (index + 1) / fps - t;
                _wake.WaitOne(TimeSpan.FromSeconds(Math.Clamp(untilNext, 0.001, 0.02)));
            }
            else
            {
                _wake.WaitOne();
            }
        }

        _source.Dispose();
        _reader.Dispose();
        _audio?.Dispose();
    }

    private void RenderFrame(FrameComposer composer, double t)
    {
        var frame = FrameAt(t);
        if (frame == null) return;
        _lastFrame = frame;

        var (width, height) = (composer.Width, composer.Height);
        if (_back == 0 || _backWidth != width || _backHeight != height)
        {
            if (_back != 0) NativeMemory.AlignedFree((void*)_back);
            _back = (nint)NativeMemory.AlignedAlloc((nuint)(width * height * 4), 64);
            (_backWidth, _backHeight) = (width, height);
        }
        composer.Render(frame, t, _back, width * 4, _scratch);

        lock (_frameLock)
        {
            (_front, _back) = (_back, _front);
            (_frontWidth, _frontHeight, _backWidth, _backHeight) = (_backWidth, _backHeight, _frontWidth, _frontHeight);
        }
        FrameReady?.Invoke();
    }

    private readonly ComposerScratch _scratch = new();

    /// <summary>The source frame at <paramref name="t"/>; seeks instead of decoding everything when jumping around.</summary>
    private VideoFrame? FrameAt(double t)
    {
        var current = _lastFrame;
        var jumpBack = current != null && t < current.Time - 0.001;
        var jumpAhead = current != null && _source!.NextTime is { } next && t > next + 1.5;
        if (jumpBack || jumpAhead)
        {
            _source!.Seek(t);
            _lastFrame = null;
        }
        return _source!.At(t);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            LowerTimerLocked();
        }
        _wake.Set();
        _thread.Join();
        lock (_lock)
        {
            _composer?.Dispose();
            _composer = null;
            foreach (var old in _retired) old.Dispose();
            _retired.Clear();
        }
        _scratch.Dispose();
        lock (_frameLock)
        {
            if (_front != 0) NativeMemory.AlignedFree((void*)_front);
            if (_back != 0) NativeMemory.AlignedFree((void*)_back);
            _front = _back = 0;
        }
        _wake.Dispose();
        _ready.Dispose();
    }
}
