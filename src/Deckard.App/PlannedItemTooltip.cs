using Deckard.Core.Builds;
using Deckard.Core.Items;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Deckard.App;

/// <summary>
/// A build item's tooltip: an <see cref="ItemCard"/> with the planner's rolled values, greater affixes starred,
/// tempered affixes, then the aspect or unique power and the sockets.
/// </summary>
internal static class PlannedItemTooltip
{
    /// <summary>The item's rarity: from a mythic or unique power among its affixes, the item's own rarity, or its aspect.</summary>
    public static string Rarity(PlannedItem item, ItemInfo? info, AffixCatalog catalog)
    {
        var magic = item.Affixes.Select(a => catalog.AffixText.GetValueOrDefault(a.Id)?.MagicType ?? 0).DefaultIfEmpty().Max();
        // The item's own rarity names mythics, uniques and set charms; "legendary" bases also carry rare builds (the planner's
        // "Rare Helm" on Helm_Legendary_...), so only an aspect makes an item legendary. With no affixes at all it's plain,
        // as the planner shows a bare "Legendary Horadric Seal".
        var own = info?.MagicType ?? 0;
        return magic == 4 || own == 4 ? "Mythic"
            : magic == 2 || own == 2 || info?.Unique == true ? "Unique"
            : own == 3 || info?.Set is not null ? "Set"
            : item.Aspects.Count > 0 ? "Legendary"
            : item.Affixes.Count + item.Tempered.Count > 0 ? "Rare" : "Common";
    }

    /// <param name="talisman">The build's seal and charms: with it, a set charm's card counts the set's pieces the build has.</param>
    public static ToolTip Create(PlannedItem item, ItemInfo? info, AffixCatalog catalog, IEnumerable<PlannedItem>? talisman = null)
    {
        var explicits = item.Affixes.Select(a => (Affix: a, Text: catalog.AffixText.GetValueOrDefault(a.Id))).ToList();
        var rarity = Rarity(item, info, catalog);
        // Roll ranges as the game's advanced tooltips show them: not on charms and the seal (slots 20 and up), whose
        // planned rolls don't match the base formulas, and doubled on two-handed weapons.
        var ranges = item.Slot < 20 ? new Ranges(item.Power, info?.TwoHanded == true ? 2 : 1) : null;

        // Unique and mythic powers come with the explicits; they go below the affixes like the aspect.
        var affixes = explicits.Where(e => e.Text?.MagicType is not (2 or 4))
            .Select(e => Line(e.Affix, e.Text, ranges, e.Affix.Greater ? "✦" : "◆", e.Affix.Greater ? ItemCard.Greater : ItemCard.Bullet, e.Affix.Greater ? ItemCard.Greater : null))
            .Concat(item.Tempered.Select(a => Line(a, catalog.AffixText.GetValueOrDefault(a.Id), ranges, "◈", ItemCard.Tempered, ItemCard.Tempered)));
        var powers = item.Aspects.Select(a => (Affix: a, Text: catalog.AffixText.GetValueOrDefault(a.Id)))
            .Concat(explicits.Where(e => e.Text?.MagicType is 2 or 4))
            .Select(e => Line(e.Affix, e.Text, ranges, "◆", ItemCard.Bullet, null));

        var card = ItemCard.Build(
            info?.Name ?? item.ItemKey, rarity, info is null ? rarity : $"{rarity} {info.Type}",
            item.Power > 0 ? $"{item.Power} Item Power" : null, affixes, powers, item.Sockets?.Count ?? 0,
            footer: info?.Set is { } set ? SetPanel(set, talisman ?? [item], catalog) : null,
            socketText: item.Sockets is { Count: > 0 } sockets ? BuildSheetView.SocketText(sockets, catalog) : null);
        return new ToolTip
        {
            Content = card, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent), MaxWidth = ItemCard.Width + 8,
        };
    }

    /// <summary>
    /// A set charm's set, as the game's tooltip shows it: its pieces (the build's in the set's colour), how many the
    /// build has, and each bonus by the pieces it takes, lit once the build has them.
    /// </summary>
    private static StackPanel SetPanel(string set, IEnumerable<PlannedItem> talisman, AffixCatalog catalog)
    {
        var green = ItemCard.RarityBrush("Set");
        // The pieces in the game's order (their keys end _01 … _05); names, since a build may hold one twice.
        var pieces = catalog.Items.Where(i => i.Value.Set == set).OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => i.Value.Name).Distinct().ToList();
        var worn = talisman.Select(t => catalog.Items.GetValueOrDefault(t.ItemKey)).OfType<ItemInfo>().ToList();
        var owned = worn.Where(i => i.Set == set).Select(i => i.Name).ToHashSet();
        var discount = worn.Select(i => i.SetDiscount).DefaultIfEmpty().Max(); // the seal's, e.g. Seal of the Diamond Mind's 1
        var panel = new StackPanel { Spacing = 4 };
        TextBlock Text(string text, Brush brush, double size = 14, double indent = 0) => new()
        {
            Text = text, Foreground = brush, FontFamily = ItemCard.Font, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(indent, 0, 0, 0),
        };
        panel.Children.Add(new TextBlock { Text = set, Foreground = green, FontFamily = ItemCard.BoldFont, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        foreach (var piece in pieces)
            panel.Children.Add(Text("→ " + piece, owned.Contains(piece) ? green : ItemCard.Bullet, 13, 10));
        panel.Children.Add(Text($"{set} ({owned.Count}/{pieces.Count})", green, 14));
        foreach (var bonus in catalog.Entries.Where(e => e.Kind == AffixKind.SetBonus && e.Name == set).OrderBy(e => e.Required))
        {
            var needed = bonus.Required <= 2 ? bonus.Required : Math.Max(2, bonus.Required - discount);
            var active = owned.Count >= needed;
            panel.Children.Add(Text(needed < bonus.Required ? $"({bonus.Required}) Set ({needed} with your seal):" : $"({bonus.Required}) Set:",
                active ? green : ItemCard.Bullet, 13));
            foreach (var line in bonus.Lines)
                panel.Children.Add(ItemCard.Line(new CardLine("◆", active ? green : ItemCard.Bullet, line, active ? null : ItemCard.Bullet)));
        }
        return panel;
    }

    private sealed record Ranges(int ItemPower, double Scale);

    /// <summary>The build's text for an affix with the planner's values filled in, and roll ranges when given (never on
    /// greater affixes, which the game shows without one).</summary>
    public static string Text(PlannedAffix affix, AffixText? text) => Text(affix, text, null);

    private static string Text(PlannedAffix affix, AffixText? text, Ranges? ranges) =>
        text is null
            ? $"Affix {affix.Id} (not in this season's data)"
            : string.Join("\n", ranges is null || affix.Greater
                ? text.Fill(affix.Values ?? [])
                : text.Fill(affix.Values ?? [], ranges.ItemPower, ranges.Scale));

    private static CardLine Line(PlannedAffix affix, AffixText? text, Ranges? ranges, string bullet, Brush bulletBrush, Brush? textBrush) =>
        new(bullet, bulletBrush, Text(affix, text, ranges), textBrush, Dim: text is null);
}
