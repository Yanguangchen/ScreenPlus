using ScreenPlus.Media;

namespace ScreenPlus.Rendering;

/// <summary>
/// Decodes a recording on its own thread, a few frames ahead, so the live preview can compose one frame
/// while the next is being decoded. Like <see cref="SourceFrames"/>, it holds the latest frame at or
/// before the time asked for; times are relative to the first frame.
/// </summary>
internal sealed class DecodeAhead : IDisposable
{
    private const int Capacity = 4;

    private readonly Mp4Reader _reader;
    private readonly Thread _thread;
    private readonly Lock _lock = new();
    private readonly Queue<VideoFrame> _queue = new();
    private readonly AutoResetEvent _decoderWake = new(false);
    private readonly AutoResetEvent _frameArrived = new(false);
    private VideoFrame? _current;
    private double _newest = double.NegativeInfinity;
    private double? _seek;
    private int _generation;
    private bool _ended, _disposed;
    private Exception? _error;

    /// <summary>Time stamp of the first frame, in the file's own time.</summary>
    public double BaseTime { get; }
    public double Duration { get; }

    public DecodeAhead(string path)
    {
        _reader = new Mp4Reader(path);
        try
        {
            using var first = _reader.ReadSample();
            BaseTime = first?.Time ?? 0;
            Duration = Math.Max(0, _reader.Duration - BaseTime);
            if (first == null) _ended = true;
            else Enqueue(first.ToFrame());
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
        _thread = new Thread(Decode) { IsBackground = true, Name = "ScreenPlus decoder" };
        _thread.Start();
    }

    /// <summary>
    /// The frame showing at <paramref name="t"/>, or null if the video has no frames. With
    /// <paramref name="wait"/> it waits until that exact frame is decoded; without (during playback) it
    /// returns the newest frame available rather than stall. Owned by this object; valid until the next call.
    /// </summary>
    public VideoFrame? At(double t, bool wait)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            lock (_lock)
            {
                if (_error != null) throw new MediaException($"The video couldn't be decoded: {_error.Message}");
                var advanced = false;
                while (_queue.Count > 0 && (_current == null || _queue.Peek().Time <= t + 0.001))
                {
                    _current?.Release();
                    _current = _queue.Dequeue();
                    advanced = true;
                }
                if (advanced) _decoderWake.Set();
                // Right when the next decoded frame is already later than t, or there are no more.
                if (_current != null && (_queue.Count > 0 || _ended || !wait)) return _current;
                if (_current == null && _ended) return null;
                if (Environment.TickCount64 > deadline) return _current;
            }
            _frameArrived.WaitOne(50);
        }
    }

    /// <summary>Time of the newest decoded frame, to tell whether jumping to a time needs a seek.</summary>
    public double NewestTime
    {
        get { lock (_lock) return _newest; }
    }

    /// <summary>Whether every frame has been decoded (the rest of the video is its last frame).</summary>
    public bool ReachedEnd
    {
        get { lock (_lock) return _ended; }
    }

    /// <summary>Starts decoding again from the keyframe at or before <paramref name="t"/>.</summary>
    public void Seek(double t)
    {
        lock (_lock)
        {
            _seek = Math.Max(0, t);
            _generation++;
            _current?.Release();
            _current = null;
            while (_queue.Count > 0) _queue.Dequeue().Release();
            _newest = double.NegativeInfinity;
            _ended = false;
        }
        _decoderWake.Set();
    }

    private void Decode()
    {
        while (true)
        {
            int generation;
            double? seek;
            bool idle;
            lock (_lock)
            {
                if (_disposed) return;
                generation = _generation;
                seek = _seek;
                _seek = null;
                idle = seek == null && (_queue.Count >= Capacity || _ended);
            }
            if (idle)
            {
                _decoderWake.WaitOne();
                continue;
            }

            try
            {
                if (seek is { } s) _reader.Seek(s + BaseTime);
                using var sample = _reader.ReadSample();
                var frame = sample?.ToFrame();
                lock (_lock)
                {
                    if (generation != _generation)  // seeked meanwhile; this frame is from before
                    {
                        frame?.Release();
                        continue;
                    }
                    if (frame == null) _ended = true;
                    else Enqueue(frame);
                }
                _frameArrived.Set();
            }
            catch (Exception e)
            {
                lock (_lock)
                {
                    _error = e;
                    _ended = true;
                }
                _frameArrived.Set();
                return;
            }
        }
    }

    private void Enqueue(VideoFrame frame)
    {
        frame.Time -= BaseTime;
        _queue.Enqueue(frame);
        _newest = frame.Time;
    }

    public void Dispose()
    {
        lock (_lock) _disposed = true;
        _decoderWake.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            System.Diagnostics.Trace.WriteLine("ScreenPlus: the decoder thread didn't stop; leaving it behind");
            return;  // it may still be using the reader and frames
        }
        lock (_lock)
        {
            _current?.Release();
            _current = null;
            while (_queue.Count > 0) _queue.Dequeue().Release();
        }
        _reader.Dispose();
        _decoderWake.Dispose();
        _frameArrived.Dispose();
    }
}
