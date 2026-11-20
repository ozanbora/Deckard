namespace Deckard.Core.Capture;

/// <summary>An axis-aligned rectangle in frame pixel coordinates.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int Area => Width * Height;

    public bool Contains(PixelRect other) =>
        other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    public PixelRect Intersect(PixelRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right > x && bottom > y ? new PixelRect(x, y, right - x, bottom - y) : default;
    }

    public PixelRect Union(PixelRect other)
    {
        if (Area == 0)
            return other;
        if (other.Area == 0)
            return this;
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new PixelRect(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };
}
