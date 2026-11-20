using Deckard.Core.Capture;
using Deckard.Core.Items;
using Deckard.Perception.Imaging;
using Deckard.Perception.Ocr;

namespace Deckard.Perception.Tooltips;

/// <summary>Finds item tooltips in a frame, reads them with OCR and matches their lines against the season's affixes.</summary>
/// <param name="createRecognizer">Makes an OCR engine; one is kept per tooltip on screen so a comparison's two tooltips
/// are recognized at the same time (~250 ms each) instead of one after the other.</param>
public sealed class TooltipReader(TooltipLocator locator, Func<TextRecognizer> createRecognizer, TooltipParser parser)
{
    /// <summary>Tooltips narrower than this (491 px at 1440p, 368 at 1080p) have text too small to read well at 1x.</summary>
    public const int SmallTooltipWidth = 430;

    private readonly List<TextRecognizer> _recognizers = [];

    public TooltipReader(AffixCatalog catalog)
        : this(new TooltipLocator(), () => TextRecognizer.Create(), new TooltipParser(catalog))
    {
    }

    /// <summary>Not safe to call again before the previous call completes (the OCR engines are reused).</summary>
    public Task<IReadOnlyList<ItemTooltip>> ReadAsync(Frame frame) => ReadAsync(frame, locator.Locate(frame));

    /// <summary>Reads tooltips whose bounds are already known, e.g. a whole image saved by <c>capture --tooltips --crop</c>.</summary>
    public async Task<IReadOnlyList<ItemTooltip>> ReadAsync(Frame frame, IReadOnlyList<PixelRect> bounds)
    {
        while (_recognizers.Count < bounds.Count)
            _recognizers.Add(createRecognizer());

        // Colored affix values (blue, orange) are often missed on the raw crop; brightest-channel grayscale
        // recovers them. Measured on Season 15 captures: 38/63 lines matched vs 33/71 raw at 1440p, where 2x upscaling
        // did not help. Small tooltips (screens under ~1260 px tall) are read at 2x: at 1080p that matched 44/105
        // lines vs 32/111.
        var scales = bounds.Select(b => b.Width < SmallTooltipWidth ? 2 : 1).ToList();
        var prepared = bounds.Select((b, i) =>
        {
            using var crop = frame.Crop(b);
            return ImageOps.PrepareForOcr(crop, scales[i]);
        }).ToList();
        try
        {
            var read = await Task.WhenAll(prepared.Select((image, i) => _recognizers[i].RecognizeAsync(image)));
            // Report line positions in frame coordinates so an overlay can point at them.
            var lines = bounds.Select((b, i) => (IReadOnlyList<TextLine>)[.. read[i].Select(l => l with { Bounds = Unscale(l.Bounds, scales[i]).Offset(b.X, b.Y) })]).ToList();
            var tooltips = new List<ItemTooltip>();
            for (var i = 0; i < bounds.Count; i++)
                tooltips.Add(MarkGreater(frame, await RereadNameAsync(frame, bounds[i], lines[i], parser.Parse(lines[i], bounds[i]), _recognizers[i])));
            return MarkWorn(tooltips);
        }
        finally
        {
            foreach (var image in prepared)
                image.Dispose();
        }
    }

    /// <summary>
    /// In a comparison the tooltip beside the hovered one (<see cref="ItemTooltip.Compared"/>) is the worn item: marked so
    /// when its banner wasn't read, or the card showed the worn item for the hovered one. Only items count: a box the
    /// game's world framed beside them (a warm-lit floor) holds no item power and no affix.
    /// </summary>
    public static IReadOnlyList<ItemTooltip> MarkWorn(IReadOnlyList<ItemTooltip> tooltips)
    {
        var items = tooltips.Where(IsItem).ToList();
        return items.Count == 2 && items.Count(t => t.Compared) == 1 && !items.Any(t => t.IsEquipped)
            ? [.. tooltips.Select(t => IsItem(t) && !t.Compared ? t with { IsEquipped = true } : t)]
            : tooltips;
    }

    /// <summary>Every piece of gear shows its item power; what has neither that nor a known affix isn't an item.</summary>
    public static bool IsItem(ItemTooltip t) => t.ItemPower is not null || t.Lines.Any(l => l.IsMatched);

    /// <summary>
    /// A name starts where the type line below it does. When it starts further right, OCR left out its first word on the
    /// whole tooltip ("ARBITER" for "PRIME ARBITER"): the header row is read again on its own, which keeps it.
    /// </summary>
    private async Task<ItemTooltip> RereadNameAsync(Frame frame, PixelRect bounds, IReadOnlyList<TextLine> lines, ItemTooltip tooltip, TextRecognizer ocr)
    {
        if (tooltip.TypeLine is null || lines.FirstOrDefault(l => l.Text == tooltip.TypeLine) is not { } type
            || lines.Where(l => l.Bounds.Bottom <= type.Bounds.Y).MaxBy(l => l.Bounds.Y) is not { } name
            || name.Bounds.X - type.Bounds.X < type.Bounds.Height)
            return tooltip;
        var pad = type.Bounds.Height;
        var strip = new PixelRect(Math.Max(bounds.X, type.Bounds.X - pad), name.Bounds.Y - name.Bounds.Height / 2,
            name.Bounds.Right + pad - Math.Max(bounds.X, type.Bounds.X - pad), name.Bounds.Height * 2).Intersect(new PixelRect(0, 0, frame.Width, frame.Height));
        using var crop = frame.Crop(strip);
        using var image = ImageOps.PrepareForOcr(crop, 1);
        var row = (await ocr.RecognizeAsync(image)).Select(l => l with { Bounds = l.Bounds.Offset(strip.X, strip.Y) }).ToList();
        if (row.Sum(l => l.Text.Length) <= name.Text.Length)
            return tooltip;
        var center = name.Bounds.Y + name.Bounds.Height / 2;
        return parser.Parse([.. lines.Where(l => l.Bounds.Y > center || l.Bounds.Bottom < center).Concat(row).OrderBy(l => l.Bounds.Y)], bounds);
    }

    private static ItemTooltip MarkGreater(Frame frame, ItemTooltip t) => t with
    {
        Lines = [.. t.Lines.Select(l => l.Matches is [{ Entry.Kind: AffixKind.Affix }, ..] && AffixIcon.IsGreater(frame, t.Bounds, l.Bounds) ? l with { Greater = true } : l)],
    };

    private static PixelRect Unscale(PixelRect r, int scale) =>
        scale == 1 ? r : new PixelRect(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale);
}
