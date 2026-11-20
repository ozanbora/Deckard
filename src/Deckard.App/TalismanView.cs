using Deckard.Core.Builds;
using Deckard.Core.Items;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Deckard.App;

/// <summary>
/// The build's talisman, laid out like the maxroll planner's: the seal in the middle of a round frame with the six
/// charms in shield-shaped slots around it, and the seal and charms by name to its right. Hovering one shows its card, a set
/// charm's with its set and the bonuses the build's charms light. The frame is drawn here, not maxroll's artwork.
/// </summary>
public sealed partial class TalismanView : UserControl
{
    private const int SealSlot = 20; // 21-26 are the charms
    private const double Size = 360, CharmRadius = 122;

    // Where each charm slot sits on the frame, in degrees clockwise from the right, as maxroll (and the game) lays
    // them out: top, bottom right, bottom left, top left, top right, bottom.
    private static readonly int[] CharmAngles = [-90, 30, 150, 210, -30, 90];

    private static readonly SolidColorBrush Bronze = new(Color.FromArgb(255, 176, 132, 64));
    private static readonly SolidColorBrush DarkBronze = new(Color.FromArgb(255, 96, 70, 34));
    private static readonly SolidColorBrush Socket = new(Color.FromArgb(255, 24, 21, 18));

    private readonly StackPanel _page = new() { Spacing = 10, Padding = new Thickness(28, 16, 28, 24), HorizontalAlignment = HorizontalAlignment.Left };

    public TalismanView() => Content = new ScrollViewer { Content = _page };

    public void Clear() => _page.Children.Clear();

    public void Show(BuildVariant variant, AffixCatalog catalog)
    {
        Clear();
        _page.Children.Add(BuildSheetView.Heading("Talisman"));
        var talisman = variant.Items.Where(i => i.Key >= SealSlot).OrderBy(i => i.Key).Select(i => i.Value).ToList();
        if (talisman.Count == 0)
        {
            _page.Children.Add(new TextBlock
            {
                Text = "This build has no seal or charms.", FontFamily = ItemCard.ItalicFont, FontSize = 14, Foreground = ItemCard.Bullet,
            });
            return;
        }
        // The frame, and the seal and charms by name beside it.
        var list = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center, MinWidth = 280 };
        foreach (var item in talisman)
            list.Children.Add(Row(item, catalog, talisman));
        _page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 40, Children = { Frame(variant.Items, catalog, talisman), list } });
    }

    /// <summary>The round frame: the seal in the middle, the charms around it where the game puts each slot.</summary>
    private static Canvas Frame(IReadOnlyDictionary<int, PlannedItem> items, AffixCatalog catalog, List<PlannedItem> talisman)
    {
        var canvas = new Canvas { Width = Size, Height = Size, Margin = new Thickness(0, 4, 0, 8) };
        void Place(FrameworkElement e, double x, double y)
        {
            Canvas.SetLeft(e, x - e.Width / 2);
            Canvas.SetTop(e, y - e.Height / 2);
            canvas.Children.Add(e);
        }
        const double c = Size / 2;
        Place(new Ellipse
        {
            Width = Size - 4, Height = Size - 4, StrokeThickness = 10, Stroke = Bronze,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(255, 58, 44, 28), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(255, 16, 14, 12), Offset = 1 },
                },
            },
        }, c, c);
        Place(new Ellipse { Width = Size - 24, Height = Size - 24, StrokeThickness = 2, Stroke = DarkBronze }, c, c);
        Place(new Ellipse { Width = CharmRadius * 2, Height = CharmRadius * 2, StrokeThickness = 3, Stroke = DarkBronze }, c, c);

        var seal = items.GetValueOrDefault(SealSlot);
        var sealSlot = new Grid { Width = 104, Height = 104 };
        sealSlot.Children.Add(new Ellipse { Fill = Socket, Stroke = Bronze, StrokeThickness = 4 });
        if (seal is not null)
            Fill(sealSlot, seal, catalog, talisman, 72);
        Place(sealSlot, c, c);

        for (var i = 0; i < 6; i++)
        {
            var angle = CharmAngles[i] * Math.PI / 180;
            // A shield: square top, rounded point at the bottom.
            var slot = new Border
            {
                Width = 70, Height = 80, Background = Socket, BorderBrush = Bronze, BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(8, 8, 34, 34),
            };
            var inside = new Grid();
            slot.Child = inside;
            if (items.GetValueOrDefault(SealSlot + 1 + i) is { } charm)
                Fill(inside, charm, catalog, talisman, 52);
            else
                slot.Opacity = 0.45; // a slot the build leaves empty
            Place(slot, c + CharmRadius * Math.Cos(angle), c + CharmRadius * Math.Sin(angle));
        }
        return canvas;
    }

    /// <summary>An item's icon in its slot, with its card on hover.</summary>
    private static void Fill(Grid slot, PlannedItem item, AffixCatalog catalog, List<PlannedItem> talisman, double size)
    {
        var info = catalog.Items.GetValueOrDefault(item.ItemKey);
        var icon = new Image { Width = size, Height = size, Stretch = Stretch.Uniform };
        if (info is { Icon: not 0 })
            _ = LoadAsync(icon, info.Icon);
        slot.Children.Add(icon);
        slot.Background ??= new SolidColorBrush(Colors.Transparent); // hoverable over its whole slot
        ToolTipService.SetToolTip(slot, PlannedItemTooltip.Create(item, info, catalog, talisman));
    }

    /// <summary>One line of the list beside the frame: the icon and the name in its rarity's colour.</summary>
    private static Grid Row(PlannedItem item, AffixCatalog catalog, List<PlannedItem> talisman)
    {
        var info = catalog.Items.GetValueOrDefault(item.ItemKey);
        var icon = new Image { Width = 28, Height = 28, Stretch = Stretch.Uniform };
        if (info is { Icon: not 0 })
            _ = LoadAsync(icon, info.Icon);
        var name = new TextBlock
        {
            Text = info?.Name ?? item.ItemKey, FontFamily = ItemCard.BoldFont, FontSize = 15, VerticalAlignment = VerticalAlignment.Center,
            Foreground = ItemCard.RarityBrush(PlannedItemTooltip.Rarity(item, info, catalog)), TextWrapping = TextWrapping.Wrap,
        };
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 2, 0, 2), Background = new SolidColorBrush(Colors.Transparent) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(icon);
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        ToolTipService.SetToolTip(row, PlannedItemTooltip.Create(item, info, catalog, talisman));
        return row;
    }

    private static async Task LoadAsync(Image image, long id) => image.Source = await IconCache.GetAsync(id);
}
