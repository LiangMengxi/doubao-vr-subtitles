using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DoubaoVR.Desktop;

internal sealed partial class MainWindow
{
    private static UIElement AboutSettings()
    {
        var panel = Column(8);
        string version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        panel.Children.Add(new TextBlock { Text = $"版本：{version}", IsTextSelectionEnabled = true });
        panel.Children.Add(new HyperlinkButton
        {
            NavigateUri = new Uri("https://github.com/LiangMengxi/doubao-vr-subtitles"),
            Content = new TextBlock { Text = "https://github.com/LiangMengxi/doubao-vr-subtitles", TextWrapping = TextWrapping.Wrap },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0, 6, 0, 6)
        });
        panel.Children.Add(new TextBlock { Text = "QQ：1585759606", IsTextSelectionEnabled = true });
        return panel;
    }
}
