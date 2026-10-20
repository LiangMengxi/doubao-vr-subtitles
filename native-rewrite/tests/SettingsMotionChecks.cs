using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace DoubaoVR.Desktop;

internal sealed class SettingsMotionChecks : Window
{
    private readonly StackPanel panel = new() { Spacing = 12, Margin = new Thickness(20) };
    private readonly List<SettingsExpander> sections = new();

    internal SettingsMotionChecks()
    {
        Title = "豆包VR字幕";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(590, 740));
        foreach (var entry in new[] { ("音频设备", 220d), ("主题与背景", 350d), ("字幕捕获", 280d), ("程序设置", 160d) })
        {
            var section = new SettingsExpander
            {
                Style = (Style)Application.Current.Resources["SettingsExpanderStyle"],
                Header = entry.Item1,
                Content = new Grid { Height = entry.Item2, Children = { new Button { Content = "设置", VerticalAlignment = VerticalAlignment.Top } } }
            };
            sections.Add(section);
            panel.Children.Add(section);
        }
        Content = new ScrollViewer { Content = panel };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static FontIcon? Chevron(DependencyObject parent)
    {
        if (parent is FontIcon icon && icon.Glyph == "\uE70D") return icon;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var found = Chevron(VisualTreeHelper.GetChild(parent, index));
            if (found != null) return found;
        }
        return null;
    }

    private async Task CheckMotionAsync(bool expanded)
    {
        var section = sections[0];
        var follower = sections[1];
        var samples = new List<(double Height, double Gap)>();
        void Sample(object? sender, object arguments)
        {
            double gap = follower.TransformToVisual(panel).TransformPoint(new Point()).Y
                - section.TransformToVisual(panel).TransformPoint(new Point()).Y - section.ActualHeight;
            samples.Add((section.ActualHeight, gap));
        }
        double before = section.ActualHeight;
        panel.LayoutUpdated += Sample;
        try
        {
            section.IsExpanded = expanded;
            await Task.Delay(320);
            panel.UpdateLayout();
        }
        finally { panel.LayoutUpdated -= Sample; }
        double after = section.ActualHeight;
        Require(Chevron(section)?.RenderTransform is RotateTransform rotation && rotation.Angle == (expanded ? 180 : 0), "Chevron direction must match the expanded state.");
        var intermediate = samples.Where(sample => sample.Height > Math.Min(before, after) + 2 && sample.Height < Math.Max(before, after) - 2).ToList();
        Require(Math.Abs(after - before) > 100, "The motion test must change the section height.");
        Require(samples.All(sample => Math.Abs(sample.Gap - panel.Spacing) <= 2), "Following sections must stay attached to the changing section height in every layout frame.");
        if (new UISettings().AnimationsEnabled)
            Require(intermediate.Count >= 2, $"Expected continuous {(expanded ? "expansion" : "collapse")}, got {intermediate.Count} intermediate layout frames.");
        Log.Write($"PASS: synchronized {(expanded ? "expansion" : "collapse")}; {intermediate.Count} intermediate layout frames, maximum gap error {samples.Select(sample => Math.Abs(sample.Gap - panel.Spacing)).DefaultIfEmpty().Max():F2}px.");
    }

    internal async Task RunAsync()
    {
        try
        {
            await Task.Delay(200);
            panel.UpdateLayout();
            double closed = sections[0].ActualHeight;
            await CheckMotionAsync(true);
            await CheckMotionAsync(false);
            Require(Math.Abs(sections[0].ActualHeight - closed) < 1, "Collapse must return to the original header height.");
            sections[0].IsExpanded = true;
            await Task.Delay(65);
            double interrupted = sections[0].RevealedHeight;
            sections[0].IsExpanded = false;
            Require(Math.Abs(sections[0].RevealedHeight - interrupted) < 1, "Reversing an animation must continue from the current height without snapping.");
            await Task.Delay(240);
            Require(sections[0].RevealedHeight == 0, "An interrupted expansion must close fully.");
            foreach (var section in sections)
            {
                foreach (var other in sections) if (other != section) other.IsExpanded = false;
                section.IsExpanded = true;
                await Task.Delay(50);
            }
            await Task.Delay(260);
            Require(sections.Take(3).All(section => section.RevealedHeight == 0) && sections[3].RevealedHeight > 100, "Rapid section changes must not leave stale animations or reserved space.");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 600));
            await Task.Delay(200);
            sections[3].IsExpanded = false;
            await Task.Delay(240);
            Require(sections.All(section => section.RevealedHeight == 0), "Resizing must not prevent a section from closing.");
            Log.Write("PASS: settings motion interruption, rapid section switching, resize and terminal heights; no user preferences or audio defaults were modified.");
        }
        catch (Exception exception) { Environment.ExitCode = 1; Log.Write("FAIL: settings motion checks: " + exception); }
        finally { Close(); }
    }
}
