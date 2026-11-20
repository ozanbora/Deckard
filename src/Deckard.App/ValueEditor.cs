using System.Globalization;
using Deckard.Perception.Tooltips;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Deckard.App;

/// <summary>
/// Click-to-correct for a line's read values, on the item card and the Stash page: the line becomes a button, and a
/// click puts a box per value under it (Enter saves, Esc cancels, Tab goes to the next). A value that looks misread
/// (<see cref="ReadCheck"/>) or was corrected says so under the line.
/// </summary>
internal static class ValueEditor
{
    /// <param name="shown">The line as drawn.</param>
    /// <param name="read">The values as read, when the player corrected them: offers to go back to them.</param>
    /// <param name="editing">Told when the boxes open and close, e.g. for a window to keep the keyboard.</param>
    public static FrameworkElement Wrap(FrameworkElement shown, IReadOnlyList<double> values, IReadOnlyList<ValueFlag> flags,
        IReadOnlyList<double>? read, Action<IReadOnlyList<double>> save, Action revert, Action<bool>? editing = null)
    {
        var host = new StackPanel { Spacing = 2 };
        var line = new Button
        {
            Content = shown, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 0, MinWidth = 0,
            UseSystemFocusVisuals = false, // no outline left on the line after a correction (a hint can't help either: it can't leave the card's window, so it covered the line)
        };
        host.Children.Add(line);
        foreach (var flag in flags)
            host.Children.Add(Note($"⚠ {Format(values[flag.Value])} looks misread: {(flag.Shown ? "the tooltip shows" : "this rolls")} {Format(flag.Low)} {(double.IsInfinity(flag.High) ? "or more" : $"to {Format(flag.High)}")}. Click to correct it, or to confirm it with Enter.", ItemCard.Fair));
        if (read is not null)
        {
            var undo = new HyperlinkButton { Content = "Revert", Padding = new Thickness(4, 0, 4, 0), FontSize = 12 };
            undo.Click += (_, _) => revert();
            host.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 0),
                Children = { Note($"Corrected (read as {string.Join(", ", read.Select(Format))})", ItemCard.Bullet, indent: 0), undo },
            });
        }

        line.Click += (_, _) =>
        {
            if (host.Tag is not null)
                return; // already open
            var boxes = values.Select(v => new TextBox { Text = Format(v), Width = 84, FontSize = 13, Padding = new Thickness(6, 3, 6, 3) }).ToList();
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(18, 2, 0, 2) };
            var hint = Note("Enter saves · Esc cancels", ItemCard.Bullet);
            void Close()
            {
                host.Children.Remove(row);
                host.Children.Remove(hint);
                host.Tag = null;
                editing?.Invoke(false);
            }
            foreach (var box in boxes)
            {
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Escape)
                    {
                        e.Handled = true;
                        Close();
                    }
                    else if (e.Key == Windows.System.VirtualKey.Enter)
                    {
                        e.Handled = true;
                        var entered = boxes.Select(b => ReadCorrections.ParseValue(b.Text)).ToList();
                        if (entered.Any(v => v is null))
                        {
                            hint.Text = "Numbers only, e.g. 213 or 10.5";
                            hint.Foreground = ItemCard.Bad;
                            return;
                        }
                        Close();
                        save([.. entered.OfType<double>()]);
                    }
                };
                box.BeforeTextChanging += (_, e) => e.Cancel = e.NewText.Any(c => !char.IsDigit(c) && c is not ('.' or ','));
                box.GotFocus += (_, _) => box.SelectAll();
                row.Children.Add(box);
            }
            host.Tag = row;
            host.Children.Insert(1, row);
            host.Children.Insert(2, hint);
            editing?.Invoke(true);
            boxes[0].Loaded += (_, _) => boxes[0].Focus(FocusState.Programmatic);
        };
        return host;
    }

    private static TextBlock Note(string text, Brush brush, double indent = 18) => new()
    {
        Text = text, FontFamily = ItemCard.Font, FontSize = 12, Foreground = brush, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(indent, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
    };

    private static string Format(double value) => value.ToString("#,0.##", CultureInfo.InvariantCulture);
}
