using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DoubaoVR.Desktop;

public sealed class RevealPanel : Panel
{
    public static readonly DependencyProperty RevealHeightProperty = DependencyProperty.Register(
        nameof(RevealHeight), typeof(double), typeof(RevealPanel), new PropertyMetadata(0d, HeightChanged));
    private readonly RectangleGeometry viewport = new();
    internal double NaturalHeight { get; private set; }

    public RevealPanel() => Clip = viewport;

    public double RevealHeight
    {
        get => (double)GetValue(RevealHeightProperty);
        set => SetValue(RevealHeightProperty, value);
    }

    private static void HeightChanged(DependencyObject sender, DependencyPropertyChangedEventArgs arguments) => ((RevealPanel)sender).InvalidateMeasure();

    internal Size MeasureContent(double width)
    {
        if (Children.Count == 0) { NaturalHeight = 0; return new Size(); }
        var child = Children[0];
        child.Measure(new Size(Math.Max(0, width), double.PositiveInfinity));
        NaturalHeight = child.DesiredSize.Height;
        return child.DesiredSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var content = MeasureContent(availableSize.Width);
        return new Size(content.Width, Math.Clamp(RevealHeight, 0, NaturalHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count > 0) Children[0].Arrange(new Rect(0, 0, finalSize.Width, NaturalHeight));
        viewport.Rect = new Rect(0, 0, finalSize.Width, finalSize.Height);
        return finalSize;
    }
}
