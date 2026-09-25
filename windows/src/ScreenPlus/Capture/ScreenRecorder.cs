using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ScreenPlus.Media;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using TerraFX.Interop.WinRT;
using DirectXPixelFormat = Windows.Graphics.DirectX.DirectXPixelFormat;
using IDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;
using IInspectable = TerraFX.Interop.WinRT.IInspectable;
using IDirect3DSurface = Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface;
using Windows.Graphics.Capture;
using WinRT;
using static TerraFX.Interop.DirectX.D3D11_CPU_ACCESS_FLAG;
using static TerraFX.Interop.DirectX.D3D11_CREATE_DEVICE_FLAG;
using static TerraFX.Interop.DirectX.D3D11_MAP;
using static TerraFX.Interop.DirectX.D3D11_USAGE;
using static TerraFX.Interop.DirectX.D3D_DRIVER_TYPE;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;
using static TerraFX.Interop.Windows.Windows;
using static TerraFX.Interop.WinRT.WinRT;

namespace ScreenPlus.Capture;

public sealed class RecorderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Records one display, without the cursor, to an .mp4 using Windows.Graphics.Capture.
///
/// Frames only arrive when the screen changes, so the video has a variable frame rate (like
/// ScreenCaptureKit on macOS); the renderer holds each frame until the next one.
/// </summary>
internal sealed unsafe class ScreenRecorder : IDisposable
{
    public sealed record Result(string Path, int Width, int Height, double FirstFrameHostTime);

    /// <summary>At most 60 frames a second, whatever the display's refresh rate.</summary>
    private const long MinFrameInterval = 10_000_000 / 60 - 30_000;  // 100 ns units, with slack for jitter
    private const int Fps = 60;

    /// <summary>The recorded display, in physical pixels.</summary>
    public ScreenInfo Screen { get; private set; }
    /// <summary>Size of the recorded video (the display, halved if it's too big for H.264).</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private ID3D11Texture2D* _staging;
    private int _captureWidth, _captureHeight, _factor;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private Mp4Writer? _writer;
    private string? _path;

    private readonly Lock _lock = new();
    private BlockingCollection<(nint Buffer, long Time)>? _queue;
    private readonly ConcurrentBag<nint> _buffers = [];
    private Thread? _encoderThread;
    private Exception? _encoderError;
    private long? _firstFrameTime;  // 100 ns, QueryPerformanceCounter clock
    private long _lastFrameTime;
    private bool _stopping;
    private int _droppedFrames;

