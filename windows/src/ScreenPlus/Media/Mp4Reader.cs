using System.IO;
using TerraFX.Interop.Windows;
using static ScreenPlus.Media.MediaFoundation;
using static TerraFX.Interop.Windows.MF;
using static TerraFX.Interop.Windows.Windows;

namespace ScreenPlus.Media;

/// <summary>Decodes a video file frame by frame into BGRA <see cref="VideoFrame"/>s, using Media Foundation's source reader.</summary>
internal sealed unsafe class Mp4Reader : IDisposable
{
    private const uint VideoStream = unchecked((uint)MF_SOURCE_READER_FIRST_VIDEO_STREAM);

    /// <summary>Visible size of the video.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>Length of the file in seconds.</summary>
    public double Duration { get; }

    private IMFSourceReader* _reader;
    private bool _nv12;
    private int _frameWidth, _frameHeight, _defaultStride, _cropX, _cropY;
    private YuvMatrix _matrix = YuvMatrix.Bt709;
    private bool _fullRange;

    public Mp4Reader(string path)
    {
        Start();
        if (!File.Exists(path)) throw new FileNotFoundException($"The recording's video is missing: {path}");

        // NV12 straight from the decoder, which we convert ourselves; if the decoder can't do that,
        // let Media Foundation convert to RGB.
        if (!TryOpen(path, nv12: true, out var error) && !TryOpen(path, nv12: false, out error))
            throw error!;

        PROPVARIANT value = default;
        if (SUCCEEDED(_reader->GetPresentationAttribute(unchecked((uint)MF_SOURCE_READER_MEDIASOURCE), G(MF_PD_DURATION), &value)))
        {
            Duration = value.uhVal.QuadPart / 10_000_000.0;
            PropVariantClear(&value);
        }
    }

    private bool TryOpen(string path, bool nv12, out Exception? error)
    {
        error = null;
        try
        {
            using var attributes = CreateAttributes(1);
            SetUInt32(attributes.Get(), MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, nv12 ? 0u : 1u);
            IMFSourceReader* reader;
            fixed (char* url = path)
                Check(MFCreateSourceReaderFromURL(url, attributes.Get(), &reader), "Opening the video");
            _reader = reader;

            Check(_reader->SetStreamSelection(unchecked((uint)MF_SOURCE_READER_ALL_STREAMS), false), "Opening the video");
            Check(_reader->SetStreamSelection(VideoStream, true), "Opening the video track");
            using var type = CreateMediaType();
            SetGuid(type.Get(), MF_MT_MAJOR_TYPE, MFMediaType_Video);
            SetGuid(type.Get(), MF_MT_SUBTYPE, nv12 ? MFVideoFormat.MFVideoFormat_NV12 : MFVideoFormat.MFVideoFormat_RGB32);
            Check(_reader->SetCurrentMediaType(VideoStream, null, type.Get()), "Setting up the video decoder");
            _nv12 = nv12;
            UpdateFormat();
            return true;
        }
        catch (Exception e) when (e is MediaException or InvalidDataException)
        {
            Dispose();
            error = e;
            return false;
        }
    }

    private void UpdateFormat()
    {
        using ComPtr<IMFMediaType> type = default;
        Check(_reader->GetCurrentMediaType(VideoStream, type.GetAddressOf()), "Reading the video format");
        (_frameWidth, _frameHeight) = GetSize(type.Get(), MF_MT_FRAME_SIZE);
        if (_frameWidth <= 0 || _frameHeight <= 0) throw new InvalidDataException("The video has no picture size.");

        uint stride;
        _defaultStride = SUCCEEDED(type.Get()->GetUINT32(G(MF_MT_DEFAULT_STRIDE), &stride)) ? (int)stride : 0;

        // Decoders pad frames to whole macroblocks (1080 → 1088); the aperture says what's visible.
        MFVideoArea area;
        uint size;
        if (SUCCEEDED(type.Get()->GetBlob(G(MF_MT_MINIMUM_DISPLAY_APERTURE), (byte*)&area, (uint)sizeof(MFVideoArea), &size))
            && area.Area.cx > 0 && area.Area.cy > 0)
        {
            _cropX = Math.Clamp((int)area.OffsetX.value, 0, _frameWidth - 1) & ~1;
            _cropY = Math.Clamp((int)area.OffsetY.value, 0, _frameHeight - 1) & ~1;
            Width = Math.Min(area.Area.cx, _frameWidth - _cropX);
            Height = Math.Min(area.Area.cy, _frameHeight - _cropY);
        }
        else
        {
            (_cropX, _cropY, Width, Height) = (0, 0, _frameWidth, _frameHeight);
        }

        uint matrix, range;
        _matrix = SUCCEEDED(type.Get()->GetUINT32(G(MF_MT_YUV_MATRIX), &matrix))
                  && matrix == (uint)MFVideoTransferMatrix.MFVideoTransferMatrix_BT601
            ? YuvMatrix.Bt601
            : YuvMatrix.Bt709;
        _fullRange = SUCCEEDED(type.Get()->GetUINT32(G(MF_MT_VIDEO_NOMINAL_RANGE), &range))
                     && range == (uint)MFNominalRange.MFNominalRange_0_255;
    }

