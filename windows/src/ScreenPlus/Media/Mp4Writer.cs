using System.IO;
using TerraFX.Interop.Windows;
using static ScreenPlus.Media.MediaFoundation;
using static TerraFX.Interop.Windows.MF;
using static TerraFX.Interop.Windows.Windows;

namespace ScreenPlus.Media;

/// <summary>
/// Writes an .mp4 with H.264 video (from NV12 frames) and, optionally, AAC audio (from 16-bit mono PCM),
/// using Media Foundation's sink writer. Tries the GPU encoder first and falls back to Windows' software one.
/// </summary>
internal sealed unsafe class Mp4Writer : IDisposable
{
    public sealed record Options(int Width, int Height, int Fps, int Bitrate, int KeyframeInterval, bool Audio);

    public const int AudioSampleRate = InputSounds.SampleRate;

    public int Width { get; }
    public int Height { get; }
    public bool UsesHardware { get; }

    private IMFSinkWriter* _writer;
    private readonly uint _videoStream;
    private readonly uint _audioStream;
    private bool _finished;

    public static Mp4Writer Create(string path, Options options, bool preferHardware = true)
    {
        if (!preferHardware) return new Mp4Writer(path, options, hardware: false);
        try
        {
            return new Mp4Writer(path, options, hardware: true);
        }
        catch (MediaException)
        {
            // Some GPU encoders reject a size or format; the software encoder handles everything.
            TryDelete(path);
            return new Mp4Writer(path, options, hardware: false);
        }
    }

    private Mp4Writer(string path, Options options, bool hardware)
    {
        Start();
        Width = options.Width;
        Height = options.Height;
        UsesHardware = hardware;

        using (var attributes = CreateAttributes(2))
        {
            SetUInt32(attributes.Get(), MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, hardware ? 1u : 0u);
            Check(attributes.Get()->SetGUID(G(MF_TRANSCODE_CONTAINERTYPE), G(MFTranscodeContainerType.MFTranscodeContainerType_MPEG4)),
                  "Setting up the video file");
            IMFSinkWriter* writer;
            fixed (char* url = path)
                Check(MFCreateSinkWriterFromURL(url, null, attributes.Get(), &writer), "Creating the video file");
            _writer = writer;
        }

        try
        {
            // Video: H.264 High profile out, NV12 in, tagged BT.709 so players get the colours right.
            using (var output = CreateMediaType())
            {
                var type = output.Get();
                SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Video);
                SetGuid(type, MF_MT_SUBTYPE, MFVideoFormat.MFVideoFormat_H264);
                SetUInt32(type, MF_MT_AVG_BITRATE, (uint)options.Bitrate);
                SetUInt32(type, MF_MT_MPEG2_PROFILE, (uint)eAVEncH264VProfile.eAVEncH264VProfile_High);
                SetVideoFormat(type, options);
                uint stream;
                Check(_writer->AddStream(type, &stream), "Adding the video track");
                _videoStream = stream;
            }
            using (var input = CreateMediaType())
            using (var encoding = CreateAttributes(3))
            {
                var type = input.Get();
                SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Video);
                SetGuid(type, MF_MT_SUBTYPE, MFVideoFormat.MFVideoFormat_NV12);
                SetUInt32(type, MF_MT_DEFAULT_STRIDE, (uint)options.Width);
                SetVideoFormat(type, options);
                SetUInt32(encoding.Get(), IID.IID_CODECAPI_AVEncCommonRateControlMode,
                          (uint)eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_UnconstrainedVBR);
                SetUInt32(encoding.Get(), IID.IID_CODECAPI_AVEncCommonMeanBitRate, (uint)options.Bitrate);
                SetUInt32(encoding.Get(), IID.IID_CODECAPI_AVEncMPVGOPSize, (uint)options.KeyframeInterval);
                Check(_writer->SetInputMediaType(_videoStream, type, encoding.Get()), "Setting up the H.264 encoder");
            }