    /// <summary>Cursor-free capture needs Windows 10 version 2004 (build 19041) or later.</summary>
    public static bool IsSupported =>
        GraphicsCaptureSession.IsSupported()
        && Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession",
                                                                        "IsCursorCaptureEnabled");

    /// <summary>QueryPerformanceCounter time in 100 ns units, the clock capture frames are stamped with.</summary>
    public static long Now()
    {
        long ticks = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency;
        return ticks / frequency * 10_000_000 + ticks % frequency * 10_000_000 / frequency;
    }

    /// <summary>Starts recording the display that currently contains the mouse pointer.</summary>
    public void Start(string path)
    {
        if (!IsSupported)
            throw new RecorderException("Screen capture isn't available. ScreenPlus needs Windows 10 version 2004 or later.");
        ComApartment.EnsureMultithreaded();
        _path = path;
        Screen = Screens.WithCursor();
        try
        {
            CreateDevice();
            _item = CreateItemForMonitor((HMONITOR)Screen.Handle);
            var size = _item.Size;
            _captureWidth = size.Width;
            _captureHeight = size.Height;

            CreateStagingTexture();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // H.264 encoders top out around 4096 pixels wide; record bigger displays at half size,
            // and fall back to half size too if no encoder takes the full size (e.g. tall portrait screens).
            var tooBig = size.Width > 4096 || size.Height > 4096 || (long)size.Width * size.Height > 4096L * 2304;
            for (_factor = tooBig ? 2 : 1; ; _factor++)
            {
                Width = size.Width / _factor & ~1;
                Height = size.Height / _factor & ~1;
                var bitrate = (int)Math.Clamp((long)Width * Height * 8, 12_000_000, 80_000_000);
                try
                {
                    var options = new Mp4Writer.Options(Width, Height, Fps, bitrate, KeyframeInterval: Fps, Audio: false);
                    _writer = Mp4Writer.Create(path, options, preferHardware: Mp4Writer.HardwareWorks(Width, Height, Fps));
                    break;
                }
                catch (MediaException) when (_factor < 2)
                {
                    Mp4Writer.TryDelete(path);
                }
            }

            _queue = new BlockingCollection<(nint, long)>(boundedCapacity: 8);
            _encoderThread = new Thread(EncodeLoop) { IsBackground = true, Name = "ScreenPlus encoder" };
            _encoderThread.Start();

            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;  // we draw our own, smoothed cursor later
            _session.StartCapture();
        }
        catch (Exception e)
        {
            Dispose();
            if (_path != null) Mp4Writer.TryDelete(_path);
            throw e as RecorderException ?? new RecorderException($"Couldn't start recording: {e.Message}", e);
        }
    }

    public Result Stop()
    {
        lock (_lock) _stopping = true;
        _session?.Dispose();
        _session = null;
        _pool?.Dispose();
        _pool = null;
        // Frames only arrive on changes, so hold the last one until now: a static ending is kept.
        var stopTime = Now();

        _queue?.CompleteAdding();
        _encoderThread?.Join();
        try
        {
            if (_encoderError != null) throw new RecorderException($"Recording failed: {_encoderError.Message}", _encoderError);
            if (_firstFrameTime is not { } first || _writer == null) throw new RecorderException("No video frames were captured.");
            if (_lastFrame != 0 && stopTime - first > _lastFrameTime - first + 10_000)
                _writer.WriteVideo((byte*)_lastFrame, stopTime - first, 10_000_000 / Fps);
            _writer.Finish();
            if (_droppedFrames > 0) Trace.WriteLine($"ScreenPlus: dropped {_droppedFrames} frames while recording");
            return new Result(_path!, Width, Height, first / 10_000_000.0);
        }
        finally
        {
            Dispose();
        }
    }

    // MARK: Capture

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object args)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame == null) return;
        var arrival = Now();
        // SystemRelativeTime is on the QueryPerformanceCounter clock, like our input timestamps.
        // Fall back to the arrival time if it ever isn't.
        var time = frame.SystemRelativeTime.Ticks;
        if (Math.Abs(arrival - time) > 50_000_000) time = arrival;

        lock (_lock)
        {
            if (_stopping || _queue == null) return;
            if (_firstFrameTime != null && time - _lastFrameTime < MinFrameInterval) return;
            if (_buffers.Count == 0 && _queue.Count >= _queue.BoundedCapacity)
            {
                _droppedFrames++;  // the encoder is behind; skip this frame rather than stall capture
                return;
            }

            var texture = TextureOf(frame.Surface);
            try
            {
                _context->CopyResource((ID3D11Resource*)_staging, (ID3D11Resource*)texture);
            }
            finally
            {
                texture->Release();
            }

            D3D11_MAPPED_SUBRESOURCE mapped;
            if (FAILED(_context->Map((ID3D11Resource*)_staging, 0, D3D11_MAP_READ, 0, &mapped))) return;
            var buffer = RentBuffer();
            try
            {
                var y = (byte*)buffer;
                Yuv.BgraToNv12((byte*)mapped.pData, (int)mapped.RowPitch, _captureWidth, _captureHeight,
                               y, Width, y + Width * Height, Width, _factor);
            }
            finally
            {
                _context->Unmap((ID3D11Resource*)_staging, 0);
            }

            _firstFrameTime ??= time;
            _lastFrameTime = time;
            if (!_queue.TryAdd((buffer, time - _firstFrameTime.Value)))
            {
                _droppedFrames++;
                _buffers.Add(buffer);
            }
        }
    }

    private nint _lastFrame;

    private void EncodeLoop()
    {
        try
        {
            foreach (var (buffer, time) in _queue!.GetConsumingEnumerable())
            {
                _writer!.WriteVideo((byte*)buffer, time, 10_000_000 / Fps);
                // Keep the newest frame for the ending; recycle the one before.
                if (_lastFrame != 0) _buffers.Add(_lastFrame);
                _lastFrame = buffer;
            }
        }
        catch (Exception e)
        {
            _encoderError = e;
            lock (_lock) _stopping = true;
            // Drain so capture never blocks.
            foreach (var (buffer, _) in _queue!.GetConsumingEnumerable()) _buffers.Add(buffer);
        }
    }

    private nint RentBuffer() =>
        _buffers.TryTake(out var buffer) ? buffer : (nint)NativeMemory.AlignedAlloc((nuint)(Width * Height * 3 / 2), 64);

    // MARK: Direct3D and WinRT plumbing

    private void CreateDevice()
    {
        D3D_FEATURE_LEVEL level;
        ID3D11Device* device;
        ID3D11DeviceContext* context;
        var flags = (uint)D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        var hr = D3D11CreateDevice(null, D3D_DRIVER_TYPE_HARDWARE, HMODULE.NULL, flags, null, 0, D3D11.D3D11_SDK_VERSION,
                                   &device, &level, &context);
        if (FAILED(hr))  // no usable GPU (e.g. some virtual machines): Windows' software renderer
            hr = D3D11CreateDevice(null, D3D_DRIVER_TYPE_WARP, HMODULE.NULL, flags, null, 0, D3D11.D3D11_SDK_VERSION,
                                   &device, &level, &context);
        MediaFoundation.Check(hr, "Creating a Direct3D device");
        _device = device;
        _context = context;

        IDXGIDevice* dxgi;
        MediaFoundation.Check(_device->QueryInterface(__uuidof<IDXGIDevice>(), (void**)&dxgi), "Creating a Direct3D device");
        IInspectable* inspectable;
        try
        {
            MediaFoundation.Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, &inspectable), "Creating a Direct3D device");
        }
        finally
        {
            dxgi->Release();
        }
        _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi((nint)inspectable);
        inspectable->Release();
    }

    private void CreateStagingTexture()
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)_captureWidth,
            Height = (uint)_captureHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE_STAGING,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_READ,
        };
        ID3D11Texture2D* staging;
        MediaFoundation.Check(_device->CreateTexture2D(&desc, null, &staging), "Creating a capture buffer");
        _staging = staging;
    }

    // TerraFX marks these interop interfaces as Windows 10 21H1+, but they exist since 1803.
