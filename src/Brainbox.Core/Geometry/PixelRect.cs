namespace Brainbox.Core.Geometry;

/// <summary>
/// Integer rectangle in physical pixels. Unless stated otherwise every rectangle in the engine
/// is expressed in the Windows <b>virtual desktop</b> coordinate space (the union of all monitors,
/// whose origin can be negative when a monitor sits left of / above the primary one).
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static readonly PixelRect Empty = new(0, 0, 0, 0);

    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public long Area => IsEmpty ? 0 : (long)Width * Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;

    public static PixelRect FromLTRB(int left, int top, int right, int bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public PixelRect Intersect(PixelRect other)
    {
        var l = Math.Max(Left, other.Left);
        var t = Math.Max(Top, other.Top);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? Empty : FromLTRB(l, t, r, b);
    }

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return FromLTRB(Math.Min(Left, other.Left), Math.Min(Top, other.Top),
            Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    public PixelRect Inflate(int dx, int dy) => FromLTRB(Left - dx, Top - dy, Right + dx, Bottom + dy);

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    public bool IntersectsWith(PixelRect other) => !Intersect(other).IsEmpty;

    public bool Contains(double x, double y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public bool Contains(PixelRect other) =>
        other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;

    /// <summary>Intersection-over-union, 0..1.</summary>
    public double IoU(PixelRect other)
    {
        var inter = Intersect(other).Area;
        if (inter == 0) return 0;
        var union = Area + other.Area - inter;
        return union <= 0 ? 0 : (double)inter / union;
    }

    /// <summary>Fraction (0..1) of THIS rectangle covered by <paramref name="other"/>.</summary>
    public double CoveredBy(PixelRect other)
    {
        var a = Area;
        return a == 0 ? 0 : (double)Intersect(other).Area / a;
    }

    public override string ToString() => $"[{X},{Y} {Width}x{Height}]";
}
