using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DoubaoVR.Desktop;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SteamVrStateNative
{
    public uint Size, Installed, AutoLaunch, Dashboard;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4096)] public string Executable;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)] public string Arguments;
}

internal static partial class SteamVrStartupService
{
    [DllImport("DoubaoVR.Core.dll", EntryPoint = "bridge_steamvr_state", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int StateNative(ref SteamVrStateNative state, ref BridgeError error);

    public static Task ChangeAsync(bool enabled, Func<bool, Task> persist, Action<bool> display, CancellationToken cancellation) =>
        new StartupTransaction(Path.GetDirectoryName(ManifestPath)!, Executable, new ProcessBackend()).ChangeAsync(enabled, persist, display, cancellation);

    private sealed class ProcessBackend : IStartupBackend
    {
        public async Task<StartupState> ReadAsync() => (await ExecuteAsync("read")).State
            ?? throw new InvalidDataException("SteamVR 状态服务没有返回实际状态。");
        public async Task ApplyAsync(bool installed, bool autoLaunch) =>
            _ = await ExecuteAsync(installed ? autoLaunch ? "enable" : "install-disabled" : "disable");
    }

    private static async Task<Result> ExecuteAsync(string action)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = AppContext.BaseDirectory
            }
        };
        process.StartInfo.ArgumentList.Add(Command);
        process.StartInfo.ArgumentList.Add(action);
        if (!process.Start()) throw new InvalidOperationException("无法启动 SteamVR 注册服务。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var errors = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string response = await output, detail = await errors;
            if (string.IsNullOrWhiteSpace(response)) throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? "SteamVR 服务没有返回结果。" : detail);
            var result = JsonSerializer.Deserialize<Result>(response) ?? throw new InvalidDataException("SteamVR 服务返回了无效结果。");
            if (process.ExitCode != 0 || !result.Success) throw new InvalidOperationException(string.IsNullOrEmpty(result.Error) ? "SteamVR 注册失败。" : result.Error);
            return result;
        }
        catch (Exception)
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            try { await Task.WhenAll(output, errors); } catch (Exception) { }
            if (timeout.IsCancellationRequested) throw new TimeoutException("SteamVR 服务超时，必须重新核实实际状态后才能恢复。");
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellation)
    {
        var text = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) != 0)
        {
            if (text.Length + count > 32768) throw new InvalidDataException("SteamVR 服务返回内容过大。");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    public static bool TryHandleCommand(string[] arguments, out int exitCode)
    {
        exitCode = 0;
        if (arguments.Length == 0 || arguments[0] != Command) return false;
        Result result;
        try
        {
            if (arguments.Length != 2 || arguments[1] is not ("read" or "enable" or "disable" or "install-disabled"))
                throw new ArgumentException("SteamVR 注册参数无效。");
            NativeBridge.EnsureVersion();
            var error = AudioService.Error();
            if (arguments[1] == "read")
            {
                var state = new SteamVrStateNative { Size = (uint)Marshal.SizeOf<SteamVrStateNative>(), Executable = "", Arguments = "" };
                if (StateNative(ref state, ref error) < 0) throw new InvalidOperationException(error.Text);
                result = new Result(true, "", new StartupState(state.Installed != 0, state.AutoLaunch != 0, state.Executable, state.Arguments, state.Dashboard != 0));
            }
            else
            {
                using var manifest = new FileStream(ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (manifest.Length > 32768) throw new InvalidDataException("SteamVR 注册文件过大。");
                using var reader = new StreamReader(manifest, Encoding.UTF8, true);
                string content = reader.ReadToEnd();
                using var document = JsonDocument.Parse(content);
                string executable = document.RootElement.GetProperty("applications")[0].GetProperty("binary_path_windows").GetString()
                    ?? throw new InvalidDataException("SteamVR 注册程序路径为空。");
                ValidateManifest(content, executable);
                var request = new SteamVrRegistration
                {
                    Size = (uint)Marshal.SizeOf<SteamVrRegistration>(), Enabled = arguments[1] == "disable" ? 0u : 1u,
                    AutoLaunch = arguments[1] == "enable" ? 1u : 0u, Manifest = ManifestPath
                };
                if (RegisterNative(request, ref error) < 0) throw new InvalidOperationException(error.Text);
                result = new Result(true, "");
            }
        }
        catch (Exception exception) { result = new Result(false, exception.Message); exitCode = 1; }
        using var response = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        response.Write(JsonSerializer.Serialize(result));
        return true;
    }
}
