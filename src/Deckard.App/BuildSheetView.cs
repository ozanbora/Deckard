using Deckard.Core.Builds;
using Deckard.Core.Items;
using Deckard.Perception.Builds;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Deckard.App;

/// <summary>
/// The rest of the build beside the item doll on the Items tab (which names each item under its picture): the runes and
/// gems to socket, the worn gear's progress, the skill bar, and the guide's how-to-play notes (the seal and charms have
/// their own page, <see cref="TalismanView"/>). Game-styled, so it stays dark like the item cards.
/// </summary>
public sealed partial class BuildSheetView : UserControl
{
    private const int FirstCharmSlot = 20; // the seal; 21-26 are charms

    private readonly StackPanel _sheet = new() { Spacing = 6, Padding = new Thickness(20, 16, 20, 24) };

    public BuildSheetView() => Content = new ScrollViewer { Content = _sheet };

    public void Clear() => _sheet.Children.Clear();

    /// <summary>A task the player ticked (or unticked): its key, kept per account so the tick stays.</summary>
    public event Action<string, bool>? TaskTicked;

    /// <summary>The player clicked a rune or gem to find it on the market: its goods kind ("Rune", "Gem") and name.</summary>
    public event Action<string, string>? FindOnMarket;

    /// <param name="worn">The player's worn items by build slot, for the progress line and what's left to craft.</param>
    /// <param name="ticked">Task keys the player has ticked off.</param>
    public void Show(BuildVariant variant, AffixCatalog catalog, string className,
        IReadOnlyDictionary<int, (WornItem Item, ItemComparison Fit)>? worn = null, IReadOnlySet<string>? ticked = null)
    {
        _sheet.Children.Clear();
        ShowSockets(variant, catalog); // first: the to-do list below it can run long
        ShowProgress(variant, catalog, worn ?? new Dictionary<int, (WornItem, ItemComparison)>(), ticked ?? new HashSet<string>());

        if (variant.SkillBar.Count > 0)
        {
            _sheet.Children.Add(Heading("Skill bar"));
            var tree = catalog.SkillTrees.GetValueOrDefault(className);
            var ranks = variant.SkillSteps.Count > 0 ? variant.SkillSteps[^1].RanksByNode : new Dictionary<int, int>();
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var key in variant.SkillBar)
                bar.Children.Add(SkillTile(key, catalog, tree, ranks));
            _sheet.Children.Add(bar);
        }

        var mechanics = variant.ClassMechanics.Select(k => catalog.ClassMechanics.GetValueOrDefault(k)).OfType<ClassMechanic>().ToList();
        if (mechanics.Count > 0)
        {
            _sheet.Children.Add(Heading("Soul shard"));
            var row = new TextBlock { Text = string.Join(" · ", mechanics.Select(m => m.Name)), FontFamily = ItemCard.Font, FontSize = 14, Foreground = ItemCard.Text, TextWrapping = TextWrapping.Wrap };
            ToolTipService.SetToolTip(row, string.Join("\n\n", mechanics.Select(m => $"{m.Name}\n{m.Desc}")));
            _sheet.Children.Add(row);
        }

