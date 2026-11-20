using Deckard.Core.Builds;
using Deckard.Core.Items;
using Deckard.Perception.Builds;
using Deckard.Perception.Tooltips;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deckard.App;

/// <summary>
/// The Stash page: a sorting session in which every item the player hovers in game is checked against every build in
/// their library, and listed under keep (for which build), craft (and how), sell (with a price check) or salvage.
/// Only reads tooltips like the rest of Deckard; the player moves the items by hand. The session lives until Deckard
/// closes or the player clears it.
/// </summary>
public sealed partial class StashView : UserControl
{
    private readonly Button _start = new() { Content = "Start sorting", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    private readonly Button _clear = new() { Content = "Clear" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = ItemCard.Text, FontSize = 14 };
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly List<(ItemTooltip Item, WornItem Signature, StashVerdict Verdict)> _items = [];
    private IReadOnlyList<(string Name, BuildVariant Variant)> _builds = [];
    private AffixCatalog? _catalog;
    private bool _sorting;
    private readonly CheckBox _flaggedOnly = new() { Content = "Only items with values that look misread", FontSize = 13 };
    private readonly HashSet<string> _open = []; // items whose values are shown, by ReadCorrections.ItemKey

    /// <summary>The player corrected a value of an item in the list: for the app to keep (the list has re-sorted it already).</summary>
    public event Action<ItemTooltip>? Corrected;

    /// <summary>The player started a session: the page asks for the library's builds (loading their plans).</summary>
    public event Func<Task>? Starting;

    public StashView()
    {
        _start.Click += async (_, _) =>
        {
            _sorting = !_sorting;
            if (_sorting && Starting is { } starting)
                await starting();
            Render();
        };
        _flaggedOnly.Checked += (_, _) => Render();
        _flaggedOnly.Unchecked += (_, _) => Render();
        _clear.Click += (_, _) =>
        {
            _items.Clear();
            Render();
        };
        var intro = new TextBlock
        {
            Text = "Start sorting, then hover each item in your stash or inventory in game. Deckard checks it against every build in your library "
                + "and says what to keep, what to craft, what's worth selling and what to salvage. You move the items yourself.",
            TextWrapping = TextWrapping.Wrap, FontFamily = ItemCard.ItalicFont, FontSize = 13, Foreground = ItemCard.Bullet,
        };
        var page = new StackPanel { Spacing = 12, Margin = new Thickness(20, 16, 20, 24), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(intro);
        page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _start, _clear, _flaggedOnly } });
        page.Children.Add(_status);
        page.Children.Add(_list);
        Content = new ScrollViewer { Content = page };
        Render();
    }

    /// <summary>The builds to sort against (each library build at its chosen variant), e.g. once their plans have loaded.</summary>
    public void SetBuilds(IReadOnlyList<(string Name, BuildVariant Variant)> builds, AffixCatalog catalog)
    {
        (_builds, _catalog) = (builds, catalog);
        for (var i = 0; i < _items.Count; i++)
            _items[i] = _items[i] with { Verdict = StashSort.Judge(_items[i].Item, _builds, catalog) };
        Render();
    }

    /// <summary>An item read in game: sorted while a session runs (the worn one of a comparison isn't the stash's).</summary>
    public void Add(ItemTooltip item)
    {
        if (!_sorting || item.IsEquipped || _catalog is null || WornItem.From(item, DateTimeOffset.UtcNow) is not { } signature)
            return;
        _items.RemoveAll(i => i.Signature.SameAs(signature)); // hovered again: once in the list, at the top
        _items.Insert(0, (item, signature, StashSort.Judge(item, _builds, _catalog)));
        Render();
    }

    private void Render()
    {
        _start.Content = _sorting ? "Stop sorting" : "Start sorting";
        _status.Text = (!_sorting ? ""
                : Supporter.SortsAllBuilds ? $"Sorting against {_builds.Count} build{(_builds.Count == 1 ? "" : "s")} in your library. "
                : "Sorting against the build you're showing (patrons sort against every build in their library). ")
            + (_items.Count == 0 ? (_sorting ? "Hover an item in game." : "") : $"{_items.Count} item{(_items.Count == 1 ? "" : "s")} read.");
        _list.Children.Clear();
        foreach (var (call, title) in new[] { (StashCall.Keep, "Keep"), (StashCall.Craft, "Craft"), (StashCall.Sell, "Sell?"), (StashCall.Salvage, "Salvage") })
        {
            var items = _items.Where(i => i.Verdict.Call == call && (_flaggedOnly.IsChecked != true || Flagged(i.Item) > 0)).ToList();
            if (items.Count == 0)
                continue;
            var heading = new TextBlock { Text = $"{title.ToUpperInvariant()} ({items.Count})", FontSize = 14, Margin = new Thickness(0, 10, 0, 2) };
            heading.Style = (Style)Application.Current.Resources["DisplayTextStyle"];
            _list.Children.Add(heading);
            foreach (var (item, _, verdict) in items)
                _list.Children.Add(Row(item, verdict));
        }
    }

    private StackPanel Row(ItemTooltip item, StashVerdict verdict)
    {
        var row = new StackPanel { Spacing = 1, Padding = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock
        {
            Text = item.Name ?? "(name not read)", FontFamily = ItemCard.BoldFont, FontSize = 15, TextWrapping = TextWrapping.Wrap,
            Foreground = ItemCard.RarityBrush(item.Rarity),
        });
        var type = string.Join(" · ", new[] { item.TypeLine, item.ItemPower is { } power ? $"{power} Item Power" : null, item.AccountBound ? "Account bound" : null }.OfType<string>());
        row.Children.Add(new TextBlock { Text = type, FontFamily = ItemCard.Font, FontSize = 12, Foreground = ItemCard.Bullet, TextWrapping = TextWrapping.Wrap });
        var why = verdict.Call switch
        {
            StashCall.Keep => $"For {verdict.Build}, as its {verdict.Fit!.PlannedName}.",
            StashCall.Craft => $"For {verdict.Build}, as its {verdict.Fit!.PlannedName}: {verdict.Fit.Verdict?.Reason}",
            StashCall.Sell => "No build of yours wants it, but it can be traded.",
            _ => item.AccountBound ? "No build of yours wants it, and it can't be traded." : "No build of yours wants it.",
        };
        row.Children.Add(new TextBlock { Text = why, FontFamily = ItemCard.Font, FontSize = 13, Foreground = ItemCard.Text, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(Values(item));
        if (verdict.Call == StashCall.Sell)
        {
            var check = new HyperlinkButton { Content = "Price check", Padding = new Thickness(0, 2, 0, 2) };
            var prices = new TextBlock { FontFamily = ItemCard.Font, FontSize = 13, Foreground = ItemCard.Number, TextWrapping = TextWrapping.Wrap };
            check.Click += async (_, _) =>
            {
                check.IsEnabled = false;
                prices.Text = await OverlayWindow.PriceCheckText(item, _catalog?.Season ?? 0);
                check.IsEnabled = true;
            };
            row.Children.Add(check);
            row.Children.Add(prices);
        }
        return row;
    }

    private int Flagged(ItemTooltip item) => _catalog is null ? 0 : item.Lines.Sum(l => ReadCheck.Check(item, l, _catalog).Count);

    /// <summary>
    /// The item's read values, behind a link (open when one looks misread): each corrects on a click, like on the item
    /// card, and the item is sorted again on the corrected values.
    /// </summary>
    private StackPanel Values(ItemTooltip item)
    {
        var key = ReadCorrections.ItemKey(item);
        var flagged = Flagged(item);
        var lines = new StackPanel { Spacing = 3, Margin = new Thickness(0, 2, 0, 2), Visibility = flagged > 0 || _open.Contains(key) ? Visibility.Visible : Visibility.Collapsed };
        for (var i = 0; i < item.Lines.Count; i++)
        {
            var (line, index) = (item.Lines[i], i);
            if (line.Matches is not [var best, ..] || best.Values.Count == 0)
                continue;
            var shown = ItemCard.Line(new CardLine("◆", ItemCard.Bullet, OverlayWindow.Fill(best.Entry.Lines[best.LineIndex], best.Values)));
            lines.Children.Add(ValueEditor.Wrap(shown, best.Values, _catalog is null ? [] : ReadCheck.Check(item, line, _catalog), line.ReadValues,
                values => Replace(item, ReadCorrections.Entered(item, index, values)), () => Replace(item, ReadCorrections.Reverted(item, index))));
        }
        var toggle = new HyperlinkButton
        {
            Content = flagged > 0 ? $"⚠ {flagged} value{(flagged == 1 ? "" : "s")} may be misread" : "Values", Padding = new Thickness(0, 2, 0, 2),
            Visibility = lines.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        toggle.Click += (_, _) =>
        {
            lines.Visibility = lines.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            _ = lines.Visibility == Visibility.Visible ? _open.Add(key) : _open.Remove(key);
        };
        return new StackPanel { Children = { toggle, lines } };
    }

    private void Replace(ItemTooltip old, ItemTooltip corrected)
    {
        var at = _items.FindIndex(i => ReferenceEquals(i.Item, old));
        if (at < 0 || _catalog is null)
            return;
        _items[at] = _items[at] with { Item = corrected, Verdict = StashSort.Judge(corrected, _builds, _catalog) };
        _open.Add(ReadCorrections.ItemKey(corrected));
        Corrected?.Invoke(corrected);
        Render();
    }
}