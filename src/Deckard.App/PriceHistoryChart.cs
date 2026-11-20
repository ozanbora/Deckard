using Deckard.Core.Trade;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Deckard.App;

/// <summary>
/// The search's price history over the last 30 days: the median price paid per day (orange, a dot per day) and the
/// median asking price of the day's new listings (blue), on one gold axis from 0. A legend names both and each line
/// ends in its last value; hovering a day shows its numbers. Colors validated for color-blind readers on the dark surface.
/// </summary>
public sealed partial class PriceHistoryChart : UserControl
{
    private static readonly SolidColorBrush Sold = new(Color(0xD0, 0x7A, 0x42));
    private static readonly SolidColorBrush Listed = new(Color(0x6E, 0x8F, 0xD6));
    private static readonly SolidColorBrush Grid = new(Color(0x34, 0x30, 0x2C));
    private static readonly SolidColorBrush Surface = new(Color(0x0E, 0x0D, 0x0C));
    private const double Days = 30, PlotHeight = 120, AxisWidth = 52, EndLabels = 64;

    private readonly Canvas _plot = new() { Height = PlotHeight + 22 };
    private PriceHistory _history = new([]);

    public PriceHistoryChart()
    {
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        legend.Children.Add(new TextBlock { Text = "Price history, 30 days", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        legend.Children.Add(Key(Sold, "Sold (median paid)", dot: true));
        legend.Children.Add(Key(Listed, "Listed (median asking)", dot: false));
        Content = new StackPanel { Spacing = 6, Children = { legend, _plot } };
        _plot.SizeChanged += (_, _) => Draw();
    }

    public void Show(PriceHistory history)
    {
        _history = history;
        Draw();
    }

    private static Windows.UI.Color Color(byte r, byte g, byte b) => ColorHelper.FromArgb(255, r, g, b);

    /// <summary>A legend entry: a short line (and a dot, for sold) in the series color, its name in text color.</summary>
    private static StackPanel Key(Brush color, string text, bool dot)
    {
        var mark = new Canvas { Width = 18, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        mark.Children.Add(new Line { X1 = 0, Y1 = 5, X2 = 18, Y2 = 5, Stroke = color, StrokeThickness = 2 });
        if (dot)
        {
            var e = new Ellipse { Width = 8, Height = 8, Fill = color };
            Canvas.SetLeft(e, 5);
            Canvas.SetTop(e, 1);
            mark.Children.Add(e);
        }
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { mark, new TextBlock { Text = text, Opacity = 0.8 } } };
    }

    private void Draw()
    {
        _plot.Children.Clear();
        var width = _plot.ActualWidth - AxisWidth - EndLabels;
        if (width < 100 || _history.Days.Count == 0)
            return;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var first = today.AddDays(-(int)Days + 1);
        var top = NiceTop(_history.Days.Max(d => Math.Max(d.SoldMedianGold ?? 0, d.ListedMedianGold ?? 0)));
        double X(DateOnly day) => AxisWidth + (day.DayNumber - first.DayNumber) / (Days - 1) * width;
        double Y(long gold) => PlotHeight - gold / (double)top * PlotHeight + 4;

        // Recessive axis: three hairlines with compact gold labels.
        foreach (var step in new[] { 0.0, 0.5, 1.0 })
        {
            var y = Y((long)(top * step));
            _plot.Children.Add(new Line { X1 = AxisWidth, X2 = AxisWidth + width, Y1 = y, Y2 = y, Stroke = Grid, StrokeThickness = 1 });
            Place(new TextBlock { Text = Gold.Format((long)(top * step)), FontSize = 11, Opacity = 0.6 }, 0, y - 8);
        }
        Place(new TextBlock { Text = first.ToString("MMM d"), FontSize = 11, Opacity = 0.6 }, AxisWidth, PlotHeight + 6);
        Place(new TextBlock { Text = "today", FontSize = 11, Opacity = 0.6 }, AxisWidth + width - 30, PlotHeight + 6);

        Series(d => d.ListedMedianGold, Listed, dots: false);
        Series(d => d.SoldMedianGold, Sold, dots: true);

        // One hover column per day with prices: a crosshair and the day's numbers.
        var column = width / (Days - 1);
        foreach (var day in _history.Days.Where(d => d.Day >= first))
        {
            var x = X(day.Day);
            var hit = new Rectangle { Width = Math.Max(column, 8), Height = PlotHeight + 8, Fill = new SolidColorBrush(Colors.Transparent) };
            var cross = new Line { X1 = x, X2 = x, Y1 = 4, Y2 = PlotHeight + 4, Stroke = Grid, StrokeThickness = 1, Visibility = Visibility.Collapsed };
            hit.PointerEntered += (_, _) => cross.Visibility = Visibility.Visible;
            hit.PointerExited += (_, _) => cross.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(hit, $"{day.Day:MMM d}\n" +
                (day.Sold > 0 ? $"Sold {day.Sold}, median {Gold.Format(day.SoldMedianGold ?? 0)}" : "None sold") + "\n" +
                (day.Listed > 0 ? $"Listed {day.Listed}, median asking {Gold.Format(day.ListedMedianGold ?? 0)}" : "None listed"));
            _plot.Children.Insert(0, cross);
            Place(hit, x - hit.Width / 2, 0);
        }

        void Series(Func<PricePoint, long?> value, Brush color, bool dots)
        {
            var points = _history.Days.Where(d => d.Day >= first && value(d) is not null).Select(d => new Point(X(d.Day), Y(value(d)!.Value))).ToList();
            if (points.Count == 0)
                return;
            var line = new Polyline { Stroke = color, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            foreach (var p in points)
                line.Points.Add(p);
            _plot.Children.Add(line);
            foreach (var p in dots ? points : [points[^1]])
            {
                // 8 px dot with a 2 px surface ring, so it reads where it crosses the other line.
                var dot = new Ellipse { Width = 12, Height = 12, Fill = color, Stroke = Surface, StrokeThickness = 2 };
                Place(dot, p.X - 6, p.Y - 6);
            }
            // The last value at the line's end, in text color: the series' identity comes from the mark beside it.
            Place(new TextBlock { Text = Gold.Format(value(_history.Days.Last(d => d.Day >= first && value(d) is not null))!.Value), FontSize = 12 },
                points[^1].X + 10, points[^1].Y - 9);
        }
    }

    private void Place(FrameworkElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        _plot.Children.Add(element);
    }

    /// <summary>A round axis top above the highest price: 1, 2, 2.5 or 5 times a power of ten.</summary>
    internal static long NiceTop(long max)
    {
        if (max <= 0)
            return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var step in new[] { 1, 2, 2.5, 5, 10 })
            if (step * magnitude >= max)
                return (long)(step * magnitude);
        return (long)(10 * magnitude);
    }
}
