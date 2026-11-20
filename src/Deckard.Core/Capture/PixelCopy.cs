namespace Deckard.Core.Capture;

public static class PixelCopy
{
    /// <summary>Copies <paramref name="rows"/> rows of <paramref name="rowBytes"/> each between buffers with different pitches.</summary>
    public static void CopyRows(ReadOnlySpan<byte> source, int sourcePitch, Span<byte> destination, int destinationPitch, int rowBytes, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourcePitch, rowBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationPitch, rowBytes);
        if (rows <= 0)
            return;

        if (sourcePitch == rowBytes && destinationPitch == rowBytes)
        {
            source[..(rowBytes * rows)].CopyTo(destination);
            return;
        }

        for (var y = 0; y < rows; y++)
            source.Slice(y * sourcePitch, rowBytes).CopyTo(destination.Slice(y * destinationPitch, rowBytes));
    }
}
