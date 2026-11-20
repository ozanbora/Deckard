using Deckard.Core.Capture;
using Deckard.Perception.Zones;

namespace Deckard.Perception.Tests;

public class ZoneReaderTests
{
    [Theory]
    // Read from a 3440x1440 screenshot: the clock, the zone, then the TAB key hint for the map.
    [InlineData("9:25 AM Backwater TAB", "Backwater")]
    [InlineData("Ked Bardu TAB 11:02 PM", "Ked Bardu")]
    [InlineData("Kyovashad TAB 0:07", "Kyovashad")]
    [InlineData("Ruins of Rakhat Keep TAB 7.41 pm", "Ruins of Rakhat Keep")]
    public void ReadsTheZoneBesideTheClockAndTabHint(string ocr, string zone) => Assert.Equal(zone, ZoneReader.Parse(ocr));

    [Theory]
    [InlineData("")]
    [InlineData("12:00 TAB")]   // no zone shown, e.g. in a menu
    [InlineData("ab")]          // OCR noise
    public void ReadsNothingWhenNoNameIsShown(string ocr) => Assert.Null(ZoneReader.Parse(ocr));

    [Fact]
    public void LooksAtTheStripAboveTheMinimapMeasuredFromTheHeight()
    {
        // The HUD scales with the height, so ultrawide and 16:9 put the strip the same distance from the right edge.
        Assert.Equal(new PixelRect(2720, 0, 720, 50), ZoneReader.Region(3440, 1440));
        Assert.Equal(new PixelRect(1200, 0, 720, 50), ZoneReader.Region(1920, 1440));
        Assert.Equal(new PixelRect(1380, 0, 540, 37), ZoneReader.Region(1920, 1080));
    }
}
