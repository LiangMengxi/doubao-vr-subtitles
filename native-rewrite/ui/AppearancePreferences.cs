using System.Globalization;
using System.Text.Json;

namespace DoubaoVR.Desktop;

internal readonly record struct Rgb(byte Red, byte Green, byte Blue)
{
    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        if (text is not { Length: 7 } || text[0] != '#' || !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value)) return false;
        color = new((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }
    public string Hex => $"#{Red:X2}{Green:X2}{Blue:X2}";
    public double Luminance
    {
        get
        {
            static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            return .2126 * Linear(Red) + .7152 * Linear(Green) + .0722 * Linear(Blue);
        }
    }
    public bool UseBlackText => Luminance > .179;
    public double Contrast(Rgb other) => (Math.Max(Luminance, other.Luminance) + .05) / (Math.Min(Luminance, other.Luminance) + .05);
    public Rgb ReadableOn(Rgb background)
    {
        var result = this;
        byte destination = background.Luminance < .5 ? (byte)255 : (byte)0;
        for (int attempt = 0; attempt < 40 && result.Contrast(background) < 4.5; attempt++)
        {
            byte Blend(byte channel) => (byte)Math.Round(channel * .85 + destination * .15);
            result = new(Blend(result.Red), Blend(result.Green), Blend(result.Blue));
        }
        return result;
    }
}

internal static class BackgroundFiles
{
    public const uint MaximumEdge = 1600;
    public const long MaximumFileBytes = 20 * 1024 * 1024;
    public static bool IsOwnedName(string? name) => name is { Length: 36 } && name.EndsWith(".png", StringComparison.Ordinal) && Guid.TryParseExact(name[..32], "N", out _);
    public static string Resolve(string dataDirectory, string name)
    {
        if (!IsOwnedName(name)) throw new InvalidDataException("背景图片名称无效。");
        return Path.Combine(Path.GetFullPath(dataDirectory), "backgrounds", name);
    }
    public static (uint Width, uint Height) Size(uint width, uint height, long bytes)
    {
        if (width == 0 || height == 0 || (ulong)width * height > 24_000_000 || bytes > MaximumFileBytes || bytes <= 0)
            throw new InvalidDataException("请选择不超过 20 MB、2400 万像素的图片。");
        double scale = Math.Min(1, MaximumEdge / (double)Math.Max(width, height));
        return ((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale)));
    }
    public static void Cleanup(string dataDirectory, string current, params string[] protectedImages)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current };
        keep.UnionWith(protectedImages);
        try
        {
            foreach (var name in new[] { "settings.json", "settings.json.bak" })
            {
                string path = Path.Combine(dataDirectory, name);
                if (!File.Exists(path)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind != JsonValueKind.Object) return;
                if (document.RootElement.TryGetProperty("background_image", out var image) && image.ValueKind == JsonValueKind.String) keep.Add(image.GetString() ?? "");
            }
            var folder = Path.Combine(dataDirectory, "backgrounds");
            if (!Directory.Exists(folder)) return;
            foreach (var path in Directory.EnumerateFiles(folder, "*.png", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                if (!IsOwnedName(name) || keep.Contains(name)) continue;
                try { File.Delete(path); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
    }
}
