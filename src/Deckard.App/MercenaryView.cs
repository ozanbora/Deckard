using Deckard.Core.Builds;
using Deckard.Core.Items;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deckard.App;

/// <summary>
/// The build's mercenaries, laid out like the maxroll planner's: the hired mercenary's skill tree drawn as the game draws
/// it (<see cref="SkillTreeView"/>), the guide's picks lit, with the picks by name beside it; then the reinforcement,
/// its skill and the player's skill that calls it, and the guide's mercenary notes. Hovering a line shows what it does.
/// </summary>
public sealed partial class MercenaryView : UserControl
{
    private readonly StackPanel _page = new() { Spacing = 10, Padding = new Thickness(28, 16, 28, 24), HorizontalAlignment = HorizontalAlignment.Left };

    public MercenaryView() => Content = new ScrollViewer { Content = _page };

    public void Clear() => _page.Children.Clear();

    public void Show(BuildVariant variant, AffixCatalog catalog)
    {
        Clear();
        var plan = variant.Mercenaries;
        var hired = plan?.Hired is { } h ? catalog.Mercenaries.GetValueOrDefault(h) : null;
        var called = plan?.Reinforcement is { } r ? catalog.Mercenaries.GetValueOrDefault(r) : null;
        var notes = (plan?.Notes ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hired is null && called is null && notes.Length == 0)
        {
            _page.Children.Add(BuildSheetView.Heading("Mercenaries"));
            _page.Children.Add(new TextBlock
            {
                Text = "The guide picks no mercenaries.", FontFamily = ItemCard.ItalicFont, FontSize = 14, Foreground = ItemCard.Bullet,
            });
            return;
        }

        if (hired is not null)
        {
            _page.Children.Add(BuildSheetView.Heading($"Hire {hired.Name}"));
            var picks = hired.InTreeOrder(plan!.Picks).Select(n => hired.Nodes.GetValueOrDefault(n)).OfType<string>()
                .Select(k => hired.Skills.GetValueOrDefault(k)).OfType<ClassMechanic>().ToList();
            var list = new StackPanel { Spacing = 6, MaxWidth = 360, VerticalAlignment = VerticalAlignment.Top };
            list.Children.Add(Label(picks.Count > 0 ? "The guide's picks in the skill tree, lit, in the order the tree opens them:" : "The guide picks no skills."));
            foreach (var pick in picks)
                list.Children.Add(Skill(pick));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 32 };
            if (hired.Tree is { Nodes.Count: > 0 } tree)
            {
                // A new tree view each time: moving one view into the new row still threw (0x800F1000) on every worn item read.
                var view = new SkillTreeView { Width = 460, Height = 460, FitWhole = true };
                view.Show(tree, plan.Picks.ToDictionary(n => n, _ => 1));
                row.Children.Add(view);
            }
            row.Children.Add(list);
            _page.Children.Add(row);
        }

        if (called is not null)
        {
            _page.Children.Add(BuildSheetView.Heading($"Reinforcement {called.Name}"));
            if (plan!.ReinforcementSkill is { } k && called.Skills.GetValueOrDefault(k) is { } skill)
            {
                _page.Children.Add(Skill(skill));
                _page.Children.Add(Label($"A reinforcement's skills unlock with Rapport: until {skill.Name} does, pick any unlocked one."));
            }
            if (plan.Trigger is { } t && catalog.Skills.GetValueOrDefault(t) is { } trigger) // a placeholder key names nothing
                _page.Children.Add(Label($"Called by your {trigger}."));
        }

        if (notes.Length > 0)
        {
            _page.Children.Add(BuildSheetView.Heading("The guide's notes"));
            foreach (var note in notes)
                _page.Children.Add(ItemCard.Line(new CardLine("◆", ItemCard.Bullet, note, ItemCard.Text)));
        }
    }

    /// <summary>A skill by name, what it does below it.</summary>
    private static StackPanel Skill(ClassMechanic skill)
    {
        var panel = new StackPanel { Spacing = 2, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = skill.Name, FontFamily = ItemCard.BoldFont, FontSize = 15, Foreground = ItemCard.Number });
        if (skill.Desc.Length > 0)
            panel.Children.Add(new TextBlock { Text = skill.Desc, FontFamily = ItemCard.Font, FontSize = 13, Foreground = ItemCard.Text, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, FontFamily = ItemCard.ItalicFont, FontSize = 13, Foreground = ItemCard.Bullet, TextWrapping = TextWrapping.Wrap };
}
