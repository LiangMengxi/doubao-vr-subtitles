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
Require(Marshal.SizeOf<AudioDeviceNative>() == 1540 && Marshal.SizeOf<AudioDevicesNative>() == 197128 && Marshal.SizeOf<AudioTargetNative>() == 2052, "Audio ABI layout mismatch.");
Require(Marshal.OffsetOf<AudioTargetNative>(nameof(AudioTargetNative.MicrophoneId)).ToInt32() == 1028, "Microphone ABI offset mismatch.");
Require(Rgb.TryParse("#aB00fF", out var accent) && accent.Hex == "#AB00FF", "Theme color parsing failed.");
foreach (var invalidColor in new[] { "", "red", "#FFF", "#FF0000FF", "#-00000", "#12ZZ00" }) Require(!Rgb.TryParse(invalidColor, out _), "Invalid theme color accepted.");
Require(new Rgb(255, 255, 255).UseBlackText && !new Rgb(0, 0, 0).UseBlackText, "Accent foreground contrast failed.");
Require(new Rgb(0, 0, 0).ReadableOn(new Rgb(32, 32, 32)).Contrast(new Rgb(32, 32, 32)) >= 4.5
    && new Rgb(255, 255, 255).ReadableOn(new Rgb(255, 255, 255)).Contrast(new Rgb(255, 255, 255)) >= 4.5, "Accent text must remain readable in light and dark themes.");
