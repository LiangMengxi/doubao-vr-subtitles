using System.Runtime.InteropServices;
using System.Text.Json;
using System.Diagnostics;
using DoubaoVR.Desktop;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

if (args is ["--exit-target"]) { await Task.Delay(TimeSpan.FromMinutes(1)); return; }

Require(Marshal.SizeOf<BridgeOptions>() == 52, "Options ABI size differs from C++.");
Require(Marshal.OffsetOf<BridgeOptions>(nameof(BridgeOptions.Yaw)).ToInt32() == 28, "Yaw ABI offset differs from C++.");
Require(Marshal.SizeOf<BridgeStats>() == 40, "Stats ABI size differs from C++.");
Require(ShortcutService.Parse("Alt+Z").SequenceEqual(new ushort[] { 0x12, 0x5a }), "Alt+Z parsing failed.");
Require(ShortcutService.Parse("Ctrl+Shift+F24").SequenceEqual(new ushort[] { 0x11, 0x10, 0x87 }), "Function key parsing failed.");
foreach (var invalid in new[] { "Z", "Ctrl+Ctrl+Z", "Win+Z", "Alt+F25", "Alt+", "Ctrl+Shift+Alt+Z+X" })
{
    bool rejected = false;
    try { ShortcutService.Parse(invalid); }
    catch (InvalidOperationException) { rejected = true; }
    Require(rejected, $"Invalid shortcut was accepted: {invalid}");
}
var settings = new Settings { Width = float.NaN, Distance = -1, Opacity = 20, CropTop = 999, Appearance = "unknown", Pitch = -99, Yaw = float.PositiveInfinity };
settings.Normalize();
Require(settings.Width == 1 && settings.Distance == .4f && settings.Opacity == 1 && settings.CropTop == 120 && settings.Appearance == "system", "Configuration normalization failed.");
Require(settings.Pitch == -45 && settings.Yaw == 0, "Independent tilt normalization failed.");
var legacy = JsonSerializer.Deserialize<Settings>("{\"width\":1.5,\"x\":0.2,\"y\":-0.4,\"crop_top\":44.0,\"show_capture_border\":true}")!;
Require(legacy.Width == 1.5f && legacy.Horizontal == .2f && legacy.Vertical == -.4f && legacy.CropTop == 44 && legacy.Border, "Legacy settings compatibility failed.");
Require(legacy.Yaw == 0 && !legacy.MinimizeOnClose && !legacy.ExitCloseDoubao, "Old configuration must preserve safe behavior defaults.");
var saved = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { Pitch = 12, Yaw = -23, MinimizeOnClose = true, ExitCloseDoubao = true }))!;
Require(saved.Pitch == 12 && saved.Yaw == -23 && saved.MinimizeOnClose && saved.ExitCloseDoubao, "Behavior and tilt persistence failed.");
Require(saved.Options(false, false).Yaw == -23, "Yaw was not passed to the native core.");
foreach (bool minimize in new[] { false, true })
foreach (bool closeDoubao in new[] { false, true })
foreach (bool exitButton in new[] { false, true })
foreach (bool smoke in new[] { false, true })
{
    var policy = ClosePolicy.Decide(new Settings { MinimizeOnClose = minimize, ExitCloseDoubao = closeDoubao }, exitButton, smoke);
    Require(policy.Minimize == (minimize && !exitButton && !smoke), "Titlebar/button minimize policy failed.");
    Require(policy.CloseDoubao == (closeDoubao && exitButton && !smoke), "Doubao must only close on an explicit Exit button, never a smoke test or titlebar X.");
}
static Process StartExitTarget()
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test executable.");
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
    if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Settings).Assembly.Location);
    start.ArgumentList.Add("--exit-target");
    return Process.Start(start) ?? throw new InvalidOperationException("Failed to start controlled process.");
}
using (var target = StartExitTarget())
using (var bystander = StartExitTarget())
{
    try
    {
        await DoubaoExitService.CloseVerifiedAsync(new[] { target, bystander }, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Require(!target.HasExited && !bystander.HasExited, "Unverified processes must remain alive.");
        var executable = WindowSource.Executable(target.SafeHandle) ?? throw new InvalidOperationException("Cannot inspect controlled process.");
        await DoubaoExitService.CloseVerifiedAsync(new[] { target }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { executable.ToUpperInvariant() });
        Require(target.HasExited && !bystander.HasExited, "Only the verified target must close.");
        await DoubaoExitService.CloseVerifiedAsync(new[] { target }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { executable });
    }
    finally
    {
        foreach (var process in new[] { target, bystander })
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
Console.WriteLine("PASS: independent tilt, setting persistence, close-policy matrix and verified-process exit isolation.");
var candidate = new CaptionWindow(1, 1, "", "Chrome_WidgetWin_1", 800, 180, 0x80);
var main = candidate with { Title = "豆包", Width = 1000, Height = 800, Style = 0 };
Require(candidate.Score >= 150 && main.Score < 150, "Subtitle candidate ranking failed.");
if (args.Length > 0)
{
    var corePath = Path.GetFullPath(args[0]);
    NativeLibrary.SetDllImportResolver(typeof(NativeBridge).Assembly, (name, _, _) =>
        name == "DoubaoVR.Core.dll" ? NativeLibrary.Load(corePath) : 0);
    Require(NativeBridge.Version() == 3, "Core ABI version differs.");
    using var bridge = new BridgeHandle();
    Require(!bridge.IsInvalid, "Core allocation failed.");
    Require(NativeBridge.Stats(bridge).State == 0, "A new core must be stopped.");
    var options = new Settings().Options(false, false);
    Require(NativeBridge.Start(bridge, 0, options) < 0, "Invalid HWND must be rejected.");
    options.Width = float.NaN;
    Require(NativeBridge.Configure(bridge, options) < 0, "NaN options must be rejected.");
    options = new Settings().Options(false, false);
    options.Size = 48;
    Require(NativeBridge.Configure(bridge, options) < 0, "Old ABI structures must be rejected.");
    options = new Settings().Options(false, false);
    foreach (float invalidYaw in new[] { float.NaN, float.PositiveInfinity, -46, 46 })
    {
        options.Yaw = invalidYaw;
        Require(NativeBridge.Configure(bridge, options) < 0, "Invalid yaw must be rejected.");
    }
    options.Yaw = -45;
    options.Pitch = 45;
    Require(NativeBridge.Configure(bridge, options) == 0, "Valid simultaneous pitch/yaw must be accepted.");
    NativeBridge.Stop(bridge);
    NativeBridge.Stop(bridge);
    Require(NativeBridge.Stats(bridge).State == 0, "Repeated stop must be harmless.");
    using var preview = new PreviewBuffer();
    var previewPixels = new byte[PreviewBuffer.Capacity];
    Require(NativeBridge.Preview(bridge, preview, previewPixels, out var width, out var height, out var sequence) == 0
        && width == 0 && height == 0 && sequence == 0, "An idle core must return an empty preview.");
    var destroyed = bridge.Identifier;
    bridge.Dispose();
    var staleStats = new BridgeStats { Size = 40 };
    Require(NativeBridge.ReadStatsById(destroyed, ref staleStats) < 0, "A destroyed object ID must be rejected.");
    for (int iteration = 0; iteration < 32; iteration++)
    {
        using var concurrent = new BridgeHandle();
        var identifier = concurrent.Identifier;
        Require(identifier != destroyed, "Object IDs must never be recycled.");
        var reader = Task.Run(() =>
        {
            for (int read = 0; read < 500; read++)
            {
                var snapshot = new BridgeStats { Size = 40 };
                int result = NativeBridge.ReadStatsById(identifier, ref snapshot);
                Require(result == 0 || result == unchecked((int)0x80070006), "Concurrent read returned an unexpected result.");
            }
        });
        concurrent.Dispose();
        reader.GetAwaiter().GetResult();
        Require(NativeBridge.ReadStatsById(identifier, ref staleStats) < 0, "A stale ID became valid after concurrent destruction.");
    }
    Console.WriteLine("PASS: native ABI, handle lifetime, invalid inputs and repeated stop.");
    Console.WriteLine("PASS: bounded SafeBuffer preview, stale IDs, unique IDs and concurrent destruction.");
}
Console.WriteLine("PASS: settings migration, normalization, shortcut validation, window ranking and ABI layouts.");
