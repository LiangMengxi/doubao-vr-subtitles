using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace DoubaoVR.Desktop;

internal static class ShortcutService
{
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int Horizontal, Vertical; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    internal static ushort[] Parse(string shortcut)
    {
        var tokens = shortcut.Split('+', StringSplitOptions.TrimEntries);
        if (tokens.Length is < 2 or > 4) throw new InvalidOperationException("请设置带 Ctrl、Alt 或 Shift 的字幕快捷键。");
        var keys = new List<ushort>();
        foreach (var token in tokens[..^1])
        {
            ushort key = token.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => 0x11, "ALT" => 0x12, "SHIFT" => 0x10,
                _ => throw new InvalidOperationException("不支持的快捷键修饰键。")
            };
            if (keys.Contains(key)) throw new InvalidOperationException("快捷键修饰键重复。");
            keys.Add(key);
        }
        var final = tokens[^1].ToUpperInvariant();
        if (final.Length == 1 && (final[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) keys.Add(final[0]);
        else if (final.StartsWith('F') && int.TryParse(final.AsSpan(1), out int function) && function is >= 1 and <= 24)
            keys.Add((ushort)(0x6f + function));
        else keys.Add(final switch
        {
            "SPACE" => 0x20, "ENTER" or "RETURN" => 0x0d, "TAB" => 0x09,
            "HOME" => 0x24, "END" => 0x23, "INSERT" => 0x2d, "DELETE" => 0x2e,
            "PAGEUP" => 0x21, "PAGEDOWN" => 0x22,
            _ => throw new InvalidOperationException("当前字幕快捷键不受支持。")
        });
        return keys.ToArray();
    }

    private static ushort[] Read()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Doubao", "User Data", "Local State");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!root.TryGetProperty("saman.shortcut.audio_translate", out var setting))
        {
            if (!root.TryGetProperty("saman", out var saman) || !saman.TryGetProperty("shortcut", out var shortcuts)
                || !shortcuts.TryGetProperty("audio_translate", out setting))
                throw new InvalidOperationException("请先在豆包设置中启用实时字幕快捷键。");
        }
        if (setting.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException("豆包实时字幕快捷键已禁用。");
        return Parse(setting.GetProperty("key").GetString() ?? "");
    }

    internal static string? RunningExecutable()
    {
        var processes = Process.GetProcessesByName("doubao");
        try
        {
            foreach (var process in processes)
            {
                var executable = WindowSource.Executable((uint)process.Id);
                if (executable != null && File.Exists(executable)) return executable;
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return null;
    }

    internal static string InstalledExecutable()
    {
        using var key = Registry.ClassesRoot.OpenSubKey(@"doubaoapp\shell\open\command");
        var command = (key?.GetValue(null) as string)?.Trim();
        if (!string.IsNullOrEmpty(command))
        {
            string candidate = command.StartsWith('"') ? command.Split('"')[1] : command.Split(' ')[0];
            if (string.Equals(Path.GetFileName(candidate), "doubao.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)) return candidate;
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Doubao", "Application", "Doubao.exe");
        if (File.Exists(fallback)) return fallback;
        throw new InvalidOperationException("未找到豆包桌面版，请先安装并登录。");
    }

    public static async Task<CaptionWindow> OpenAsync(CancellationToken cancellation)
    {
        var existing = WindowSource.Discover().FirstOrDefault(window => window.Score >= 150);
        if (existing != null) return existing;
        var keys = Read();
        if (RunningExecutable() == null)
        {
            var executable = InstalledExecutable();
            Process.Start(new ProcessStartInfo(executable)
            {
                Arguments = "--launch_from=auto_run --no-startup-window --auto_launch_delay_seconds=0",
                WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            })?.Dispose();
            await Task.Delay(4000, cancellation);
        }
        var deadline = DateTime.UtcNow.AddSeconds(20);
        bool sent = false;
        while (DateTime.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            var caption = WindowSource.Discover().FirstOrDefault(window => window.Score >= 150);
            if (caption != null) return caption;
            if (!sent && !new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0))
            {
                var events = keys.Select(key => Key(key, false)).Concat(keys.Reverse().Select(key => Key(key, true))).ToArray();
                if (SendInput((uint)events.Length, events, Marshal.SizeOf<Input>()) != events.Length)
                {
                    var releases = keys.Reverse().Select(key => Key(key, true)).ToArray();
                    SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>());
                    throw new InvalidOperationException("无法发送快捷键，请确认豆包与字幕桥以相同权限运行。");
                }
                sent = true;
            }
            await Task.Delay(200, cancellation);
        }
        throw new TimeoutException("未能唤起字幕，请确认豆包已登录且字幕快捷键有效。");
    }

    private static Input Key(ushort key, bool release) => new()
    {
        Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = release ? 2u : 0 } }
    };
}
