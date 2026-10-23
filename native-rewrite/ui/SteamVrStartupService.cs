using System.Runtime.InteropServices;
using System.Text.Json;

namespace DoubaoVR.Desktop;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SteamVrRegistration
{
    public uint Size, Enabled, AutoLaunch, Reserved;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4096)] public string Manifest;
}

internal static partial class SteamVrStartupService
{
    internal const string ApplicationKey = "org.liangmengxi.doubaovr";
    private const string Command = "--steamvr-registration";
    private static string ManifestPath => Path.Combine(Settings.DataDirectory, "steamvr", "DoubaoVR.vrmanifest");
    private sealed record Result(bool Success, string Error, StartupState? State = null);

    [DllImport("DoubaoVR.Core.dll", EntryPoint = "bridge_steamvr_register", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RegisterNative(in SteamVrRegistration registration, ref BridgeError error);

    internal static string BuildManifest(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Length >= 3500 || executable.Any(char.IsControl) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SteamVR 启动程序路径无效。");
        return JsonSerializer.Serialize(new
        {
            applications = new[]
            {
                new
                {
                    app_key = ApplicationKey,
                    launch_type = "binary",
                    binary_path_windows = Path.GetFullPath(executable),
                    arguments = "--steamvr-startup",
                    is_dashboard_overlay = true,
                    strings = new Dictionary<string, object>
                    {
                        ["zh_cn"] = new { name = "豆包VR字幕" },
                        ["en_us"] = new { name = "豆包VR字幕" }
                    }
                }
            }
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    internal static void ValidateManifest(string content, string executable)
    {
        if (content.Length > 32768) throw new InvalidDataException("SteamVR 注册文件过大。");
        _ = BuildManifest(executable);
        content = content.TrimStart('\uFEFF');
        using var document = JsonDocument.Parse(content);
        var applications = document.RootElement.GetProperty("applications");
        if (applications.GetArrayLength() != 1) throw new InvalidDataException("SteamVR 注册文件包含其他应用。");
        var application = applications[0];
        if (application.GetProperty("app_key").GetString() != ApplicationKey ||
            application.GetProperty("launch_type").GetString() != "binary" ||
            application.GetProperty("arguments").GetString() != "--steamvr-startup" ||
            !application.GetProperty("is_dashboard_overlay").GetBoolean() ||
            !string.Equals(application.GetProperty("binary_path_windows").GetString(), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SteamVR 注册文件与当前程序不匹配。");
    }

    private static string Executable => Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");

}
