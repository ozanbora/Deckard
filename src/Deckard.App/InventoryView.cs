using Deckard.Core.Builds;
using Deckard.Core.Items;
using Deckard.Perception.Builds;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Deckard.App;

/// <summary>
/// The build's items laid out like Diablo II: Resurrected's character screen: weapons tall on the left and right,
/// helm and amulet on top, chest in the middle, gloves, rings, pants (where D2R has the belt) and boots below.
/// Extra weapons (Barbarian two-handers, Rogue ranged) get their own columns outside the hands.
/// Hover a slot for the item's name, aspects and affixes.
/// </summary>
public sealed partial class InventoryView : UserControl
{
    private const double Cell = 44;           // one inventory cell, in pixels
    private const double Gap = 4;

    // Weapon slots by preference for the left and right hand: one-hand/off-hand, dual-wield, two-handed, ranged.
    private static readonly int[] LeftHand = [7, 11, 8, 10];
    private static readonly int[] RightHand = [6, 12, 9];
    private static readonly int[] OuterLeft = [8, 10]; // two-handed bludgeoning, ranged
    private static readonly int[] OuterRight = [9];    // two-handed slashing

    // Doll slots in cells: x, y, width, height (D2R's equipment panel), spaced so each item's name (up to three lines,
    // 0.85 cells) fits below it without running into the next item.
    private static readonly (int Slot, double X, double Y, int W, int H)[] Doll =
    [
        (4, 3.8, 0, 2, 2),       // helm
        (18, 6.6, 0.3, 1, 1),    // amulet, above the right hand
        (5, 3.8, 2.9, 2, 3),     // chest
        (13, 0, 7.3, 2, 2),      // gloves
        (16, 2.4, 7.3, 1, 1),    // ring
        (14, 3.8, 7.3, 2, 2),    // pants
        (17, 6.2, 7.3, 1, 1),    // ring
        (15, 7.6, 7.3, 2, 2),    // boots
    ];
    private const double LeftHandX = 1.2, RightHandX = 6.4, HandY = 2.3;
    private const double DollWidth = 9.6, DollHeight = 10.2; // the bottom row's names (3 lines) end at 10.15
    private const double NameWidthSmall = 1.8; // a 1-cell item's name (ring, amulet): the whole gap between its neighbours

    // A translucent gray marks empty slots on the surrounding panel.
    private static readonly SolidColorBrush SlotFill = new(Color.FromArgb(40, 128, 128, 128));
    private static readonly SolidColorBrush Transparent = new(Microsoft.UI.Colors.Transparent);
    private static readonly SolidColorBrush Bronze = Brush(150, 118, 74);

    private readonly Canvas _canvas = new();

    // The screen scales to fill the tab, so items grow with the window.
    public InventoryView() => Content = new Border
    {
        Background = Transparent,
        Padding = new Thickness(16),
        Child = new Viewbox { Child = _canvas, Stretch = Stretch.Uniform },
    };

    public void Clear() => _canvas.Children.Clear();

    private IReadOnlyDictionary<int, (WornItem Item, ItemComparison Fit)> _worn = new Dictionary<int, (WornItem, ItemComparison)>();

    /// <param name="worn">The player's worn items by build slot (<see cref="Gear.Assign"/>): each gets a badge for how close it is.</param>
    public void Show(BuildVariant variant, AffixCatalog catalog, IReadOnlyDictionary<int, (WornItem Item, ItemComparison Fit)>? worn = null)
    {
        _canvas.Children.Clear();
        _worn = worn ?? new Dictionary<int, (WornItem, ItemComparison)>();
        var items = variant.Items;
        var left = LeftHand.FirstOrDefault(items.ContainsKey);
        var right = RightHand.FirstOrDefault(items.ContainsKey);

        // Barbarian and Rogue carry more weapons than two hands hold: each extra gets a tall column beside the doll.
        var leftExtra = OuterLeft.FirstOrDefault(s => s != left && items.ContainsKey(s));
        var rightExtra = OuterRight.FirstOrDefault(s => s != right && items.ContainsKey(s));
        var dx = leftExtra != 0 ? 2.4 - LeftHandX : 0;
        if (leftExtra != 0)
            Place(items[leftExtra], catalog, 0, HandY, 2, 4);
        if (rightExtra != 0)
            Place(items[rightExtra], catalog, dx + RightHandX + 2.4, HandY, 2, 4);

        Place(items.GetValueOrDefault(left), catalog, dx + LeftHandX, HandY, 2, 4);
        Place(items.GetValueOrDefault(right), catalog, dx + RightHandX, HandY, 2, 4);
        foreach (var (slot, x, y, w, h) in Doll)
            Place(items.GetValueOrDefault(slot), catalog, dx + x, y, w, h);

        _canvas.Width = (dx + (rightExtra != 0 ? RightHandX + 4.4 : DollWidth)) * (Cell + Gap);
        _canvas.Height = DollHeight * (Cell + Gap);
    }

