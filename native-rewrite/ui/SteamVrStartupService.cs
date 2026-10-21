using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DoubaoVR.Desktop;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SteamVrRegistration
{
    public uint Size, Enabled;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4096)] public string Manifest;
}

internal static class SteamVrStartupService
{
    internal const string ApplicationKey = "org.liangmengxi.doubaovr";
    private const string Command = "--steamvr-registration";
    private static string ManifestPath => Path.Combine(Settings.DataDirectory, "steamvr", "DoubaoVR.vrmanifest");
    private sealed record Result(bool Success, string Error);

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

    public static async Task ChangeAsync(bool enabled, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string executable = Executable;
        string manifest = ManifestPath;
        if (manifest.Length >= 4096) throw new InvalidOperationException("SteamVR 注册文件路径过长。");
        string content = BuildManifest(executable);
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        string temporary = manifest + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        if (File.Exists(manifest)) File.Copy(manifest, manifest + ".bak", true);
        File.Move(temporary, manifest, true);
        cancellation.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = AppContext.BaseDirectory
            }
        };
        process.StartInfo.ArgumentList.Add(Command);
        process.StartInfo.ArgumentList.Add(enabled ? "enable" : "disable");
        if (!process.Start()) throw new InvalidOperationException("无法启动 SteamVR 注册服务。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string response = await output;
            string detail = await errors;
            if (response.Length > 16384 || string.IsNullOrWhiteSpace(response)) throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? "SteamVR 注册服务没有返回结果。" : detail);
            var result = JsonSerializer.Deserialize<Result>(response) ?? throw new InvalidDataException("SteamVR 注册服务返回了无效结果。");
            if (process.ExitCode != 0 || !result.Success) throw new InvalidOperationException(string.IsNullOrEmpty(result.Error) ? "SteamVR 注册失败。" : result.Error);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            try { await Task.WhenAll(output, errors); } catch (OperationCanceledException) { }
            throw new TimeoutException("SteamVR 注册超时，启动项状态尚未确认，请在 SteamVR 设置中检查后重试。");
        }
    }

    public static bool TryHandleCommand(string[] arguments, out int exitCode)
    {
        exitCode = 0;
        if (arguments.Length == 0 || arguments[0] != Command) return false;
        Result result;
        try
        {
            if (arguments.Length != 2 || arguments[1] is not ("enable" or "disable")) throw new ArgumentException("SteamVR 注册参数无效。");
            if (NativeBridge.Version() != 6) throw new InvalidOperationException("原生核心接口版本不匹配。");
            string manifest = ManifestPath;
            if (new FileInfo(manifest).Length > 32768) throw new InvalidDataException("SteamVR 注册文件过大。");
            ValidateManifest(File.ReadAllText(manifest), Executable);
            var request = new SteamVrRegistration { Size = (uint)Marshal.SizeOf<SteamVrRegistration>(), Enabled = arguments[1] == "enable" ? 1u : 0u, Manifest = manifest };
            var error = AudioService.Error();
            int status = RegisterNative(request, ref error);
            if (status < 0) throw new InvalidOperationException(string.IsNullOrEmpty(error.Text) ? "无法注册 SteamVR 启动项。" : error.Text);
            result = new Result(true, "");
        }
        catch (Exception exception) { result = new Result(false, exception.Message); exitCode = 1; }
        using var response = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        response.Write(JsonSerializer.Serialize(result));
        return true;
    }
}
