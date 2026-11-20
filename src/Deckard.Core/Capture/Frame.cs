using System.Buffers;

namespace Deckard.Core.Capture;

/// <summary>
/// A captured BGRA8 frame, tightly packed (<see cref="Stride"/> == <see cref="Width"/> * 4).
/// The pixel buffer is rented from <see cref="ArrayPool{T}"/>; dispose the frame to return it.
/// </summary>
public sealed class Frame : IDisposable
{
    public const int BytesPerPixel = 4;

    private byte[]? _buffer;

    private Frame(int width, int height, TimeSpan timestamp, byte[] buffer)
    {
        Width = width;
        Height = height;
        Timestamp = timestamp;
        _buffer = buffer;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * BytesPerPixel;

    /// <summary>System-relative time the frame was presented (QPC based).</summary>
    public TimeSpan Timestamp { get; }

    public Span<byte> Pixels
    {
        get
        {
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            return _buffer.AsSpan(0, Stride * Height);
        }
    }

    public static Frame Create(int width, int height, TimeSpan timestamp)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var buffer = ArrayPool<byte>.Shared.Rent(width * height * BytesPerPixel);
        return new Frame(width, height, timestamp, buffer);
    }

    /// <summary>Copies a region into a new frame (clipped to this frame). The caller owns the result.</summary>
    public Frame Crop(PixelRect region)
    {
        var clipped = region.Intersect(new PixelRect(0, 0, Width, Height));
        if (clipped.Area == 0)
            throw new ArgumentOutOfRangeException(nameof(region), region, "Region lies outside the frame.");

        var crop = Create(clipped.Width, clipped.Height, Timestamp);
        PixelCopy.CopyRows(Pixels[(clipped.Y * Stride + clipped.X * BytesPerPixel)..], Stride,
            crop.Pixels, crop.Stride, clipped.Width * BytesPerPixel, clipped.Height);
        return crop;
    }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}
