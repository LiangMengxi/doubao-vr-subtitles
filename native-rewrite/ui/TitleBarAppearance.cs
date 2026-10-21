using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace DoubaoVR.Desktop;

internal sealed class TitleBarAppearance : IDisposable
{
    private readonly Window window;
    private readonly FrameworkElement root;
    private readonly UISettings system = new();
    private readonly AccessibilitySettings accessibility = new();
    private bool disposed;
    private readonly bool watchesHighContrast;

    public TitleBarAppearance(Window window, FrameworkElement root)
    {
        this.window = window;
        this.root = root;
        root.ActualThemeChanged += ThemeChanged;
        root.Loaded += Loaded;
        system.ColorValuesChanged += SystemChanged;
        try
        {
            accessibility.HighContrastChanged += HighContrastChanged;
            watchesHighContrast = true;
        }
        catch (COMException) { }
        Apply();
    }

    private static Color ColorOf(Rgb color) => Color.FromArgb(255, color.Red, color.Green, color.Blue);

    public void Apply()
    {
        if (disposed) return;
        bool dark = root.ActualTheme == ElementTheme.Dark;
        var palette = TitleBarPalette.ForTheme(dark);
        Color background = accessibility.HighContrast ? system.UIElementColor(UIElementType.Window) : ColorOf(palette.Background);
        Color foreground = accessibility.HighContrast ? system.UIElementColor(UIElementType.WindowText) : ColorOf(palette.Foreground);
        NativeBridge.TitleBarTheme(WinRT.Interop.WindowNative.GetWindowHandle(window), dark, background, foreground);
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var bar = window.AppWindow.TitleBar;
        bar.BackgroundColor = bar.InactiveBackgroundColor = bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = background;
        bar.ForegroundColor = bar.ButtonForegroundColor = foreground;
        bar.InactiveForegroundColor = bar.ButtonInactiveForegroundColor = accessibility.HighContrast ? foreground : ColorOf(palette.Inactive);
        bar.ButtonHoverBackgroundColor = accessibility.HighContrast ? system.UIElementColor(UIElementType.Highlight) : ColorOf(palette.Hover);
        bar.ButtonPressedBackgroundColor = accessibility.HighContrast ? system.UIElementColor(UIElementType.Highlight) : ColorOf(palette.Pressed);
        bar.ButtonHoverForegroundColor = bar.ButtonPressedForegroundColor = accessibility.HighContrast ? system.UIElementColor(UIElementType.HighlightText) : foreground;
    }

    private void ThemeChanged(FrameworkElement sender, object arguments) => Apply();
    private void Loaded(object sender, RoutedEventArgs arguments) => Apply();
    private void SystemChanged(UISettings sender, object arguments) => root.DispatcherQueue.TryEnqueue(Apply);
    private void HighContrastChanged(AccessibilitySettings sender, object arguments) => root.DispatcherQueue.TryEnqueue(Apply);

    public void Dispose()
    {
        disposed = true;
        root.ActualThemeChanged -= ThemeChanged;
        root.Loaded -= Loaded;
        system.ColorValuesChanged -= SystemChanged;
        if (watchesHighContrast) accessibility.HighContrastChanged -= HighContrastChanged;
    }
}