var appearance = new Settings { AccentColor = "#aB00fF", BackgroundOpacity = float.NaN, BackgroundImage = "../../private.png", AudioTargetId = "invalid\0device", MicrophoneTargetId = new string('x', 512), MicrophoneTargetName = new string('x', 256) };
appearance.Normalize();
Require(appearance.AccentColor == "#AB00FF" && appearance.BackgroundOpacity == .3f && appearance.BackgroundImage == "" && appearance.AudioTargetId == "", "Appearance/audio input normalization failed.");
Require(appearance.MicrophoneTargetId == "" && appearance.MicrophoneTargetName == "", "Microphone input normalization failed.");
var outputOnly = JsonSerializer.Deserialize<Settings>("{\"audio_target_id\":\"existing-speaker\",\"audio_target_name\":\"Speakers\"}")!;
Require(outputOnly.AudioTargetId == "existing-speaker" && outputOnly.MicrophoneTargetId == "", "Old playback selection must survive without selecting a microphone automatically.");
var imageSize = BackgroundFiles.Size(4000, 3000, 1024);
Require(imageSize.Width == 1600 && imageSize.Height == 1200 && BackgroundFiles.Size(400, 300, 1024) == (400u, 300u), "Background scaling must be bounded and preserve aspect ratio.");
foreach (var invalidImage in new[] { (0u, 20u, 1024L), (8000u, 8000u, 1024L), (100u, 100u, 30L * 1024 * 1024) })
{
    bool rejected = false;
    try { BackgroundFiles.Size(invalidImage.Item1, invalidImage.Item2, invalidImage.Item3); }
    catch (InvalidDataException) { rejected = true; }
    Require(rejected, "Oversized image dimensions/size accepted.");
}
string imageFolder = Path.Combine(Path.GetTempPath(), "DoubaoVR-BackgroundChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(imageFolder, "backgrounds"));
try
{
    string currentImage = Guid.NewGuid().ToString("N") + ".png";
    string backupImage = Guid.NewGuid().ToString("N") + ".png";
    string staleImage = Guid.NewGuid().ToString("N") + ".png";
    foreach (var name in new[] { currentImage, backupImage, staleImage, "user-original.png" }) File.WriteAllText(Path.Combine(imageFolder, "backgrounds", name), "controlled fixture");
    File.WriteAllText(Path.Combine(imageFolder, "settings.json.bak"), JsonSerializer.Serialize(new Settings { BackgroundImage = backupImage }));
    BackgroundFiles.Cleanup(imageFolder, currentImage);
    Require(File.Exists(BackgroundFiles.Resolve(imageFolder, currentImage)) && File.Exists(BackgroundFiles.Resolve(imageFolder, backupImage)), "Current/backup images must remain intact.");
    Require(!File.Exists(BackgroundFiles.Resolve(imageFolder, staleImage)) && File.Exists(Path.Combine(imageFolder, "backgrounds", "user-original.png")), "Only obsolete app-owned images may be cleaned.");
    File.WriteAllText(BackgroundFiles.Resolve(imageFolder, staleImage), "controlled fixture");
    File.WriteAllText(Path.Combine(imageFolder, "settings.json.bak"), "[]");
    BackgroundFiles.Cleanup(imageFolder, currentImage);
    Require(File.Exists(BackgroundFiles.Resolve(imageFolder, staleImage)), "An unreadable backup must prevent image cleanup.");
    var appearanceSaved = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { AccentColor = "#112233", BackgroundImage = currentImage, BackgroundOpacity = .6f, AudioTargetId = "speaker-id", AudioTargetName = "Speakers", MicrophoneTargetId = "microphone-id", MicrophoneTargetName = "Microphone" }))!;
    Require(appearanceSaved.BackgroundImage == currentImage && appearanceSaved.BackgroundOpacity == .6f && appearanceSaved.AudioTargetId == "speaker-id" && appearanceSaved.AccentColor == "#112233", "New preference persistence failed.");
    Require(appearanceSaved.MicrophoneTargetId == "microphone-id" && appearanceSaved.MicrophoneTargetName == "Microphone", "Microphone preference persistence failed.");
}
finally
{
    foreach (var path in Directory.EnumerateFiles(Path.Combine(imageFolder, "backgrounds"))) File.Delete(path);
    File.Delete(Path.Combine(imageFolder, "settings.json.bak"));
    Directory.Delete(Path.Combine(imageFolder, "backgrounds"));
    Directory.Delete(imageFolder);
}
Console.WriteLine("PASS: accent validation, foreground contrast, bounded image scaling, owned-file isolation and background/audio persistence.");
await BackgroundChecks.RunAsync();
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
    Require(NativeBridge.Version() == 5, "Core ABI version differs.");
    var audioError = AudioService.Error();
    var invalidAudio = new AudioDevicesNative { Size = 1, Devices = new AudioDeviceNative[128] };
    Require(AudioService.ReadDevices(AudioFlow.Playback, ref invalidAudio, ref audioError) < 0, "Invalid audio structure size must be rejected.");
    invalidAudio.Size = (uint)Marshal.SizeOf<AudioDevicesNative>();
    Require(AudioService.ReadDevices((AudioFlow)2, ref invalidAudio, ref audioError) < 0, "Invalid audio direction must be rejected.");
    var invalidTarget = new AudioTargetNative { Size = 2052, PlaybackId = "", MicrophoneId = "" };
    Require(AudioService.RestoreDevice(invalidTarget, ref audioError) < 0, "Empty audio target must be rejected without changing defaults.");
    var audioBefore = AudioService.Devices(AudioFlow.Playback);
    var microphonesBefore = AudioService.Devices(AudioFlow.Microphone);
    string missingDevice = "DoubaoVR-invalid-device-" + Guid.NewGuid().ToString("N");
    invalidTarget.PlaybackId = missingDevice;
    Require(AudioService.RestoreDevice(invalidTarget, ref audioError) < 0, "Missing audio target must not select a fallback device.");
    invalidTarget.MicrophoneId = missingDevice;
    invalidTarget.PlaybackId = audioBefore.FirstOrDefault()?.Id ?? "";
    Require(AudioService.RestoreDevice(invalidTarget, ref audioError) < 0, "An invalid microphone must reject the entire request before changing playback.");
    invalidTarget.PlaybackId = missingDevice;
    invalidTarget.MicrophoneId = microphonesBefore.FirstOrDefault()?.Id ?? "";
    Require(AudioService.RestoreDevice(invalidTarget, ref audioError) < 0, "An invalid playback device must not change the microphone.");
    if (audioBefore.Count > 0)
    {
        invalidTarget.PlaybackId = "";
        invalidTarget.MicrophoneId = audioBefore[0].Id;
        Require(AudioService.RestoreDevice(invalidTarget, ref audioError) < 0, "Playback endpoints must not be accepted as microphones.");
    }
    if (microphonesBefore.Count > 0)
    {
        invalidTarget.PlaybackId = microphonesBefore[0].Id;
        invalidTarget.MicrophoneId = "";
        Require(AudioService.RestoreDevice(invalidTarget, ref audioError) < 0, "Microphones must not be accepted as playback endpoints.");
    }
    invalidTarget.Size = 1028;
    Require(AudioService.RestoreDevice(invalidTarget, ref audioError) < 0, "Old audio request sizes must be rejected.");
    var audioAfter = AudioService.Devices(AudioFlow.Playback);
    var microphonesAfter = AudioService.Devices(AudioFlow.Microphone);
    Require(audioBefore.Count == audioAfter.Count && audioBefore.Zip(audioAfter).All(pair => pair.First.Id == pair.Second.Id && pair.First.DefaultRoles == pair.Second.DefaultRoles), "Invalid switching must preserve playback defaults.");
    Require(microphonesBefore.Count == microphonesAfter.Count && microphonesBefore.Zip(microphonesAfter).All(pair => pair.First.Id == pair.Second.Id && pair.First.DefaultRoles == pair.Second.DefaultRoles), "Invalid switching must preserve microphone defaults.");
    Console.WriteLine($"PASS: audio ABI, read-only enumeration ({audioAfter.Count} playback / {microphonesAfter.Count} microphone devices), invalid/missing/wrong-direction targets and unchanged system defaults.");
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
