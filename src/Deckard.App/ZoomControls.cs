using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Deckard.App;

/// <summary>
/// Zoom in, zoom out and fit-to-window buttons in the corner of a zoomable view (Skill tree, Paragon), for players who
/// don't zoom with Ctrl and the mouse wheel. Zooming keeps the middle of the view in place.
/// </summary>
internal static class ZoomControls
{
    /// <summary>The scroll viewer with the buttons over its bottom-right corner; <paramref name="content"/> is what it zooms.</summary>
    public static Grid Wrap(ScrollViewer scroll, FrameworkElement content)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
        };
        buttons.Children.Add(Button("", "Zoom in", () => ZoomBy(scroll, 1.25f)));
        buttons.Children.Add(Button("", "Zoom out", () => ZoomBy(scroll, 0.8f)));
        buttons.Children.Add(Button("", "Fit to window", () => Fit(scroll, content)));
        return new Grid { Children = { scroll, buttons } };
    }

    private static Button Button(string glyph, string name, Action click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Padding = new Thickness(8), Opacity = 0.9 };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private static void ZoomBy(ScrollViewer scroll, float factor)
    {
        var zoom = Math.Clamp(scroll.ZoomFactor * factor, scroll.MinZoomFactor, scroll.MaxZoomFactor);
        // The point in the middle of the view, in content units, stays in the middle.
        var centerX = (scroll.HorizontalOffset + scroll.ViewportWidth / 2) / scroll.ZoomFactor;
        var centerY = (scroll.VerticalOffset + scroll.ViewportHeight / 2) / scroll.ZoomFactor;
        scroll.ChangeView(Math.Max(0, centerX * zoom - scroll.ViewportWidth / 2), Math.Max(0, centerY * zoom - scroll.ViewportHeight / 2), zoom);
    }

    private static void Fit(ScrollViewer scroll, FrameworkElement content)
    {
        if (content.Width is not > 0 || content.Height is not > 0)
            return; // nothing drawn yet
        var zoom = (float)Math.Clamp(Math.Min(scroll.ViewportWidth / content.Width, scroll.ViewportHeight / content.Height), scroll.MinZoomFactor, scroll.MaxZoomFactor);
        scroll.ChangeView(0, 0, zoom);
    }
}