        var notes = variant.SkillNotes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (notes.Length > 0)
        {
            _sheet.Children.Add(Heading("How to play"));
            foreach (var note in notes)
                _sheet.Children.Add(ItemCard.Line(new CardLine("◆", ItemCard.Bullet, note, ItemCard.Text)));
        }
    }

    /// <summary>
    /// How far the worn gear is from the build (the slots Deckard has read, from "EQUIPPED" tooltips), then what's left
    /// to do on each worn item: imprint, enchant, temper or replace. Each task can be ticked off.
    /// </summary>
    private void ShowProgress(BuildVariant variant, AffixCatalog catalog, IReadOnlyDictionary<int, (WornItem Item, ItemComparison Fit)> worn, IReadOnlySet<string> ticked)
    {
        var gearSlots = variant.Items.Keys.Where(s => s < FirstCharmSlot).ToList();
        _sheet.Children.Add(Heading("Your gear"));
        if (worn.Count == 0)
        {
            _sheet.Children.Add(Note("Hover your worn items in game (or compare an item with them) and Deckard tracks how close your gear is to this build."));
            return;
        }
        int Count(ItemVerdict v) => worn.Values.Count(w => w.Fit.Verdict?.Verdict == v);
        var unread = gearSlots.Count(s => !worn.ContainsKey(s));
        _sheet.Children.Add(new TextBlock
        {
            Text = $"{Count(ItemVerdict.Keep)} of {gearSlots.Count} slots ready · {Count(ItemVerdict.Craft)} to craft · {Count(ItemVerdict.Salvage)} to replace"
                + (unread > 0 ? $" · {unread} not read yet" : ""),
            FontFamily = ItemCard.BoldFont, FontSize = 14, Foreground = ItemCard.Number, TextWrapping = TextWrapping.Wrap,
        });

        var tasks = worn.OrderBy(w => w.Key).SelectMany(w => Gear.Tasks(w.Value.Item, w.Value.Fit, catalog)).ToList();
        if (tasks.Count == 0)
            return;
        _sheet.Children.Add(Heading("To do"));
        foreach (var task in tasks)
        {
            var item = catalog.Items.GetValueOrDefault(variant.Items[task.Slot].ItemKey)?.Name ?? "";
            var text = task.Kind switch
            {
                CraftKind.Replace => worn[task.Slot].Fit.SameUnique == false
                    ? $"{item}: you wear {worn[task.Slot].Item.Name ?? "another item"} instead. Find {item}."
                    : $"{item}: yours is missing {worn[task.Slot].Fit.Missing.Count} of the build's affixes. Find a better one.",
                CraftKind.Imprint => $"{item}: imprint {task.Affix!.Name ?? "the aspect"} at the Occultist.",
                CraftKind.Enchant => $"{item}: enchant for {AffixText(task, variant, catalog)} at the Occultist.",
                _ => $"{item}: temper {AffixText(task, variant, catalog)} at the Blacksmith.",
            };
            var box = new CheckBox
            {
                IsChecked = ticked.Contains(task.Key), MinWidth = 0, Padding = new Thickness(6, 2, 0, 2),
                Content = new TextBlock { Text = text, FontFamily = ItemCard.Font, FontSize = 13, Foreground = ItemCard.Text, TextWrapping = TextWrapping.Wrap },
            };
            box.Checked += (_, _) => TaskTicked?.Invoke(task.Key, true);
            box.Unchecked += (_, _) => TaskTicked?.Invoke(task.Key, false);
            _sheet.Children.Add(box);
        }
    }

    /// <summary>The build's text for the task's affix, with the planner's value.</summary>
    private static string AffixText(CraftTask task, BuildVariant variant, AffixCatalog catalog)
    {
        var planned = variant.Items[task.Slot];
        var affix = planned.Affixes.Concat(planned.Tempered).FirstOrDefault(a => task.Affix!.Ids.Contains(a.Id));
        return affix is not null ? PlannedItemTooltip.Text(affix, catalog.AffixText.GetValueOrDefault(affix.Id)) : task.Affix!.Lines[0].Replace("#", "…", StringComparison.Ordinal);
    }

    /// <summary>
    /// A socketed gem or rune's name. Gems and runes are goods; seasonal socketables have their own list. Null for a key
    /// the data doesn't name (an older data file).
    /// </summary>
    internal static string? SocketName(string key, AffixCatalog catalog) =>
        catalog.Goods.FirstOrDefault(g => g.Key == key)?.Name ?? catalog.Socketables.GetValueOrDefault(key)?.Name;

    /// <summary>The sockets' names in order; unnamed ones are counted rather than shown raw, empty ones left out.</summary>
    internal static string? SocketText(IReadOnlyList<string> sockets, AffixCatalog catalog)
    {
        var filled = sockets.Where(k => k.Length > 0).ToList();
        if (filled.Count == 0)
            return null;
        var named = filled.Select(key => SocketName(key, catalog)).OfType<string>().ToList();
        var unnamed = filled.Count - named.Count;
        if (unnamed > 0)
            named.Add(unnamed == 1 ? "1 more socketed" : $"{unnamed} more socketed");
        return string.Join(", ", named);
    }

    /// <summary>Every gem and rune the build sockets, counted: runes first (the ones to hunt or buy), then the rest.</summary>
    private void ShowSockets(BuildVariant variant, AffixCatalog catalog)
    {
        var keys = variant.Items.Values.SelectMany(i => i.Sockets ?? []).Where(k => k.Length > 0).ToList();
        if (keys.Count == 0)
            return;
        // Row 0 runes, 1 seasonal splinters (socketables), 2 gems and anything else.
        Socketed Describe(string key, int count)
        {
            var goods = catalog.Goods.FirstOrDefault(x => x.Key == key);
            var socketable = catalog.Socketables.GetValueOrDefault(key);
            return new(SocketName(key, catalog) ?? "Unknown socketable", count, goods?.Kind == "Rune" ? 0 : goods is null ? 1 : 2,
                goods?.Icon ?? socketable?.Icon ?? 0, goods?.Kind ?? "Socketable", goods?.Lines ?? socketable?.Lines ?? []);
        }
        _sheet.Children.Add(Heading("Runes and gems"));

        // Runes as the item they go in pairs them (a Ritual and an Invocation rune make its runeword), like the planner.
        var pairs = variant.Items.OrderBy(i => i.Key)
            .Select(i => (Item: i.Value, Runes: (i.Value.Sockets ?? []).Select(k => Describe(k, 1)).Where(x => x.Row == 0).ToList()))
            .Where(p => p.Runes.Count > 0)
            .Select(p =>
            {
                var tiles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                foreach (var rune in p.Runes)
                    tiles.Children.Add(Tile(rune));
                var box = new StackPanel { Spacing = 2 };
                box.Children.Add(tiles);
                box.Children.Add(new TextBlock
                {
                    Text = catalog.Items.GetValueOrDefault(p.Item.ItemKey)?.Name ?? "", FontFamily = ItemCard.ItalicFont, FontSize = 12,
                    Foreground = ItemCard.Bullet, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                });
                return (UIElement)new Border
                {
                    Child = box, Padding = new Thickness(4, 6, 4, 6), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1),
                    BorderBrush = ItemCard.Bullet, VerticalAlignment = VerticalAlignment.Top,
                };
            }).ToList();
        AddLines(pairs, 2);

        var counted = keys.GroupBy(k => k).Select(g => Describe(g.Key, g.Count())).Where(x => x.Row != 0)
            .GroupBy(x => x.Name).Select(g => g.First() with { Count = g.Sum(x => x.Count) }) // keys of one name count together
            .OrderBy(x => x.Row).ThenByDescending(x => x.Count).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
        foreach (var group in counted.GroupBy(x => x.Row))
            AddLines([.. group.Select(x => (UIElement)Tile(x))], 5);
    }

    private sealed record Socketed(string Name, int Count, int Row, long Icon, string Kind, IReadOnlyList<string> Lines);

    // shortcut: a set number of tiles a line whatever the sheet's width; a wrap layout would fit more on a wide window.
    private void AddLines(List<UIElement> tiles, int perLine)
    {
        for (var i = 0; i < tiles.Count; i += perLine)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 8) };
            foreach (var tile in tiles.Skip(i).Take(perLine))
                line.Children.Add(tile);
            _sheet.Children.Add(line);
        }
    }

    /// <summary>A rune, splinter or gem: its icon over its name; hovering shows what it does, a click finds runes and gems on the market.</summary>
    private StackPanel Tile(Socketed x)
    {
        var icon = new Image { Width = 40, Height = 40, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        if (x.Icon != 0)
            _ = LoadAsync(icon, x.Icon);
        var text = x.Count > 1 ? $"{x.Name} ×{x.Count}" : x.Name;
        var tile = new StackPanel { Spacing = 4, Width = 110, VerticalAlignment = VerticalAlignment.Top };
        tile.Children.Add(icon);
        tile.Children.Add(new TextBlock
        {
            Text = text, FontFamily = ItemCard.Font, FontSize = 13, TextAlignment = TextAlignment.Center,
            Foreground = x.Row == 0 ? ItemCard.Number : ItemCard.Text, TextWrapping = TextWrapping.Wrap,
        });
        // Runes and gems are market goods: a click searches for them (splinters aren't traded on Deckard's market).
        var tradable = x.Kind != "Socketable";
        if (tradable)
        {
            tile.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // clickable between icon and name too
            tile.Tapped += (_, _) => FindOnMarket?.Invoke(x.Kind, x.Name);
        }
        // What it does, as a card like the items' (an older data file has no text: just the market hint).
        if (x.Lines.Count > 0 || tradable)
            ToolTipService.SetToolTip(tile, new ToolTip
            {
                Content = ItemCard.Build(x.Name, null, x.Kind, x.Count > 1 ? $"×{x.Count} in this build" : null,
                    x.Lines.Select(l => new CardLine("◆", ItemCard.Bullet, l)), [], 0,
                    footer: tradable ? new TextBlock { Text = "Click to find it on the market.", FontFamily = ItemCard.ItalicFont, FontSize = 13, Foreground = ItemCard.Bullet } : null),
                Padding = new Thickness(0), BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), MaxWidth = ItemCard.Width + 8,
            });
        return tile;
    }

    private static TextBlock Note(string text) =>
        new() { Text = text, FontFamily = ItemCard.ItalicFont, FontSize = 13, Foreground = ItemCard.Bullet, TextWrapping = TextWrapping.Wrap };

    /// <summary>A skill as on the game's skill bar: its icon in a frame, its name, and its description at the build's rank on hover.</summary>
    private static StackPanel SkillTile(string key, AffixCatalog catalog, SkillTree? tree, IReadOnlyDictionary<int, int> ranks)
    {
        var node = tree?.Nodes.FirstOrDefault(n => n.Kind == "skill" && n.Skill == key);
        var icon = new Image { Width = 52, Height = 52, Stretch = Stretch.Uniform };
        if (node is { Icon: not 0 })
            _ = LoadAsync(icon, node.Icon);
        var tile = new StackPanel { Width = 96, Spacing = 4 };
        tile.Children.Add(new Border
        {
            Width = 58, Height = 58, HorizontalAlignment = HorizontalAlignment.Center, BorderThickness = new Thickness(1),
            BorderBrush = ItemCard.Greater, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 20, 18, 16)), Child = icon,
        });
        var name = catalog.Skills.GetValueOrDefault(key) ?? node?.Name ?? key;
        tile.Children.Add(new TextBlock { Text = name, FontFamily = ItemCard.Font, FontSize = 12, Foreground = ItemCard.Text, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.WrapWholeWords });
        if (node is not null)
        {
            var rank = ranks.GetValueOrDefault(node.Node);
            var desc = rank > 0 && node.DescByRank is { } byRank && rank <= byRank.Count ? byRank[rank - 1] : node.Desc;
            ToolTipService.SetToolTip(tile, rank > 0 ? $"{name} (rank {rank})\n{desc}" : $"{name}\n{desc}");
        }
        return tile;
    }

    internal static TextBlock Heading(string text)
    {
        var heading = new TextBlock { Text = text.ToUpperInvariant(), FontSize = 14, Margin = new Thickness(0, 14, 0, 2) };
        heading.Style = (Style)Application.Current.Resources["DisplayTextStyle"];
        return heading;
    }

    private static async Task LoadAsync(Image image, long id) => image.Source = await IconCache.GetAsync(id);
}
