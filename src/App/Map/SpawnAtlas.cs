using System.Text.Json;

namespace ArnoA2.App.Map;

public sealed record SpawnPoint(int PetId, int NpcId, string Name, double X, double Y);
public sealed record MapTile(int Column, int Row);

/// <summary>A region map: which tiles exist and where pet creatures spawn (normalized 0..1).</summary>
public sealed record SpawnRegion(string Key, string Name, int Grid, IReadOnlyList<MapTile> Tiles, IReadOnlyList<SpawnPoint> Points);

/// <summary>Spawn data for the bundled regions (data/FarmingSpawns.json, see THIRD-PARTY-NOTICES.txt).</summary>
public static class SpawnAtlas
{
    public static IReadOnlyList<SpawnRegion> Regions { get; } = Load();

    public static SpawnRegion Region(string key) => Regions.First(r => r.Key == key);

    static IReadOnlyList<SpawnRegion> Load()
    {
        using var stream = typeof(SpawnAtlas).Assembly.GetManifestResourceStream("spawns.json") ?? throw new InvalidDataException("Missing spawn data");
        return JsonSerializer.Deserialize<SpawnRegion[]>(stream) ?? throw new InvalidDataException("Empty spawn data");
    }
}
