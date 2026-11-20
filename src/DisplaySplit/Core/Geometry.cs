namespace DisplaySplit.Core;

public readonly record struct PointI(int X, int Y);

/// <summary>Integer rectangle, right/bottom exclusive (same convention as Win32 RECT).</summary>
public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public PointI Center => new((Left + Right) / 2, (Top + Bottom) / 2);

    public static RectI FromXYWH(int x, int y, int w, int h) => new(x, y, x + w, y + h);

    public bool Contains(PointI p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
    public bool Contains(RectI r) => r.Left >= Left && r.Top >= Top && r.Right <= Right && r.Bottom <= Bottom;

    public RectI Inflate(int dx, int dy) => new(Left - dx, Top - dy, Right + dx, Bottom + dy);

    public RectI Intersect(RectI o)
    {
        var r = new RectI(Math.Max(Left, o.Left), Math.Max(Top, o.Top), Math.Min(Right, o.Right), Math.Min(Bottom, o.Bottom));
        return r.IsEmpty ? default : r;
    }

    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public bool NearlyEquals(RectI o, int tol) =>
        Math.Abs(Left - o.Left) <= tol && Math.Abs(Top - o.Top) <= tol &&
        Math.Abs(Right - o.Right) <= tol && Math.Abs(Bottom - o.Bottom) <= tol;

    /// <summary>Rect expressed as fractions of <paramref name="outer"/>.</summary>
    public RectF ToFraction(RectI outer) => outer.IsEmpty ? new RectF(0, 0, 1, 1) : new RectF(
        (double)(Left - outer.Left) / outer.Width, (double)(Top - outer.Top) / outer.Height,
        (double)(Right - outer.Left) / outer.Width, (double)(Bottom - outer.Top) / outer.Height);

    /// <summary>Centre the given size inside this rect (clamped to the rect's origin if larger).</summary>
    public RectI CenterSize(int w, int h)
    {
        int x = Left + (Width - w) / 2, y = Top + (Height - h) / 2;
        return FromXYWH(Math.Max(Left, x), Math.Max(Top, y), w, h);
    }

    public override string ToString() => $"[{Left},{Top} {Width}x{Height}]";
}

/// <summary>Rectangle in fractional (0..1) coordinates relative to some outer rect.</summary>
public readonly record struct RectF(double Left, double Top, double Right, double Bottom)
{
    public RectI Resolve(RectI outer) => new(
        outer.Left + (int)Math.Round(Left * outer.Width), outer.Top + (int)Math.Round(Top * outer.Height),
        outer.Left + (int)Math.Round(Right * outer.Width), outer.Top + (int)Math.Round(Bottom * outer.Height));
}