            if (options.Audio)
            {
                using (var output = CreateMediaType())
                {
                    var type = output.Get();
                    SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                    SetGuid(type, MF_MT_SUBTYPE, MFAudioFormat.MFAudioFormat_AAC);
                    SetUInt32(type, MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                    SetUInt32(type, MF_MT_AUDIO_SAMPLES_PER_SECOND, AudioSampleRate);
                    SetUInt32(type, MF_MT_AUDIO_NUM_CHANNELS, 1);
                    SetUInt32(type, MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 16_000);  // 128 kbps
                    uint stream;
                    Check(_writer->AddStream(type, &stream), "Adding the audio track");
                    _audioStream = stream;
                }
                using (var input = CreateMediaType())
                {
                    var type = input.Get();
                    SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                    SetGuid(type, MF_MT_SUBTYPE, MFAudioFormat.MFAudioFormat_PCM);
                    SetUInt32(type, MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                    SetUInt32(type, MF_MT_AUDIO_SAMPLES_PER_SECOND, AudioSampleRate);
                    SetUInt32(type, MF_MT_AUDIO_NUM_CHANNELS, 1);
                    SetUInt32(type, MF_MT_AUDIO_BLOCK_ALIGNMENT, 2);
                    SetUInt32(type, MF_MT_AUDIO_AVG_BYTES_PER_SECOND, AudioSampleRate * 2);
                    Check(_writer->SetInputMediaType(_audioStream, type, null), "Setting up the AAC encoder");
                }
            }

            Check(_writer->BeginWriting(), "Starting the video file");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static void SetVideoFormat(IMFMediaType* type, Options options)
    {
        SetSize(type, MF_MT_FRAME_SIZE, options.Width, options.Height);
        SetSize(type, MF_MT_FRAME_RATE, options.Fps, 1);
        SetSize(type, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        SetUInt32(type, MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
        SetUInt32(type, MF_MT_YUV_MATRIX, (uint)MFVideoTransferMatrix.MFVideoTransferMatrix_BT709);
        SetUInt32(type, MF_MT_VIDEO_PRIMARIES, (uint)MFVideoPrimaries.MFVideoPrimaries_BT709);
        SetUInt32(type, MF_MT_TRANSFER_FUNCTION, (uint)MFVideoTransferFunction.MFVideoTransFunc_709);
        SetUInt32(type, MF_MT_VIDEO_NOMINAL_RANGE, (uint)MFNominalRange.MFNominalRange_16_235);
    }

    /// <summary>Size of one NV12 frame in bytes.</summary>
    public int FrameBytes => Width * Height * 3 / 2;

    /// <summary>Queues one NV12 frame (<see cref="FrameBytes"/> bytes). Times are in 100 ns units.</summary>
    public void WriteVideo(byte* nv12, long time, long duration) =>
        Write(_videoStream, nv12, FrameBytes, time, duration, "Encoding video");

    /// <summary>Queues 16-bit mono PCM at <see cref="AudioSampleRate"/>.</summary>
    public void WriteAudio(ReadOnlySpan<short> pcm, long time)
    {
        if (pcm.IsEmpty) return;
        fixed (short* data = pcm)
            Write(_audioStream, (byte*)data, pcm.Length * 2, time, pcm.Length * 10_000_000L / AudioSampleRate, "Encoding audio");
    }

    private void Write(uint stream, byte* data, int length, long time, long duration, string what)
    {
        using ComPtr<IMFMediaBuffer> buffer = default;
        Check(MFCreateMemoryBuffer((uint)length, buffer.GetAddressOf()), what);
        byte* destination;
        Check(buffer.Get()->Lock(&destination, null, null), what);
        Buffer.MemoryCopy(data, destination, length, length);
        buffer.Get()->Unlock();
        Check(buffer.Get()->SetCurrentLength((uint)length), what);

        using ComPtr<IMFSample> sample = default;
        Check(MFCreateSample(sample.GetAddressOf()), what);
        Check(sample.Get()->AddBuffer(buffer.Get()), what);
        Check(sample.Get()->SetSampleTime(time), what);
        Check(sample.Get()->SetSampleDuration(duration), what);
        Check(_writer->WriteSample(stream, sample.Get()), what);
    }

    /// <summary>Finishes the file. Without this the .mp4 is unplayable.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        Check(_writer->Finalize(), "Finishing the video file");
    }

    public void Dispose()
    {
        if (_writer != null)
        {
            _writer->Release();
            _writer = null;
        }
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
