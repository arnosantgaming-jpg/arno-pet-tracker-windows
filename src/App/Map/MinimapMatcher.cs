namespace ArnoA2.App.Map;

/// <summary>Single-channel float image, row-major.</summary>
public sealed class GrayImage(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public float[] Pixels { get; } = new float[width * height];
    public float this[int x, int y]
    {
        get => Pixels[y * Width + x];
        set => Pixels[y * Width + x] = value;
    }
}

/// <summary>Terrain at a power-of-two reduction: tile (col,row) is TileSize/Down pixels square.
/// Null for tiles outside the map.</summary>
public interface ITerrain
{
    GrayImage? Tile(WorldMap map, int column, int row, int down);
}

public sealed record MinimapFix(double X, double Y, double Score, double Ratio);

/// <summary>
/// Locates the in-game minimap (north-up, centred on the player) on the bundled region
/// tiles with masked normalized cross-correlation over brightness and gradient. Ratio is
/// tile pixels per minimap pixel (2.75 at 1080p with the default minimap zoom). Icons, the
/// range rings, route lines and the view cone are masked out. Coarse search at 1/8 tile
/// resolution, refined at 1/4: about 2 m precision.
/// </summary>
public static class MinimapMatcher
{
    public const double DefaultRatio = 2.75, MinScore = 0.35;

    /// <summary>True where the minimap pixel shows terrain.</summary>
    public static bool Terrain(byte r, byte g, byte b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        bool cyan = b > r + 20 && g > r + 15;
        bool white = min > 170;
        bool saturatedBlue = max - min > 0.5 * (max + 1) && b > 150;
        return !(cyan || white || saturatedBlue);
    }

    /// <summary>Area-average resample of RGB minimap pixels into gray + mask at the target size.</summary>
    public static (GrayImage Gray, bool[] Mask) Template(byte[] rgb, int width, int height, int targetWidth, int targetHeight)
    {
        var gray = new GrayImage(targetWidth, targetHeight);
        var mask = new bool[targetWidth * targetHeight];
        double sx = (double)width / targetWidth, sy = (double)height / targetHeight;
        for (int ty = 0; ty < targetHeight; ty++)
        for (int tx = 0; tx < targetWidth; tx++)
        {
            int x0 = (int)(tx * sx), x1 = Math.Max(x0 + 1, (int)((tx + 1) * sx)), y0 = (int)(ty * sy), y1 = Math.Max(y0 + 1, (int)((ty + 1) * sy));
            double sum = 0;
            int good = 0, all = 0;
            for (int y = y0; y < y1 && y < height; y++)
            for (int x = x0; x < x1 && x < width; x++)
            {
                int i = (y * width + x) * 3;
                all++;
                if (!Terrain(rgb[i], rgb[i + 1], rgb[i + 2]))
                    continue;
                good++;
                sum += 0.299 * rgb[i] + 0.587 * rgb[i + 1] + 0.114 * rgb[i + 2];
            }

            int k = ty * targetWidth + tx;
            mask[k] = all > 0 && good * 2 > all;
            gray.Pixels[k] = good > 0 ? (float)(sum / good) : 0;
        }

        return (gray, mask);
    }

    public static GrayImage Gradient(GrayImage image)
    {
        var g = new GrayImage(image.Width, image.Height);
        for (int y = 1; y < image.Height - 1; y++)
        for (int x = 1; x < image.Width - 1; x++)
        {
            float dx = image[x + 1, y] - image[x - 1, y], dy = image[x, y + 1] - image[x, y - 1];
            g[x, y] = MathF.Sqrt(dx * dx + dy * dy);
        }

        return g;
    }

    /// <summary>Terrain window of size w×h at down-scaled pixel origin (x0,y0); missing tiles are 0.</summary>
    public static GrayImage Window(ITerrain terrain, WorldMap map, int down, int x0, int y0, int w, int h)
    {
        var window = new GrayImage(w, h);
        int tile = map.Tile / down;
        for (int row = Math.Max(0, Floor(y0, tile)); row <= Floor(y0 + h - 1, tile) && row < map.Grid; row++)
        for (int col = Math.Max(0, Floor(x0, tile)); col <= Floor(x0 + w - 1, tile) && col < map.Grid; col++)
        {
            if (terrain.Tile(map, col, row, down) is not { } source)
                continue;
            int ox = col * tile - x0, oy = row * tile - y0;
            for (int y = Math.Max(0, oy); y < Math.Min(h, oy + tile); y++)
                Array.Copy(source.Pixels, (y - oy) * tile + Math.Max(0, -ox), window.Pixels, y * w + Math.Max(0, ox), Math.Min(w, ox + tile) - Math.Max(0, ox));
        }

        return window;
    }

    static int Floor(int value, int size) => (int)Math.Floor((double)value / size);

    sealed class Prepared
    {
        public required int Width, Height;
        public required int[] Index;
        public required float[] Gray, Edge;
        public required double GrayNorm, EdgeNorm;
    }

