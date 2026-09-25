using System.IO;
using System.Runtime.InteropServices;
using ScreenPlus.Media;

namespace ScreenPlus.Rendering;

/// <summary>Exports a recording to an .mp4 at full quality, frame by frame, with click and keyboard sounds.</summary>
internal sealed unsafe class Renderer(RecordingSession session, RenderSettings settings, CursorArt cursor)
{
    private const int Bitrate = 20_000_000;

    /// <summary>Tallest output the software H.264 encoder takes; portrait screens are scaled to fit.</summary>
    private const int MaxOutputHeight = 2160;

    public void Render(string outputPath, Action<double>? progress, CancellationToken cancellation)
    {
        try
        {
            RenderOnce(outputPath, progress, cancellation, preferHardware: true);
        }
        catch (MediaException) when (!cancellation.IsCancellationRequested && _usedHardware)
        {
            // Some GPU encoders fail partway through; Windows' software encoder always works.
            RenderOnce(outputPath, progress, cancellation, preferHardware: false);
        }
    }

    private bool _usedHardware;

    private void RenderOnce(string outputPath, Action<double>? progress, CancellationToken cancellation, bool preferHardware)
    {
        _usedHardware = false;
        using var reader = new Mp4Reader(session.VideoPath);
        var source = new SourceFrames(reader);
        try
        {
            var duration = reader.Duration > 0 ? reader.Duration - source.BaseTime : 0;
            if (duration <= 0) throw new MediaException("The recording is empty.");

            using var composer = CreateComposer(duration);
            var fps = settings.Fps;
            var hits = new List<InputSounds.Hit>();
            if (settings.ClickSounds) hits.AddRange(InputSounds.ClickHits(session));
            if (settings.KeyboardSounds) hits.AddRange(InputSounds.KeyHits(session));
            // Sounds go where their moment lands in the sped-up (or slowed-down) video.
            var outputDuration = duration / settings.Speed;
            var sounds = new SoundTrack(InputSounds.AtSpeed(hits, settings.Speed), outputDuration);

            // Write next to the destination and move into place at the end, so a cancelled or failed
            // export never leaves a broken file behind.
            var folder = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
            var temporary = Path.Combine(folder, $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.partial.mp4");
            try
            {
                using (var writer = Mp4Writer.Create(temporary, new Mp4Writer.Options(
                           composer.Width, composer.Height, fps, Bitrate, KeyframeInterval: fps * 2, Audio: !sounds.IsEmpty),
                           preferHardware))
                {
                    _usedHardware = writer.UsesHardware;
                    RenderFrames(composer, source, writer, sounds, outputDuration, settings.Speed, fps, progress, cancellation);
                    writer.Finish();
                }
                File.Move(temporary, outputPath, overwrite: true);
            }
            finally
            {
                Mp4Writer.TryDelete(temporary);
            }
            progress?.Invoke(1);
        }
        finally
        {
            source.Dispose();
        }
    }

    private FrameComposer CreateComposer(double duration)
    {
        var composer = new FrameComposer(session, settings, cursor, duration, settings.OutputWidth);
        if (composer.Height <= MaxOutputHeight) return composer;
        var width = (int)((long)composer.Width * MaxOutputHeight / composer.Height);
        composer.Dispose();
        return new FrameComposer(session, settings, cursor, duration, width);
    }

    /// <summary>Renders <paramref name="duration"/> seconds of output; output frame n shows recording time n / fps × speed.</summary>
    private static void RenderFrames(FrameComposer composer, SourceFrames source, Mp4Writer writer, SoundTrack sounds,
                                     double duration, double speed, int fps, Action<double>? progress,
                                     CancellationToken cancellation)
    {
        var frameCount = Math.Max(1, (int)(duration * fps));
        // Frames are independent once decoded, so render a batch at a time on all cores.
        var workers = Math.Clamp(Environment.ProcessorCount, 1, 8);
        var stride = composer.Width * 4;
        var scratch = Enumerable.Range(0, workers).Select(_ => new ComposerScratch()).ToArray();
        var bgra = Enumerable.Range(0, workers).Select(_ => (nint)NativeMemory.AlignedAlloc((nuint)(stride * composer.Height), 64)).ToArray();
        var nv12 = Enumerable.Range(0, workers).Select(_ => (nint)NativeMemory.AlignedAlloc((nuint)writer.FrameBytes, 64)).ToArray();
        var frames = new VideoFrame?[workers];
        var totalSamples = (long)(duration * InputSounds.SampleRate);
        var audio = new AudioChunks(sounds, writer, totalSamples);
        try
        {
            for (var batch = 0; batch < frameCount; batch += workers)
            {
                cancellation.ThrowIfCancellationRequested();
                var count = Math.Min(workers, frameCount - batch);
                for (var i = 0; i < count; i++)
                    frames[i] = source.At((double)(batch + i) / fps * speed)?.AddRef();
                if (frames[0] == null) break;  // no picture at all

                Parallel.For(0, count, new ParallelOptions { CancellationToken = cancellation }, i =>
                {
                    var t = (double)(batch + i) / fps * speed;
                    composer.Render(frames[i] ?? frames[0]!, t, bgra[i], stride, scratch[i]);
                    var y = (byte*)nv12[i];
                    Yuv.BgraToNv12((byte*)bgra[i], stride, composer.Width, composer.Height, y, composer.Width,
                                   y + composer.Width * composer.Height, composer.Width);
                });

                for (var i = 0; i < count; i++)
                {
                    var frame = batch + i;
                    writer.WriteVideo((byte*)nv12[i], frame * 10_000_000L / fps, (frame + 1) * 10_000_000L / fps - frame * 10_000_000L / fps);
                    frames[i]?.Release();
                    frames[i] = null;
                }
                audio.WriteUntil((long)((double)(batch + count) / fps * InputSounds.SampleRate));
                progress?.Invoke((double)(batch + count) / frameCount);
            }
            audio.WriteUntil(totalSamples);
        }
        finally
        {
            foreach (var frame in frames) frame?.Release();
            foreach (var s in scratch) s.Dispose();
            foreach (var p in bgra) NativeMemory.AlignedFree((void*)p);
            foreach (var p in nv12) NativeMemory.AlignedFree((void*)p);
        }
    }

    /// <summary>Feeds the sound track to the writer in step with the video, so the file interleaves nicely.</summary>
    private sealed class AudioChunks(SoundTrack sounds, Mp4Writer writer, long totalSamples)
    {
        private long _written;
        private readonly float[] _mix = new float[InputSounds.SampleRate];
        private readonly short[] _pcm = new short[InputSounds.SampleRate];

        public void WriteUntil(long sample)
        {
            if (sounds.IsEmpty) return;
            sample = Math.Min(sample, totalSamples);
            while (_written < sample)
            {
                var count = (int)Math.Min(_mix.Length, sample - _written);
                var mix = _mix.AsSpan(0, count);
                mix.Clear();
                sounds.MixInto(_written, mix);
                SoundTrack.ToPcm16(mix, _pcm);
                writer.WriteAudio(_pcm.AsSpan(0, count), _written * 10_000_000L / InputSounds.SampleRate);
                _written += count;
            }
        }
    }
}

/// <summary>
/// Walks through a recording's frames in time order, holding the latest frame at or before each time asked
/// for. Source frames arrive at a variable rate (the screen is only captured when it changes).
/// Times are relative to the first frame.
/// </summary>
internal sealed class SourceFrames : IDisposable
{
    private readonly Mp4Reader _reader;
    private VideoFrame? _current;
    private Mp4Reader.DecodedSample? _next;

