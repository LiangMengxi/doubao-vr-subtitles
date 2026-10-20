using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace DoubaoVR.Desktop;

internal sealed class AccentTheme : IDisposable
{
    private readonly FrameworkElement root;
    private readonly UISettings system = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly Dictionary<string, SolidColorBrush> brushes = new();
    private string preference = "";
    private bool disposed;

    public AccentTheme(FrameworkElement root)
    {
        this.root = root;
        foreach (var key in new[] { "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush", "AccentFillColorDisabledBrush",
            "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush",
            "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush", "TextOnAccentFillColorDisabledBrush",
            "AccentFillColorSelectedTextBackgroundBrush", "SystemControlHighlightAccentBrush" })
        {
            var brush = new SolidColorBrush();
            brushes.Add(key, brush);
            root.Resources[key] = brush;
            Application.Current.Resources[key] = brush;
        }
        foreach (var prefix in new[] { "AccentButtonBackground", "ToggleButtonBackgroundChecked", "ToggleSwitchFillOn", "ToggleSwitchStrokeOn", "SliderTrackValueFill", "SliderThumbBackground" })
        {
            Alias(prefix, "AccentFillColorDefaultBrush");
            Alias(prefix + "PointerOver", "AccentFillColorSecondaryBrush");
            Alias(prefix + "Pressed", "AccentFillColorTertiaryBrush");
            Alias(prefix + "Disabled", "AccentFillColorDisabledBrush");
        }
        foreach (var prefix in new[] { "AccentButtonForeground", "ToggleButtonForegroundChecked", "ToggleSwitchKnobFillOn" })
        {
            Alias(prefix, "TextOnAccentFillColorPrimaryBrush");
            Alias(prefix + "PointerOver", "TextOnAccentFillColorPrimaryBrush");
            Alias(prefix + "Pressed", "TextOnAccentFillColorSecondaryBrush");
            Alias(prefix + "Disabled", "TextOnAccentFillColorDisabledBrush");
        }
        root.ActualThemeChanged += ThemeChanged;
        system.ColorValuesChanged += SystemChanged;
        Refresh();
    }
    public Color Current
    {
        get
        {
            if (accessibility.HighContrast) return system.UIElementColor(UIElementType.Highlight);
            return Rgb.TryParse(preference, out var color) ? Color.FromArgb(255, color.Red, color.Green, color.Blue) : system.GetColorValue(UIColorType.Accent);
        }
    }
    public void Apply(string color) { preference = color; Refresh(); }
    private void Alias(string key, string brush)
    {
        root.Resources[key] = brushes[brush];
        Application.Current.Resources[key] = brushes[brush];
    }
    private void Refresh()
    {
        if (disposed) return;
        Color accent = Current;
        var rgb = new Rgb(accent.R, accent.G, accent.B);
        Color text = accessibility.HighContrast ? system.UIElementColor(UIElementType.HighlightText) : rgb.UseBlackText ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255);
        var readable = rgb.ReadableOn(root.ActualTheme == ElementTheme.Dark ? new Rgb(32, 32, 32) : new Rgb(255, 255, 255));
        foreach (var entry in brushes)
        {
            Color value = entry.Key.StartsWith("TextOnAccent", StringComparison.Ordinal) ? text : accent;
            if (!accessibility.HighContrast && entry.Key.StartsWith("AccentText", StringComparison.Ordinal)) value = Color.FromArgb(255, readable.Red, readable.Green, readable.Blue);
            if (!accessibility.HighContrast)
            {
                if (entry.Key.Contains("Secondary", StringComparison.Ordinal)) value.A = 230;
                else if (entry.Key.Contains("Tertiary", StringComparison.Ordinal)) value.A = 204;
                else if (entry.Key.Contains("Disabled", StringComparison.Ordinal)) value.A = 100;
            }
            entry.Value.Color = value;
        }
    }
    private void ThemeChanged(FrameworkElement sender, object arguments) => Refresh();
    private void SystemChanged(UISettings sender, object arguments) => root.DispatcherQueue.TryEnqueue(Refresh);
    public void Dispose()
    {
        disposed = true;
        root.ActualThemeChanged -= ThemeChanged;
        system.ColorValuesChanged -= SystemChanged;
    }
}