    static Prepared? Prepare(byte[] rgb, int width, int height, double ratio, int down)
    {
        int tw = (int)Math.Round(width * ratio / down), th = (int)Math.Round(height * ratio / down);
        if (tw < 12 || th < 8)
            return null;
        var (gray, mask) = Template(rgb, width, height, tw, th);
        var edge = Gradient(gray);
        var index = new List<int>();
        for (int y = 1; y < th - 1; y++)
        for (int x = 1; x < tw - 1; x++)
            if (mask[y * tw + x] && mask[y * tw + x - 1] && mask[y * tw + x + 1] && mask[(y - 1) * tw + x] && mask[(y + 1) * tw + x])
                index.Add(y * tw + x);
        if (index.Count < tw * th / 4)
            return null;
        float[] Center(GrayImage image, out double norm)
        {
            double mean = index.Average(i => image.Pixels[i]);
            var values = index.Select(i => (float)(image.Pixels[i] - mean)).ToArray();
            norm = Math.Sqrt(values.Sum(v => (double)v * v));
            return values;
        }

        var g = Center(gray, out double gn);
        var e = Center(edge, out double en);
        return new() { Width = tw, Height = th, Index = index.ToArray(), Gray = g, Edge = e, GrayNorm = gn, EdgeNorm = en };
    }

    static double Score(Prepared t, GrayImage gray, GrayImage edge, int ox, int oy)
    {
        int n = t.Index.Length, stride = gray.Width;
        double gNum = 0, gSum = 0, gSq = 0, eNum = 0, eSum = 0, eSq = 0;
        var gp = gray.Pixels;
        var ep = edge.Pixels;
        for (int k = 0; k < n; k++)
        {
            int i = t.Index[k], at = (oy + i / t.Width) * stride + ox + i % t.Width;
            float gv = gp[at], ev = ep[at];
            gNum += t.Gray[k] * gv;
            gSum += gv;
            gSq += gv * gv;
            eNum += t.Edge[k] * ev;
            eSum += ev;
            eSq += ev * ev;
        }

        double gVar = gSq - gSum * gSum / n, eVar = eSq - eSum * eSum / n;
        // Flat windows (missing tiles, std < 6) cannot be matched.
        if (gVar < n * 36 || t.GrayNorm <= 0 || t.EdgeNorm <= 0)
            return -1;
        return 0.5 * gNum / (t.GrayNorm * Math.Sqrt(gVar)) + 0.5 * eNum / (t.EdgeNorm * Math.Sqrt(Math.Max(eVar, 1e-6)));
    }

    /// <summary>Best placement of the minimap centre within radiusMetres of (cx,cy).</summary>
    public static MinimapFix? Locate(ITerrain terrain, WorldMap map, byte[] rgb, int width, int height, double cx, double cy, double radiusMetres, double ratio)
    {
        var coarse = Search(terrain, map, rgb, width, height, cx, cy, radiusMetres, ratio, 8, 1);
        if (coarse == null)
            return null;
        // Refine at 1/4 within two coarse pixels (8 tile px each).
        double coarsePixelMetres = 8 * map.Metres / map.Span;
        return Search(terrain, map, rgb, width, height, coarse.X, coarse.Y, 2 * coarsePixelMetres, ratio, 4, 1) ?? coarse;
    }

    static MinimapFix? Search(ITerrain terrain, WorldMap map, byte[] rgb, int width, int height, double cx, double cy, double radiusMetres, double ratio, int down, int step)
    {
        var t = Prepare(rgb, width, height, ratio, down);
        if (t == null)
            return null;
        double pixelsPerMetre = map.Span / map.Metres / down;
        int radius = Math.Max(1, (int)Math.Ceiling(radiusMetres * pixelsPerMetre));
        // Window origin so that offset (radius, radius) places the template centre at (cx,cy).
        int centerX = (int)Math.Round(cx * map.Span / down), centerY = (int)Math.Round(cy * map.Span / down);
        int x0 = centerX - t.Width / 2 - radius, y0 = centerY - t.Height / 2 - radius;
        var gray = Window(terrain, map, down, x0, y0, t.Width + 2 * radius, t.Height + 2 * radius);
        var edge = Gradient(gray);
        double best = double.NegativeInfinity;
        int bx = 0, by = 0;
        for (int oy = 0; oy <= 2 * radius; oy += step)
        for (int ox = 0; ox <= 2 * radius; ox += step)
        {
            double score = Score(t, gray, edge, ox, oy);
            if (score > best)
            {
                best = score;
                bx = ox;
                by = oy;
            }
        }

        if (best < MinScore)
            return null;
        double px = (x0 + bx + t.Width / 2.0) * down / map.Span, py = (y0 + by + t.Height / 2.0) * down / map.Span;
        return new(px, py, best, ratio);
    }

    /// <summary>Find the minimap scale near a trusted position (first lock or after a zoom change).</summary>
    public static MinimapFix? Calibrate(ITerrain terrain, WorldMap map, byte[] rgb, int width, int height, double cx, double cy, double radiusMetres)
    {
        MinimapFix? best = null;
        for (double ratio = 1.8; ratio <= 4.2; ratio += 0.15)
            if (Search(terrain, map, rgb, width, height, cx, cy, radiusMetres, ratio, 8, 1) is { } fix && (best == null || fix.Score > best.Score))
                best = fix;
        if (best == null)
            return null;
        MinimapFix? refined = null;
        for (double ratio = best.Ratio - 0.12; ratio <= best.Ratio + 0.121; ratio += 0.04)
            if (Search(terrain, map, rgb, width, height, best.X, best.Y, 12, ratio, 4, 1) is { } fix && (refined == null || fix.Score > refined.Score))
                refined = fix;
        return refined ?? best;
    }
}