#pragma warning disable CA1416

    /// <summary>A capture item for a whole display, via IGraphicsCaptureItemInterop.</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(HMONITOR monitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        HSTRING name;
        fixed (char* chars = className)
            MediaFoundation.Check(WindowsCreateString(chars, (uint)className.Length, &name), "Starting screen capture");
        IGraphicsCaptureItemInterop* interop;
        try
        {
            MediaFoundation.Check(RoGetActivationFactory(name, __uuidof<IGraphicsCaptureItemInterop>(), (void**)&interop),
                                  "Starting screen capture");
        }
        finally
        {
            WindowsDeleteString(name);
        }

        void* item;
        try
        {
            var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");  // IGraphicsCaptureItem
            MediaFoundation.Check(interop->CreateForMonitor(monitor, &iid, &item), "Starting screen capture");
        }
        finally
        {
            interop->Release();
        }
        var result = GraphicsCaptureItem.FromAbi((nint)item);
        Marshal.Release((nint)item);
        return result;
    }

    /// <summary>The Direct3D texture behind a captured frame. The caller releases it.</summary>
    private static ID3D11Texture2D* TextureOf(IDirect3DSurface surface)
    {
        var unknown = (IUnknown*)((IWinRTObject)surface).NativeObject.ThisPtr;
        IDirect3DDxgiInterfaceAccess* access;
        MediaFoundation.Check(unknown->QueryInterface(__uuidof<IDirect3DDxgiInterfaceAccess>(), (void**)&access), "Reading a captured frame");
        try
        {
            ID3D11Texture2D* texture;
            MediaFoundation.Check(access->GetInterface(__uuidof<ID3D11Texture2D>(), (void**)&texture), "Reading a captured frame");
            return texture;
        }
        finally
        {
            access->Release();
        }
    }

#pragma warning restore CA1416

    public void Dispose()
    {
        lock (_lock) _stopping = true;
        _session?.Dispose();
        _session = null;
        _pool?.Dispose();
        _pool = null;
        if (_queue is { IsAddingCompleted: false }) _queue.CompleteAdding();
        _encoderThread?.Join();
        _encoderThread = null;
        _writer?.Dispose();
        _writer = null;
        _item = null;
        (_winrtDevice as IDisposable)?.Dispose();
        _winrtDevice = null;
        if (_staging != null) { _staging->Release(); _staging = null; }
        if (_context != null) { _context->Release(); _context = null; }
        if (_device != null) { _device->Release(); _device = null; }
        if (_lastFrame != 0) { _buffers.Add(_lastFrame); _lastFrame = 0; }
        if (_queue != null)
            while (_queue.TryTake(out var pending)) _buffers.Add(pending.Buffer);
        while (_buffers.TryTake(out var buffer)) NativeMemory.AlignedFree((void*)buffer);
        _queue?.Dispose();
        _queue = null;
    }
}
