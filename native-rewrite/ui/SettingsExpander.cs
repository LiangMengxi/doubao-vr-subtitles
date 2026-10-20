using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace DoubaoVR.Desktop;

public sealed class SettingsExpander : Expander
{
    private readonly UISettings motionSettings = new();
    private RevealPanel? viewport;
    private Border? body;
    private ToggleButton? header;
    private Storyboard? motion;
    private double targetHeight;
    internal double RevealedHeight => viewport?.RevealHeight ?? 0;

    public SettingsExpander()
    {
        Expanding += (_, _) => UpdateReveal(true);
        Collapsed += (_, _) => UpdateReveal(true);
        Loaded += (_, _) => UpdateReveal(false);
        Unloaded += (_, _) => { StopMotion(); UpdateReveal(false); };
    }

    protected override void OnApplyTemplate()
    {
        StopMotion();
        if (body != null) body.SizeChanged -= BodySizeChanged;
        base.OnApplyTemplate();
        viewport = GetTemplateChild("RevealViewport") as RevealPanel;
        body = GetTemplateChild("ExpanderContent") as Border;
        header = GetTemplateChild("ExpanderHeader") as ToggleButton;
        if (body != null) body.SizeChanged += BodySizeChanged;
        UpdateReveal(false);
    }

    private void BodySizeChanged(object sender, SizeChangedEventArgs arguments)
    {
        if (IsExpanded) UpdateReveal(motion != null);
    }

    private void StopMotion()
    {
        double current = RevealedHeight;
        var previous = motion;
        motion = null;
        previous?.Stop();
        if (viewport != null) viewport.RevealHeight = current;
    }

    private void UpdateReveal(bool animate)
    {
        if (viewport == null || body == null) return;
        bool expanded = IsExpanded;
        if (expanded) body.Visibility = Visibility.Visible;
        else if (XamlRoot != null)
        {
            var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (focused != null && focused != body) focused = VisualTreeHelper.GetParent(focused);
            if (focused == body) header?.Focus(FocusState.Programmatic);
        }
        body.IsHitTestVisible = expanded;
        double width = viewport.ActualWidth > 0 ? viewport.ActualWidth : ActualWidth;
        if (width <= 0) return;
        double natural = viewport.MeasureContent(width).Height;
        double destination = expanded ? natural : 0;
        if (motion != null && Math.Abs(targetHeight - destination) < .5) return;
        StopMotion();
        double current = viewport.RevealHeight;
        targetHeight = destination;
        if (!animate || !IsLoaded || !motionSettings.AnimationsEnabled || Math.Abs(current - destination) < .5)
        {
            viewport.RevealHeight = destination;
            if (!expanded) body.Visibility = Visibility.Collapsed;
            return;
        }
        var storyboard = new Storyboard();
        var height = new DoubleAnimation
        {
            From = current, To = destination, EnableDependentAnimation = true,
            Duration = new Duration(TimeSpan.FromMilliseconds(Math.Clamp(180 * Math.Abs(destination - current) / Math.Max(natural, 1), 60, 180))),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(height, viewport);
        Storyboard.SetTargetProperty(height, nameof(RevealPanel.RevealHeight));
        storyboard.Children.Add(height);
        storyboard.Completed += (_, _) =>
        {
            if (motion != storyboard) return;
            StopMotion();
            viewport.RevealHeight = destination;
            if (!IsExpanded)
            {
                viewport.UpdateLayout();
                body.Visibility = Visibility.Collapsed;
            }
        };
        motion = storyboard;
        storyboard.Begin();
    }
}
