using Deckard.Core.Capture;

namespace Deckard.Core.Tests;

public class PixelCopyTests
{
    [Fact]
    public void CopyRows_StripsSourcePadding()
    {
        // 2 rows of 8 bytes, each followed by 4 bytes of GPU row padding.
        byte[] source = [1, 2, 3, 4, 5, 6, 7, 8, 0xEE, 0xEE, 0xEE, 0xEE, 9, 10, 11, 12, 13, 14, 15, 16, 0xEE, 0xEE, 0xEE, 0xEE];
        var destination = new byte[16];

        PixelCopy.CopyRows(source, sourcePitch: 12, destination, destinationPitch: 8, rowBytes: 8, rows: 2);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], destination);
    }

    [Fact]
    public void CopyRows_TightlyPackedCopiesInOneBlock()
    {
        byte[] source = [1, 2, 3, 4, 5, 6, 7, 8];
        var destination = new byte[8];

        PixelCopy.CopyRows(source, 4, destination, 4, 4, 2);

        Assert.Equal(source, destination);
    }

    [Fact]
    public void CopyRows_RejectsPitchSmallerThanRow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelCopy.CopyRows(new byte[8], 2, new byte[8], 4, 4, 2));
    }

    [Fact]
    public void Frame_IsTightlyPackedAndUnusableAfterDispose()
    {
        var frame = Frame.Create(3, 2, TimeSpan.Zero);
        Assert.Equal(12, frame.Stride);
        Assert.Equal(24, frame.Pixels.Length);

        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(() => frame.Pixels.Length);
    }
}