    /// <summary>The next frame, or null at the end of the video. The caller owns (and releases) the frame.</summary>
    public VideoFrame? Read()
    {
        using var sample = ReadSample();
        return sample?.ToFrame();
    }

    /// <summary>
    /// The next decoded sample, not yet converted to BGRA, or null at the end of the video. Lets callers
    /// skip over frames cheaply (when seeking, or when frames come faster than they're shown).
    /// </summary>
    public DecodedSample? ReadSample()
    {
        while (true)
        {
            uint streamIndex, flags;
            long timestamp;
            IMFSample* sample = null;
            Check(_reader->ReadSample(VideoStream, 0, &streamIndex, &flags, &timestamp, &sample), "Decoding the video");
            if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0) UpdateFormat();
            var end = (flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0;
            var error = (flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0;
            if (sample != null && (end || error))
            {
                sample->Release();
                sample = null;
            }
            if (error) throw new MediaException("The video couldn't be decoded.");
            if (end) return null;
            if (sample == null) continue;  // a gap in the stream
            return new DecodedSample(this, sample, timestamp / 10_000_000.0);
        }
    }

    /// <summary>A decoded frame still in the decoder's format.</summary>
    public sealed class DecodedSample : IDisposable
    {
        private readonly Mp4Reader _reader;
        private IMFSample* _sample;

        internal DecodedSample(Mp4Reader reader, IMFSample* sample, double time)
        {
            _reader = reader;
            _sample = sample;
            Time = time;
        }

        /// <summary>Presentation time in seconds.</summary>
        public double Time { get; }

        public VideoFrame ToFrame()
        {
            ObjectDisposedException.ThrowIf(_sample == null, this);
            var frame = new VideoFrame(_reader.Width, _reader.Height) { Time = Time };
            try
            {
                _reader.Convert(_sample, frame);
            }
            catch
            {
                frame.Release();
                throw;
            }
            return frame;
        }

        public void Dispose()
        {
            if (_sample == null) return;
            _sample->Release();
            _sample = null;
        }
    }

    private void Convert(IMFSample* sample, VideoFrame frame)
    {
        using ComPtr<IMFMediaBuffer> buffer = default;
        Check(sample->ConvertToContiguousBuffer(buffer.GetAddressOf()), "Reading a video frame");

        using ComPtr<IMF2DBuffer> buffer2D = default;
        byte* data = null;
        var pitch = 0;
        var locked2D = SUCCEEDED(buffer.Get()->QueryInterface(__uuidof<IMF2DBuffer>(), (void**)buffer2D.GetAddressOf()))
                       && SUCCEEDED(buffer2D.Get()->Lock2D(&data, &pitch));
        if (!locked2D)
        {
            uint max, length;
            Check(buffer.Get()->Lock(&data, &max, &length), "Reading a video frame");
            pitch = _defaultStride != 0 ? _defaultStride : _nv12 ? _frameWidth : _frameWidth * 4;
            // A negative stride means bottom-up rows: the first row in memory is the last one.
            if (pitch < 0) data += (long)-pitch * (_frameHeight - 1);
        }

        try
        {
            if (_nv12)
            {
                var y = data + (long)_cropY * pitch + _cropX;
                var uv = data + (long)pitch * _frameHeight + (long)(_cropY / 2) * pitch + _cropX;
                Yuv.Nv12ToBgra(y, pitch, uv, pitch, Width, Height, frame.Data, frame.Stride, _matrix, _fullRange);
            }
            else
            {
                var rowBytes = Width * 4;
                for (var row = 0; row < Height; row++)
                {
                    var src = data + (long)(row + _cropY) * pitch + _cropX * 4;
                    var dst = frame.Data + (long)row * frame.Stride;
                    Buffer.MemoryCopy(src, dst, rowBytes, rowBytes);
                    for (var x = 3; x < rowBytes; x += 4) dst[x] = 255;  // RGB32's fourth byte is padding
                }
            }
        }
        finally
        {
            if (locked2D) buffer2D.Get()->Unlock2D();
            else buffer.Get()->Unlock();
        }
    }

    /// <summary>Jumps to the keyframe at or before <paramref name="seconds"/>; the next <see cref="Read"/> continues from there.</summary>
    public void Seek(double seconds)
    {
        PROPVARIANT position = default;
        position.vt = (ushort)VARENUM.VT_I8;
        position.hVal.QuadPart = (long)(Math.Max(0, seconds) * 10_000_000);
        var format = Guid.Empty;
        Check(_reader->SetCurrentPosition(&format, &position), "Seeking in the video");
    }

    public void Dispose()
    {
        if (_reader != null)
        {
            _reader->Release();
            _reader = null;
        }
    }
}
