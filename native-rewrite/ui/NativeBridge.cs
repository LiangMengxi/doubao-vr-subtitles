using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DoubaoVR.Desktop;

[StructLayout(LayoutKind.Sequential)]
internal struct BridgeOptions
{
    public uint Size;
    public float Width, Distance, Horizontal, Vertical, Opacity, Pitch, Yaw;
    public uint CropTop, Trim, Border, Vr, Preview;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BridgeStats
{
    public uint Size, State;
    public ulong Frames, Submitted;
    public uint Width, Height;
    public double ProcessingMs;
}

internal sealed class BridgeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public ulong Identifier { get; }
    public BridgeHandle() : base(true) { Identifier = NativeBridge.Create(); SetHandle(checked((nint)Identifier)); }
    protected override bool ReleaseHandle() { NativeBridge.Destroy(handle); return true; }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct BridgeError
{
    public uint Size;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)] public string Text;
}

internal sealed class PreviewBuffer : SafeBuffer
{
    internal const uint Capacity = 640 * 240 * 4;
    internal const uint HeaderSize = 24;
    internal const uint StructureSize = HeaderSize + Capacity;

    public PreviewBuffer() : base(true)
    {
        SetHandle(Marshal.AllocHGlobal((int)StructureSize));
        Initialize(StructureSize);
        Write(0, StructureSize);
    }

    protected override bool ReleaseHandle() { Marshal.FreeHGlobal(handle); return true; }

    public uint CopyTo(byte[] output, out uint width, out uint height, out ulong sequence)
    {
        width = Read<uint>(4);
        height = Read<uint>(8);
        sequence = Read<ulong>(16);
        if (width > 640 || height > 240) throw new InvalidDataException("预览尺寸超出接口限制。");
        uint length = checked(width * height * 4);
        if (length > output.Length) throw new InvalidDataException("预览缓冲区不足。");
        ReadArray(HeaderSize, output, 0, (int)length);
        return length;
    }
}

internal static class NativeBridge
{
    private const string Library = "DoubaoVR.Core.dll";
    public const uint AbiVersion = 8;

    public static void EnsureVersion()
    {
        if (Version() != AbiVersion) throw new InvalidOperationException("原生核心接口版本不匹配。");
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, in int value, uint size);

    public static void TitleBarTheme(nint hwnd, bool dark, Windows.UI.Color background, Windows.UI.Color foreground)
    {
        int enabled = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, enabled, sizeof(int));
        int caption = background.R | background.G << 8 | background.B << 16;
        int text = foreground.R | foreground.G << 8 | foreground.B << 16;
        DwmSetWindowAttribute(hwnd, 35, caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36, text, sizeof(int));
    }

    [DllImport(Library, EntryPoint = "bridge_version", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Version();
    [DllImport(Library, EntryPoint = "bridge_create", CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong Create();
    [DllImport(Library, EntryPoint = "bridge_destroy", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Destroy(nint bridge);
    [DllImport(Library, EntryPoint = "bridge_start", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Start(BridgeHandle bridge, nuint hwnd, in BridgeOptions options);
    public static void StartCapture(BridgeHandle bridge, nuint hwnd, in BridgeOptions options)
    {
        int result = Start(bridge, hwnd, options);
        if (result >= 0) return;
        string detail = Error(bridge);
        throw new COMException(string.IsNullOrWhiteSpace(detail) ? $"启动字幕捕获失败（0x{unchecked((uint)result):X8}）。" : detail, result);
    }
    [DllImport(Library, EntryPoint = "bridge_configure", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Configure(BridgeHandle bridge, in BridgeOptions options);
    [DllImport(Library, EntryPoint = "bridge_stop", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Stop(BridgeHandle bridge);
    [DllImport(Library, EntryPoint = "bridge_stats", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ReadStats(BridgeHandle bridge, ref BridgeStats stats);
    [DllImport(Library, EntryPoint = "bridge_stats", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadStatsById(ulong bridge, ref BridgeStats stats);
    [DllImport(Library, EntryPoint = "bridge_error", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int ReadError(BridgeHandle bridge, ref BridgeError output);
    [DllImport(Library, EntryPoint = "bridge_preview", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ReadPreview(BridgeHandle bridge, ulong afterSequence, PreviewBuffer output);

    public static uint Preview(BridgeHandle bridge, PreviewBuffer buffer, byte[] output, ulong afterSequence,
        out uint width, out uint height, out ulong sequence)
    {
        int result = ReadPreview(bridge, afterSequence, buffer);
        Marshal.ThrowExceptionForHR(result);
        if (result == 1) { width = height = 0; sequence = afterSequence; return 0; }
        return buffer.CopyTo(output, out width, out height, out sequence);
    }

    public static BridgeStats Stats(BridgeHandle bridge)
    {
        var stats = new BridgeStats { Size = (uint)Marshal.SizeOf<BridgeStats>() };
        Marshal.ThrowExceptionForHR(ReadStats(bridge, ref stats));
        return stats;
    }

    public static string Error(BridgeHandle bridge)
    {
        var output = new BridgeError { Size = (uint)Marshal.SizeOf<BridgeError>(), Text = "" };
        Marshal.ThrowExceptionForHR(ReadError(bridge, ref output));
        return output.Text;
    }
}
