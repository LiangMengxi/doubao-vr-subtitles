using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;

namespace DoubaoVR.Desktop;

internal sealed record SettingsSnapshot(string Json, string BackgroundImage);

internal sealed class Settings
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoubaoVR.Native");
    private static string FilePath => Path.Combine(DataDirectory, "settings.json");
    public static string? LoadWarning { get; private set; }
    [JsonPropertyName("width")] public float Width { get; set; } = 1;
    [JsonPropertyName("distance")] public float Distance { get; set; } = 1.2f;
    [JsonPropertyName("x")] public float Horizontal { get; set; }
    [JsonPropertyName("y")] public float Vertical { get; set; } = -.32f;
    [JsonPropertyName("opacity")] public float Opacity { get; set; } = 1;
    [JsonPropertyName("pitch")] public float Pitch { get; set; }
    [JsonPropertyName("yaw")] public float Yaw { get; set; }
    [JsonPropertyName("curvature_degrees"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public float CurvatureDegrees { get; set; }
    [JsonPropertyName("crop_top")] public float CropTop { get; set; }
    [JsonPropertyName("appearance")] public string Appearance { get; set; } = "system";
    [JsonPropertyName("show_capture_border")] public bool Border { get; set; }
    [JsonPropertyName("trim")] public bool Trim { get; set; } = true;
    [JsonPropertyName("exit_close_doubao")] public bool ExitCloseDoubao { get; set; }
    [JsonPropertyName("minimize_on_close")] public bool MinimizeOnClose { get; set; }
    [JsonPropertyName("accent_color")] public string AccentColor { get; set; } = "";
    [JsonPropertyName("background_image")] public string BackgroundImage { get; set; } = "";
    [JsonPropertyName("background_opacity")] public float BackgroundOpacity { get; set; } = .3f;
    [JsonPropertyName("audio_target_id")] public string AudioTargetId { get; set; } = "";
    [JsonPropertyName("audio_target_name")] public string AudioTargetName { get; set; } = "";
    [JsonPropertyName("microphone_target_id")] public string MicrophoneTargetId { get; set; } = "";
    [JsonPropertyName("microphone_target_name")] public string MicrophoneTargetName { get; set; } = "";
    [JsonPropertyName("pico_microphone_keeper")] public bool PicoMicrophoneKeeper { get; set; }
    [JsonPropertyName("steamvr_auto_start")] public bool SteamVrAutoStart { get; set; }
    [JsonPropertyName("assistant_activate_on_click"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AssistantActivateOnClick { get; set; }
    [JsonPropertyName("assistant_auto_attach")] public bool AssistantAutoAttach { get; set; } = true;
    [JsonPropertyName("assistant_width")] public float AssistantWidth { get; set; } = .7f;

    public static Settings Load()
    {
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoubaoVR", "settings.json");
        var candidates = File.Exists(FilePath) ? new[] { FilePath, FilePath + ".bak" } : new[] { legacy };
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(candidate)) ?? throw new JsonException("配置为空。");
                settings.Normalize();
                return settings;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                LoadWarning = $"配置读取失败，已尝试备份或默认值：{exception.Message}";
            }
        }
        return new Settings();
    }

    public void Normalize()
    {
        Width = Limit(Width, .25f, 2.5f, 1);
        Distance = Limit(Distance, .4f, 3, 1.2f);
        Horizontal = Limit(Horizontal, -1.2f, 1.2f, 0);
        Vertical = Limit(Vertical, -1.2f, .8f, -.32f);
        Opacity = Limit(Opacity, .1f, 1, 1);
        Pitch = Limit(Pitch, -45, 45, 0);
        Yaw = Limit(Yaw, -45, 45, 0);
        CurvatureDegrees = Limit(CurvatureDegrees, 0, 90, 0);
        CropTop = Limit(CropTop, 0, 120, 0);
        AssistantWidth = Limit(AssistantWidth, .3f, 1.5f, .7f);
        if (Appearance is not ("light" or "dark" or "system")) Appearance = "system";
        AccentColor = Rgb.TryParse(AccentColor, out var color) ? color.Hex : "";
        if (!BackgroundFiles.IsOwnedName(BackgroundImage)) BackgroundImage = "";
        BackgroundOpacity = Limit(BackgroundOpacity, 0, 1, .3f);
        if (AudioTargetId is null || AudioTargetId.Length >= 512 || AudioTargetId.Contains('\0')) AudioTargetId = "";
        if (AudioTargetName is null || AudioTargetName.Length > 255) AudioTargetName = "";
        if (MicrophoneTargetId is null || MicrophoneTargetId.Length >= 512 || MicrophoneTargetId.Contains('\0')) MicrophoneTargetId = "";
        if (MicrophoneTargetName is null || MicrophoneTargetName.Length > 255) MicrophoneTargetName = "";
    }

    private static float Limit(float value, float minimum, float maximum, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    public SettingsSnapshot Snapshot()
    {
        var copy = (Settings)MemberwiseClone();
        copy.Normalize();
        return new(JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }), copy.BackgroundImage);
    }

    public BridgeOptions Options(bool vr, bool preview) => new()
    {
        Size = (uint)Marshal.SizeOf<BridgeOptions>(), Width = Width, Distance = Distance,
        Horizontal = Horizontal, Vertical = Vertical, Opacity = Opacity, Pitch = Pitch, Yaw = Yaw,
        CropTop = (uint)CropTop, Trim = Trim ? 1u : 0, Border = Border ? 1u : 0,
        Vr = vr ? 1u : 0, Preview = preview ? 1u : 0, CurvatureDegrees = CurvatureDegrees
    };
}