    public SourceFrames(Mp4Reader reader)
    {
        _reader = reader;
        _next = reader.ReadSample();
        BaseTime = _next?.Time ?? 0;
    }

    /// <summary>Time stamp of the first frame, in the file's own time.</summary>
    public double BaseTime { get; }

    /// <summary>The frame showing at <paramref name="t"/>, or null if the video has no frames. Owned by this object.</summary>
    public VideoFrame? At(double t)
    {
        Mp4Reader.DecodedSample? pending = null;
        while (_next != null && (_current == null && pending == null || _next.Time - BaseTime <= t + 0.001))
        {
            pending?.Dispose();
            pending = _next;
            _next = _reader.ReadSample();
        }
        if (pending != null)
        {
            var frame = pending.ToFrame();
            frame.Time -= BaseTime;
            pending.Dispose();
            _current?.Release();
            _current = frame;
        }
        return _current;
    }

    /// <summary>Time of the next frame after the current one, relative to the first frame.</summary>
    public double? NextTime => _next?.Time - BaseTime;

    /// <summary>Starts again from the keyframe at or before <paramref name="t"/>.</summary>
    public void Seek(double t)
    {
        _current?.Release();
        _current = null;
        _next?.Dispose();
        _reader.Seek(Math.Max(0, t + BaseTime));
        _next = _reader.ReadSample();
    }

    public void Dispose()
    {
        _current?.Release();
        _current = null;
        _next?.Dispose();
        _next = null;
    }
}
