using Deckard.Core.Capture;
using Deckard.Perception.Imaging;
using static Deckard.Perception.Tests.TestScenes;

namespace Deckard.Perception.Tests;

public class ImageOpsTests
{
    [Fact]
    public void TellsABlackCaptureFromADarkScene()
    {
        using var black = Frame.Create(1920, 1080, TimeSpan.Zero);
        Assert.True(ImageOps.IsBlack(black));

        using var scene = Scene(1920, 1080);
        Assert.False(ImageOps.IsBlack(scene));

        // A black frame with only a small lit patch (the HUD in a dark dungeon) isn't a black capture.
        var pixels = black.Pixels;
        for (var y = 980; y < 1080; y++)
            for (var x = 860; x < 1060; x++)
                pixels[y * black.Stride + x * Frame.BytesPerPixel + 2] = 120;
        Assert.False(ImageOps.IsBlack(black));
    }

    [Fact]
    public void HalvesByAveragingBlocks()
    {
        using var frame = Frame.Create(4, 2, TimeSpan.Zero);
        frame.Pixels.Clear();
        frame.Pixels.Slice(0, 4).Fill(100);           // (0,0)
        frame.Pixels.Slice(4 * 4 + 4, 4).Fill(200);   // (1,1): the same 2x2 block
        using var half = ImageOps.Halve(frame);
        Assert.Equal((2, 1), (half.Width, half.Height));
        Assert.Equal(new byte[] { 75, 75, 75, 75 }, half.Pixels.Slice(0, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, half.Pixels.Slice(4, 4).ToArray());
    }
}
