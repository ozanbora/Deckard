using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Deckard.App;

/// <summary>One line on an item card: a bullet, then text whose numbers are picked out unless it has its own color.</summary>
/// <param name="Wrap">Puts the drawn line into something else, e.g. <see cref="ValueEditor"/>'s click-to-correct.</param>
internal sealed record CardLine(string Bullet, Brush BulletBrush, string Text, Brush? TextBrush = null, bool Dim = false,
    Func<FrameworkElement, FrameworkElement>? Wrap = null);

/// <summary>
/// An item drawn like the maxroll planner's tooltip: name, rarity and type in the rarity color over a tinted header,
/// item power, the affix lines, then the aspect or unique power, sockets and an optional footer. Build items
/// (<see cref="PlannedItemTooltip"/>) and items read from the game (<see cref="OverlayWindow"/>) share it.
/// </summary>
internal static partial class ItemCard
{
    public const double Width = 380;

    // Diablo IV tooltip rarity colors.
    public static readonly Dictionary<string, Color> RarityColors = new()
    {
        ["Magic"] = Color.FromArgb(255, 105, 105, 255),
        ["Rare"] = Color.FromArgb(255, 255, 255, 0),
        ["Legendary"] = Color.FromArgb(255, 191, 100, 47),
        ["Unique"] = Color.FromArgb(255, 199, 179, 119),
        ["Mythic"] = Color.FromArgb(255, 166, 107, 224),
        ["Set"] = Color.FromArgb(255, 96, 214, 76),
    };

    public static readonly SolidColorBrush Text = Brush(178, 172, 162);
    public static readonly SolidColorBrush Number = Brush(240, 236, 228);
    public static readonly SolidColorBrush Bullet = Brush(120, 112, 100);
    public static readonly SolidColorBrush Greater = Brush(232, 196, 104);
    public static readonly SolidColorBrush Tempered = Brush(130, 166, 232);
    public static readonly SolidColorBrush Good = Brush(110, 200, 110);
    public static readonly SolidColorBrush Fair = Brush(230, 190, 80);
    public static readonly SolidColorBrush Bad = Brush(220, 90, 80);
    private static readonly SolidColorBrush Panel = Brush(14, 13, 12);

    // PT Serif, the font of Diablo IV's tooltips (App.xaml); its styles are separate files.
    public static FontFamily Font => (FontFamily)Application.Current.Resources["CardFont"];
    public static FontFamily BoldFont => (FontFamily)Application.Current.Resources["CardFontBold"];
    public static FontFamily ItalicFont => (FontFamily)Application.Current.Resources["CardFontItalic"];
    private static readonly SolidColorBrush Divider = Brush(52, 48, 44);

    public static SolidColorBrush RarityBrush(string? rarity) => new(RarityColor(rarity));

    /// <param name="onClose">When set, the header gets a close button that calls it.</param>
    public static Border Build(
        string name, string? rarity, string typeLine, string? info, IEnumerable<CardLine> affixes, IEnumerable<CardLine> powers,
        int sockets, UIElement? footer = null, Action? onClose = null, string? socketText = null, Func<FrameworkElement, FrameworkElement>? wrapInfo = null)
    {
        var color = RarityColor(rarity);
        var rarityBrush = new SolidColorBrush(color);

        var header = new StackPanel { Padding = new Thickness(16, 12, onClose is null ? 16 : 36, 10), Spacing = 2 };
        header.Children.Add(new TextBlock { Text = name, Foreground = rarityBrush, FontSize = 20, TextWrapping = TextWrapping.Wrap, FontFamily = BoldFont });
        header.Children.Add(new TextBlock { Text = typeLine, Foreground = rarityBrush, FontSize = 14, TextWrapping = TextWrapping.Wrap, FontFamily = Font });
        if (info is not null)
        {
            var infoText = new TextBlock { Text = info, Foreground = Number, FontSize = 14, TextWrapping = TextWrapping.Wrap, FontFamily = Font };
            header.Children.Add(wrapInfo?.Invoke(infoText) ?? infoText);
        }
        var headerGrid = new Grid
        {
            // The rarity color fading down into the panel, like the planner's header.
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(90, color.R, color.G, color.B), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0, color.R, color.G, color.B), Offset = 1 },
                },
            },
            Children = { header },
        };
        if (onClose is not null)
        {
            var close = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 11 },
                Padding = new Thickness(6), Margin = new Thickness(0, 6, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Foreground = Text,
            };
            ToolTipService.SetToolTip(close, "Close");
            close.Click += (_, _) => onClose();
            headerGrid.Children.Add(close);
        }

        var body = new StackPanel { Padding = new Thickness(16, 10, 16, 14), Spacing = 5 };
        foreach (var line in affixes)
            body.Children.Add(Wrapped(line));
        var powerLines = powers.ToList();
        if (powerLines.Count > 0)
        {
            body.Children.Add(Rule());
            foreach (var line in powerLines)
                body.Children.Add(Wrapped(line with { BulletBrush = rarityBrush, TextBrush = line.TextBrush ?? rarityBrush }));
        }
        if (sockets > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
            for (var i = 0; i < sockets; i++)
                row.Children.Add(new Ellipse { Width = 14, Height = 14, Fill = Brush(30, 28, 26), Stroke = Bullet, StrokeThickness = 1.5 });
            // Named, or the rings read as an affix line that lost its text.
            row.Children.Add(new TextBlock
            {
                Text = socketText ?? (sockets == 1 ? "1 socket" : $"{sockets} sockets"),
                FontFamily = Font, FontSize = 13, Foreground = Number, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            });
            body.Children.Add(row);
        }

        var stack = new StackPanel { Children = { headerGrid } };
        if (body.Children.Count > 0) // e.g. a planned charm without affixes: no empty band under the header
        {
            stack.Children.Add(Rule());
            stack.Children.Add(body);
        }
        if (footer is not null)
        {
            stack.Children.Add(Rule());
            stack.Children.Add(new Border { Padding = new Thickness(16, 10, 16, 14), Child = footer });
        }
        return new Border
        {
            Width = Width,
            Background = Panel,
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, color.R, color.G, color.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = stack,
        };
    }

    private static FrameworkElement Wrapped(CardLine line) => line.Wrap?.Invoke(Line(line)) ?? Line(line);

    /// <summary>A bullet, then the text with its numbers picked out ("+8.0%", "x50%", "1,225").</summary>
    public static Grid Line(CardLine line)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = line.TextBrush ?? Text, LineHeight = 20, FontFamily = line.Dim ? ItalicFont : Font };
        foreach (var part in NumberPattern().Split(line.Text).Where(p => p.Length > 0))
        {
            var run = new Run { Text = part };
            if (line.TextBrush is null && NumberPattern().IsMatch(part))
            {
                run.Foreground = Number;
                run.FontFamily = BoldFont;
            }
            block.Inlines.Add(run);
        }
        if (line.Dim)
        {
            block.Opacity = 0.5;
        }

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock { Text = line.Bullet, Foreground = line.BulletBrush, FontSize = 10, Margin = new Thickness(0, 3, 0, 0) });
        Grid.SetColumn(block, 1);
        grid.Children.Add(block);
        return grid;
    }

    public static Rectangle Rule() => new() { Height = 1, Fill = Divider, Margin = new Thickness(0, 2, 0, 2) };

    public static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));

    private static Color RarityColor(string? rarity) => rarity is not null && RarityColors.TryGetValue(rarity, out var c) ? c : Colors.White;

    [GeneratedRegex(@"([+x]?\d[\d,]*(?:\.\d+)?%?)")]
    private static partial Regex NumberPattern();
}
