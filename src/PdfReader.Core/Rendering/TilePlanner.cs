namespace PdfReader.Core.Rendering;

/// <summary>A square region of a fully scaled page, in pixels.</summary>
public readonly record struct Tile(int Column, int Row, int X, int Y, int Width, int Height);

/// <summary>
/// Decides how a page is rasterized at a given scale: a single bitmap when it is small enough,
/// otherwise a grid of fixed-size tiles of which only the visible ones are rendered.
/// </summary>
public static class TilePlanner
{
    public const int TileSize = 512;

    /// <summary>Pages up to this many pixels are rendered as one bitmap (≈ 4K × 2K).</summary>
    public const long MaxSingleBitmapPixels = 8L * 1024 * 1024;

    public static (int Width, int Height) ScaledSize(double widthPt, double heightPt, double scale) =>
        (Math.Max(1, (int)Math.Round(widthPt * scale)), Math.Max(1, (int)Math.Round(heightPt * scale)));

    public static bool NeedsTiling(int pixelWidth, int pixelHeight) =>
        (long)pixelWidth * pixelHeight > MaxSingleBitmapPixels;

    /// <summary>
    /// Tiles of a <paramref name="pixelWidth"/> × <paramref name="pixelHeight"/> page intersecting the
    /// visible pixel region, ordered from the center of that region outwards.
    /// </summary>
    public static IReadOnlyList<Tile> VisibleTiles(int pixelWidth, int pixelHeight,
        double visibleX, double visibleY, double visibleWidth, double visibleHeight, int tileSize = TileSize)
    {
        double x0 = Math.Max(0, visibleX), y0 = Math.Max(0, visibleY);
        double x1 = Math.Min(pixelWidth, visibleX + visibleWidth), y1 = Math.Min(pixelHeight, visibleY + visibleHeight);
        if (x1 <= x0 || y1 <= y0) return [];

        int c0 = (int)(x0 / tileSize), c1 = (int)Math.Ceiling(x1 / tileSize) - 1;
        int r0 = (int)(y0 / tileSize), r1 = (int)Math.Ceiling(y1 / tileSize) - 1;
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;

        var tiles = new List<Tile>((c1 - c0 + 1) * (r1 - r0 + 1));
        for (int r = r0; r <= r1; r++)
        for (int c = c0; c <= c1; c++)
        {
            int x = c * tileSize, y = r * tileSize;
            tiles.Add(new Tile(c, r, x, y, Math.Min(tileSize, pixelWidth - x), Math.Min(tileSize, pixelHeight - y)));
        }
        tiles.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
        return tiles;

        double Distance(Tile t)
        {
            double dx = t.X + t.Width / 2.0 - cx, dy = t.Y + t.Height / 2.0 - cy;
            return dx * dx + dy * dy;
        }
    }
}
