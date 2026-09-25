using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.MF;
using static TerraFX.Interop.Windows.Windows;

namespace ScreenPlus.Media;

/// <summary>Shared Media Foundation plumbing.</summary>
internal static unsafe class MediaFoundation
{
    private static readonly Lazy<Exception?> Startup = new(() =>
    {
        try
        {
            var hr = MFStartup((uint)MF_VERSION, (uint)MFSTARTUP_FULL);
            return FAILED(hr) ? new MediaException("Starting Windows Media Foundation", hr.Value) : null;
        }
        catch (DllNotFoundException)
        {
            return new MediaException("Windows Media Foundation is not installed. On Windows N editions, install the " +
                                      "Media Feature Pack from Settings → Apps → Optional features.");
        }
    });

    public static void Start()
    {
        if (Startup.Value is { } error) throw error;
    }

    /// <summary>Address of a GUID constant (TerraFX keeps them in static read-only data, so they never move).</summary>
    public static Guid* G(in Guid guid) => (Guid*)Unsafe.AsPointer(ref Unsafe.AsRef(in guid));

    public static void Check(HRESULT hr, string what)
    {
        if (FAILED(hr)) throw new MediaException(what, hr.Value);
    }

    public static ComPtr<IMFMediaType> CreateMediaType()
    {
        ComPtr<IMFMediaType> type = default;
        Check(MFCreateMediaType(type.GetAddressOf()), "Creating a media type");
        return type;
    }

    public static ComPtr<IMFAttributes> CreateAttributes(uint count)
    {
        ComPtr<IMFAttributes> attributes = default;
        Check(MFCreateAttributes(attributes.GetAddressOf(), count), "Creating media attributes");
        return attributes;
    }

    public static void SetSize(IMFMediaType* type, in Guid key, int a, int b) =>
        Check(type->SetUINT64(G(key), ((ulong)(uint)a << 32) | (uint)b), "Setting a media attribute");

    public static (int A, int B) GetSize(IMFMediaType* type, in Guid key)
    {
        ulong value;
        return SUCCEEDED(type->GetUINT64(G(key), &value)) ? ((int)(value >> 32), (int)(value & 0xFFFFFFFF)) : (0, 0);
    }

    public static void SetUInt32(IMFAttributes* attributes, in Guid key, uint value) =>
        Check(attributes->SetUINT32(G(key), value), "Setting a media attribute");

    public static void SetUInt32(IMFMediaType* type, in Guid key, uint value) =>
        Check(type->SetUINT32(G(key), value), "Setting a media attribute");

    public static void SetGuid(IMFMediaType* type, in Guid key, in Guid value) =>
        Check(type->SetGUID(G(key), G(value)), "Setting a media attribute");
}

public sealed class MediaException : Exception
{
    public MediaException(string message) : base(message) { }

    public MediaException(string what, int hr) : base($"{what} failed: {Describe(hr)}")
    {
        HResult = hr;
    }

    private static string Describe(int hr) => unchecked((uint)hr) switch
    {
        0xC00D5212 => "no suitable video codec was found. On Windows N editions, install the Media Feature Pack.",
        0xC00D36C4 => "the file type isn't supported.",
        0xC00D36B4 => "the video format isn't supported.",
        0xC00D36E6 => "the video format isn't supported by this computer's encoder.",
        0x80070002 or 0x80070003 => "the file couldn't be found.",
        0x80070005 => "access was denied.",
        0x80070020 => "the file is in use by another program.",
        0x80070070 => "the disk is full.",
        _ => $"{Marshal.GetExceptionForHR(hr)?.Message.TrimEnd('.') ?? "unknown error"} (0x{hr:X8}).",
    };
}