    /// <summary>A slot at cell position (x, y), w by h cells: the item's icon, or a dim empty slot.</summary>
    private void Place(PlannedItem? item, AffixCatalog catalog, double x, double y, int w, int h)
    {
        var info = item is null ? null : catalog.Items.GetValueOrDefault(item.ItemKey);
        var slot = new Border
        {
            Width = w * Cell + (w - 1) * Gap,
            Height = h * Cell + (h - 1) * Gap,
            Background = item is null ? SlotFill : Transparent, // transparent still takes the hover for the tooltip
            Padding = new Thickness(2),
        };
        if (item is not null)
        {
            if (info is { Icon: not 0 })
            {
                var image = new Image { Stretch = Stretch.Uniform };
                _ = LoadAsync(image, info.Icon);
                slot.Child = image;
            }
            else
            {
                slot.Child = new TextBlock { Text = info?.Name ?? item.ItemKey, FontSize = 10, TextWrapping = TextWrapping.Wrap, Foreground = Bronze };
            }
            ToolTipService.SetToolTip(slot, PlannedItemTooltip.Create(item, info, catalog));
            // The item's name below it, in its rarity's colour, as the game's character screen labels slots.
            var nameWidth = (w == 1 ? NameWidthSmall : w) * (Cell + Gap) - Gap;
            Add(new TextBlock
            {
                Text = info?.Name ?? item.ItemKey, Width = nameWidth, MaxLines = 3, FontSize = 10, LineHeight = 12,
                FontFamily = ItemCard.BoldFont, Foreground = ItemCard.RarityBrush(PlannedItemTooltip.Rarity(item, info, catalog)),
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.WrapWholeWords, TextTrimming = TextTrimming.CharacterEllipsis,
            }, x * (Cell + Gap) + (slot.Width - nameWidth) / 2, y * (Cell + Gap) + slot.Height + 3);
        }
        Add(slot, x * (Cell + Gap), y * (Cell + Gap));
        if (item is not null && _worn.TryGetValue(item.Slot, out var worn) && worn.Fit.Verdict is var (verdict, reason))
            Add(Badge(verdict, $"You wear: {worn.Item.Name ?? worn.Item.ItemType}\n{reason}"), x * (Cell + Gap) + slot.Width - 12, y * (Cell + Gap) - 4);
    }

    /// <summary>How close the worn item is to the build's: ✓ it is the build's, ◐ craft it there, ✗ replace it.</summary>
    private static Border Badge(ItemVerdict verdict, string tooltip)
    {
        var (glyph, brush) = verdict switch
        {
            ItemVerdict.Keep => ("✓", ItemCard.Good),
            ItemVerdict.Craft => ("◐", ItemCard.Fair),
            _ => ("✗", ItemCard.Bad),
        };
        var badge = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = brush,
            Child = new TextBlock { Text = glyph, FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(255, 14, 13, 12)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        ToolTipService.SetToolTip(badge, tooltip);
        return badge;
    }

    private void Add(FrameworkElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        _canvas.Children.Add(element);
    }

    private static async Task LoadAsync(Image image, long id) => image.Source = await IconCache.GetAsync(id);

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));
}
