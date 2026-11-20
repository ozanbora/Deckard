using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Deckard.App;

/// <summary>Lays children out left to right, starting a new line when the next one doesn't fit the width.</summary>
public sealed partial class WrapPanel : Panel
{
    public double Spacing { get; set; } = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return Lay(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Lay(finalSize.Width, arrange: true);
        return finalSize;
    }

    /// <summary>Places each child after the previous one on its line, or at the start of a new line; returns the space used.</summary>
    private Size Lay(double width, bool arrange)
    {
        double x = 0, y = 0, lineHeight = 0, used = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width)
            {
                (x, y) = (0, y + lineHeight + Spacing);
                lineHeight = 0;
            }
            if (arrange)
                child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + Spacing;
            lineHeight = Math.Max(lineHeight, size.Height);
            used = Math.Max(used, x - Spacing);
        }
        return new Size(used, y + lineHeight);
    }
}
