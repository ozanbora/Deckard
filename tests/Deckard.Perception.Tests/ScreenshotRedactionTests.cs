using Deckard.Core.Capture;
using Deckard.Perception.Feedback;
using Deckard.Perception.Ocr;

namespace Deckard.Perception.Tests;

public class ScreenshotRedactionTests
{
    private static TextLine Line(string text, int y) => new(text, new PixelRect(100, y, 300, 20));

    [Fact]
    public void SuggestsBattleTagsAndSharedZonesOnly()
    {
        var lines = new[]
        {
            Line("Deal with Demo8484x2#2002 · 28M gold", 100),
            Line("Buyer: 41 completed trades, no reports · in game · in Gea Kul", 140),
            Line("Ancestral Legendary Gloves · 800 Item Power", 180),
            Line("TempestBard # 2193", 220), // OCR spacing
        };
        var boxes = ScreenshotRedaction.Suggest(lines, 1920, 1080, game: false);
        Assert.Equal(3, boxes.Count);
        Assert.All(boxes, b => Assert.True(b.Contains(new PixelRect(100, b.Y + 5, 300, 20))));
        Assert.DoesNotContain(boxes, b => b.Y <= 180 && b.Bottom >= 200 && b.Y > 160); // the item line stays
    }

    [Fact]
    public void GameScreenshotsAlsoCoverChatAndParty()
    {
        var boxes = ScreenshotRedaction.Suggest([], 2560, 1440, game: true);
        Assert.Equal(2, boxes.Count);
        Assert.All(boxes, b => Assert.Equal(0, b.X)); // the HUD's left side
    }

    [Fact]
    public void BoxesStayInsideTheImage()
    {
        var boxes = ScreenshotRedaction.Suggest([new TextLine("Name#1234", new PixelRect(0, 0, 50, 12))], 400, 300, game: true);
        Assert.All(boxes, b => Assert.True(new PixelRect(0, 0, 400, 300).Contains(b)));
    }

    [Fact]
    public void FillsSolidBlackAndLeavesTheRest()
    {
        using var frame = Frame.Create(10, 10, TimeSpan.Zero);
        frame.Pixels.Fill(200);
        ScreenshotRedaction.Fill(frame, [new PixelRect(2, 3, 4, 2), new PixelRect(8, 8, 5, 5)]); // the second runs off the edge
        byte[] Pixel(int x, int y) => frame.Pixels.Slice((y * 10 + x) * 4, 4).ToArray();
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(2, 3));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(5, 4));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(9, 9));
        Assert.Equal(new byte[] { 200, 200, 200, 200 }, Pixel(6, 3));
        Assert.Equal(new byte[] { 200, 200, 200, 200 }, Pixel(2, 5));
    }
}
