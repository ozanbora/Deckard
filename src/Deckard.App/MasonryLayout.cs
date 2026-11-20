using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Deckard.App;

/// <summary>
/// Lays cards of different heights out in as many columns as fit, each card going under the shortest column, and only
/// creates the cards near the viewport. Placed cards keep their spot, so paging in more results only adds below.
/// </summary>
public sealed partial class MasonryLayout : VirtualizingLayout
{
    public double MinColumnWidth { get; set; } = 380;
    public double Spacing { get; set; } = 8;

    private sealed class State
    {
        public List<Rect> Bounds { get; } = []; // per item, in order: placement only depends on the items before it
        public double Width = double.NaN;
        public int First = -1, Last = -1;         // the realized items
    }

    protected override void InitializeForContextCore(VirtualizingLayoutContext context) => context.LayoutState = new State();

    protected override void UninitializeForContextCore(VirtualizingLayoutContext context) => context.LayoutState = null;

    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source, NotifyCollectionChangedEventArgs args)
    {
        var state = (State)context.LayoutState;
        // Appending keeps every placed card where it is; any other change places them all again.
        if (args.Action != NotifyCollectionChangedAction.Add || args.NewStartingIndex < state.Bounds.Count)
        {
            state.Bounds.Clear();
            state.First = state.Last = -1; // the repeater already dropped the old elements
        }
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var state = (State)context.LayoutState;
        var width = double.IsInfinity(availableSize.Width) ? MinColumnWidth : availableSize.Width;
        var columns = Math.Max(1, (int)((width + Spacing) / (MinColumnWidth + Spacing)));
        var columnWidth = (width - Spacing * (columns - 1)) / columns;
        if (width != state.Width)
        {
            state.Bounds.Clear(); // card heights change with the column width
            state.Width = width;
        }

        var view = context.RealizationRect;
        var bottoms = new double[columns];
        int first = -1, last = -1;
        for (var i = 0; i < context.ItemCount; i++)
        {
            if (i == state.Bounds.Count)
            {
                var shortest = Array.IndexOf(bottoms, bottoms.Min());
                if (bottoms[shortest] > view.Bottom)
                    break; // every column already reaches past the viewport: the rest is placed when scrolled to
                var element = context.GetOrCreateElementAt(i);
                element.Measure(new Size(columnWidth, double.PositiveInfinity));
                state.Bounds.Add(new Rect(shortest * (columnWidth + Spacing), bottoms[shortest], columnWidth, element.DesiredSize.Height));
                if (bottoms[shortest] + element.DesiredSize.Height < view.Top)
                    context.RecycleElement(element); // measured only to place it, after a jump past it
            }
            var rect = state.Bounds[i];
            var column = (int)Math.Round(rect.X / (columnWidth + Spacing));
            bottoms[column] = rect.Y + rect.Height + Spacing;
            if (rect.Y <= view.Bottom && rect.Y + rect.Height >= view.Top)
            {
                first = first < 0 ? i : first;
                last = i;
            }
        }

        // Realize everything between the first and last visible card, and give back what scrolled away.
        for (var i = state.First; i >= 0 && i <= state.Last && i < context.ItemCount; i++)
            if (i < first || i > last)
                context.RecycleElement(context.GetOrCreateElementAt(i));
        for (var i = first; i >= 0 && i <= last; i++)
            context.GetOrCreateElementAt(i).Measure(new Size(columnWidth, double.PositiveInfinity));
        (state.First, state.Last) = (first, last);

        // Unplaced cards are estimated at the average height so far, so the scroll bar stays about right.
        var placed = state.Bounds.Count;
        var average = placed == 0 ? 0 : state.Bounds.Average(b => b.Height + Spacing);
        var height = bottoms.Max() + Math.Ceiling((context.ItemCount - placed) / (double)columns) * average;
        return new Size(width, Math.Max(0, height - Spacing));
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        var state = (State)context.LayoutState;
        for (var i = state.First; i >= 0 && i <= state.Last; i++)
            context.GetOrCreateElementAt(i).Arrange(state.Bounds[i]);
        return finalSize;
    }
}
