using System.Runtime.InteropServices;

namespace DoubaoVR.Desktop;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MicrophoneStats
{
    public uint Size, Enabled, State, Reserved;
    public ulong Frames, Packets;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Device;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string Error;
}

internal static class PicoMicrophoneService
{
    [DllImport("DoubaoVR.Core.dll", EntryPoint = "bridge_microphone_configure", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ConfigureNative(BridgeHandle bridge, uint enabled);
    [DllImport("DoubaoVR.Core.dll", EntryPoint = "bridge_microphone_stats", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadNative(BridgeHandle bridge, ref MicrophoneStats stats);
    [DllImport("DoubaoVR.Core.dll", EntryPoint = "bridge_microphone_stats", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadById(ulong bridge, ref MicrophoneStats stats);

    public static void Configure(BridgeHandle bridge, bool enabled) => Marshal.ThrowExceptionForHR(ConfigureNative(bridge, enabled ? 1u : 0u));

    public static MicrophoneStats Read(BridgeHandle bridge)
    {
        var stats = new MicrophoneStats { Size = (uint)Marshal.SizeOf<MicrophoneStats>(), Device = "", Error = "" };
        Marshal.ThrowExceptionForHR(ReadNative(bridge, ref stats));
        if (stats.Enabled > 1 || stats.State > 3) throw new InvalidDataException("PICO 麦克风状态无效。");
        return stats;
    }

}
