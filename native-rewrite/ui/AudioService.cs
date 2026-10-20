using System.Runtime.InteropServices;

namespace DoubaoVR.Desktop;

internal enum AudioFlow : uint { Playback, Microphone }

internal sealed record AudioDevice(string Id, string Name, uint DefaultRoles, bool Available = true)
{
    public override string ToString() => Available ? Name : $"{Name}（未连接）";
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct AudioDeviceNative
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string Id;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
    public uint DefaultRoles;
}
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct AudioDevicesNative
{
    public uint Size, Count;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public AudioDeviceNative[] Devices;
}
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct AudioTargetNative
{
    public uint Size;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string PlaybackId;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string MicrophoneId;
}

internal static class AudioService
{
    [DllImport("DoubaoVR.Core.dll", EntryPoint = "bridge_audio_devices", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadDevices(AudioFlow flow, ref AudioDevicesNative devices, ref BridgeError error);
    [DllImport("DoubaoVR.Core.dll", EntryPoint = "bridge_audio_restore", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RestoreDevice(in AudioTargetNative target, ref BridgeError error);

    public static List<AudioDevice> Devices(AudioFlow flow)
    {
        var devices = new AudioDevicesNative { Size = (uint)Marshal.SizeOf<AudioDevicesNative>(), Devices = new AudioDeviceNative[128] };
        var error = Error();
        Check(ReadDevices(flow, ref devices, ref error), error);
        if (devices.Count > devices.Devices.Length) throw new InvalidDataException("音频设备数量无效。");
        return devices.Devices.Take((int)devices.Count).Select(device => new AudioDevice(device.Id, device.Name, device.DefaultRoles))
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    public static void Restore(string playback, string microphone)
    {
        if ((string.IsNullOrEmpty(playback) && string.IsNullOrEmpty(microphone)) || !ValidIdentifier(playback) || !ValidIdentifier(microphone)) throw new InvalidOperationException("请先在设置中选择目标音频设备。");
        var target = new AudioTargetNative { Size = (uint)Marshal.SizeOf<AudioTargetNative>(), PlaybackId = playback, MicrophoneId = microphone };
        var error = Error();
        Check(RestoreDevice(target, ref error), error);
    }
    private static bool ValidIdentifier(string identifier) => identifier != null && identifier.Length < 512 && !identifier.Contains('\0') && (identifier.Length == 0 || !string.IsNullOrWhiteSpace(identifier));
    internal static BridgeError Error() => new() { Size = (uint)Marshal.SizeOf<BridgeError>(), Text = "" };
    private static void Check(int result, BridgeError error)
    {
        if (result < 0) throw new InvalidOperationException(string.IsNullOrEmpty(error.Text) ? "音频设备操作失败。" : error.Text, Marshal.GetExceptionForHR(result));
    }
}
