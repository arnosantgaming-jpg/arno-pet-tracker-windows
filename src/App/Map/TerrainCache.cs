using System.Collections.Concurrent;
using SkiaSharp;

namespace ArnoA2.App.Map;

/// <summary>Bundled map tiles decoded to reduced gray images for minimap matching. A 1/4
/// tile is 256 KB of floats, so the few tiles around the player stay cached.</summary>
public sealed class TerrainCache(string root) : ITerrain
{
    readonly ConcurrentDictionary<(string, int, int, int), GrayImage?> tiles = new();

    public GrayImage? Tile(WorldMap map, int column, int row, int down) => tiles.GetOrAdd((map.Key, column, row, down), _ => Load(map, column, row, down));

    GrayImage? Load(WorldMap map, int column, int row, int down)
    {
        string path = Path.Combine(root, map.Key, $"{column}_{row}.jpg");
        if (!File.Exists(path))
            return null;
        using var source = SKBitmap.Decode(path);
        if (source == null)
            return null;
        int size = map.Tile / down;
        using var small = source.Resize(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
        var gray = new GrayImage(size, size);
        var pixels = small.GetPixelSpan();
        for (int i = 0; i < size * size; i++)
            gray.Pixels[i] = 0.299f * pixels[i * 4] + 0.587f * pixels[i * 4 + 1] + 0.114f * pixels[i * 4 + 2];
        return gray;
    }
}
