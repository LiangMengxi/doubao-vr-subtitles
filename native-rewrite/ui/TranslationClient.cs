using System.Text.Json;

namespace DoubaoVR.Desktop;

internal static class TranslationClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string clean = text.Trim();

        string targetPair = targetLanguage.ToLowerInvariant() switch
        {
            "ja" or "japanese" or "日文" or "日语" => "zh|ja",
            "ko" or "korean" or "韩文" or "韩语" => "zh|ko",
            _ => "zh|en"
        };

        try
        {
            string url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(clean)}&langpair={targetPair}";
            using var response = await Http.GetAsync(url, cancellation);
            if (!response.IsSuccessStatusCode) return "";
            string json = await response.Content.ReadAsStringAsync(cancellation);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("responseData", out var responseData)
                && responseData.TryGetProperty("translatedText", out var translatedText))
            {
                string result = translatedText.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(result) && !result.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
                {
                    return result.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[Translation] MyMemory failed: {ex.Message}");
        }

        return "";
    }
}
