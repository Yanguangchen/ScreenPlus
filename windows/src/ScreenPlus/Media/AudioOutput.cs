using System.Runtime.InteropServices;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.TIME;
using static TerraFX.Interop.Windows.WAVE;
using static TerraFX.Interop.Windows.Windows;

namespace ScreenPlus.Media;

/// <summary>
/// Streams mono 16-bit audio to the default output device (WinMM), and reports how much has actually
/// been played — the live preview uses that as its clock, so picture and click sounds stay in sync.
/// </summary>
internal sealed unsafe class AudioOutput : IDisposable
{
    public const int SampleRate = InputSounds.SampleRate;
    private const int BufferSamples = SampleRate / 50;  // 20 ms
    private const int BufferCount = 4;

    private HWAVEOUT _device;
    private HANDLE _event;
    private WAVEHDR* _headers;
    private short* _samples;
    private Thread? _thread;
    private volatile bool _playing;
    private Action<Span<short>>? _fill;

    private AudioOutput() { }

    /// <summary>Null when there's no audio device (the preview then plays silently on a normal clock).</summary>
    public static AudioOutput? TryCreate()
    {
        var output = new AudioOutput();
        try
        {
            output._event = CreateEventW(null, FALSE, FALSE, null);
            var format = new WAVEFORMATEX
            {
                wFormatTag = (ushort)WAVE_FORMAT_PCM,
                nChannels = 1,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = SampleRate * 2,
            };
            HWAVEOUT device;
            if (waveOutOpen(&device, WAVE_MAPPER, &format, (nuint)(nint)output._event, 0, (uint)CALLBACK_EVENT) != 0)
            {
                output.Dispose();
                return null;
            }
            output._device = device;
            output._headers = (WAVEHDR*)NativeMemory.AllocZeroed((nuint)(sizeof(WAVEHDR) * BufferCount));
            output._samples = (short*)NativeMemory.AllocZeroed((nuint)(BufferSamples * BufferCount * 2));
            for (var i = 0; i < BufferCount; i++)
            {
                var header = &output._headers[i];
                header->lpData = (sbyte*)(output._samples + i * BufferSamples);
                header->dwBufferLength = BufferSamples * 2;
                waveOutPrepareHeader(device, header, (uint)sizeof(WAVEHDR));
                header->dwFlags |= (uint)WHDR_DONE;  // free to fill
            }
            return output;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            output.Dispose();
            return null;
        }
    }

    /// <summary>Starts playing from sample 0 of whatever <paramref name="fill"/> produces.</summary>
    public void Start(Action<Span<short>> fill)
    {
        Stop();
        _fill = fill;
        _playing = true;
        _thread = new Thread(Pump) { IsBackground = true, Name = "ScreenPlus audio", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread == null) return;
        _playing = false;
        SetEvent(_event);
        _thread.Join();
        _thread = null;
        waveOutReset(_device);  // returns all queued buffers and rewinds the position
    }

    /// <summary>Seconds of audio actually played since <see cref="Start"/>.</summary>
    public double PlayedSeconds
    {
        get
        {
            MMTIME time = default;
            time.wType = (uint)TIME_SAMPLES;
            if (waveOutGetPosition(_device, &time, (uint)sizeof(MMTIME)) != 0) return 0;
            return time.wType switch
            {
                (uint)TIME_SAMPLES => time.u.sample / (double)SampleRate,
                (uint)TIME_BYTES => time.u.cb / 2.0 / SampleRate,
                _ => 0,
            };
        }
    }

    private void Pump()
    {
        while (_playing)
        {
            for (var i = 0; i < BufferCount && _playing; i++)
            {
                var header = &_headers[i];
                if ((header->dwFlags & WHDR_DONE) == 0) continue;
                var samples = new Span<short>(header->lpData, BufferSamples);
                samples.Clear();
                _fill?.Invoke(samples);
                header->dwFlags &= ~(uint)WHDR_DONE;
                waveOutWrite(_device, header, (uint)sizeof(WAVEHDR));
            }
            WaitForSingleObject(_event, 10);
        }
    }

    public void Dispose()
    {
        if (_device != HWAVEOUT.NULL)
        {
            Stop();
            waveOutReset(_device);
            for (var i = 0; i < BufferCount; i++)
                waveOutUnprepareHeader(_device, &_headers[i], (uint)sizeof(WAVEHDR));
            waveOutClose(_device);
            _device = HWAVEOUT.NULL;
        }
        if (_headers != null) NativeMemory.Free(_headers);
        if (_samples != null) NativeMemory.Free(_samples);
        _headers = null;
        _samples = null;
        if (_event != HANDLE.NULL) CloseHandle(_event);
        _event = HANDLE.NULL;
    }
}
