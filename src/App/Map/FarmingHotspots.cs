namespace ArnoA2.App.Map;

public sealed record Hotspot(double X, double Y, int Spawns, double Value, IReadOnlyList<int> PetIds, double? DistanceMetres, double Score);

/// <summary>
/// Groups static spawn points of the wanted pets into farming spots. A spot's value is the
/// sum of its spawns' weights within <see cref="RadiusMetres"/>; nearer spots rank higher
/// when the player's position is known. Spawn dots are approximate and say nothing about
/// which mobs are alive, so this ranks places, not live targets.
/// </summary>
public static class FarmingHotspots
{
    public const double RadiusMetres = 60;
    const double TravelMetres = 600;

    /// <summary>How much one more kill of this pet is worth: 0 when maxed or not wanted,
    /// more when many souls remain to the next rank.</summary>
    public static double Weight(ArnoA2.Tracker.PetProgress? pet)
    {
        if (pet == null || pet.Rank == null)
            return 1.5; // rank not known: worth farming until proven otherwise
        if (pet.Max)
            return 0;
        if (pet.Balance is int balance && pet.Requirement is int need && need > 0)
            return 1 + Math.Clamp((need - balance) / (double)need, 0, 1);
        return 1.5;
    }

    public static IReadOnlyList<Hotspot> Find(SpawnRegion region, WorldMap map, IReadOnlyDictionary<int, double> weights, (double X, double Y)? player, int limit = 8)
    {
        var points = region.Points.Where(p => weights.GetValueOrDefault(p.PetId) > 0).ToArray();
        if (points.Length == 0)
            return Array.Empty<Hotspot>();
        double radius = RadiusMetres / map.Metres, radius2 = radius * radius;
        // Grid hash with cell = radius, so each neighbourhood query visits 9 cells.
        var cells = new Dictionary<(int, int), List<int>>();
        (int, int) Cell(double x, double y) => ((int)Math.Floor(x / radius), (int)Math.Floor(y / radius));
        for (int i = 0; i < points.Length; i++)
        {
            var key = Cell(points[i].X, points[i].Y);
            if (!cells.TryGetValue(key, out var list))
                cells[key] = list = new();
            list.Add(i);
        }

        IEnumerable<int> Near(double x, double y)
        {
            var (cx, cy) = Cell(x, y);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (cells.TryGetValue((cx + dx, cy + dy), out var list))
                    foreach (int i in list)
                        if (Math.Pow(points[i].X - x, 2) + Math.Pow(points[i].Y - y, 2) <= radius2)
                            yield return i;
        }

        var candidates = new List<Hotspot>();
        for (int i = 0; i < points.Length; i++)
        {
            var members = Near(points[i].X, points[i].Y).ToArray();
            double value = members.Sum(m => weights[points[m].PetId]);
            double sx = members.Sum(m => points[m].X * weights[points[m].PetId]) / value, sy = members.Sum(m => points[m].Y * weights[points[m].PetId]) / value;
            double? distance = player is { } p ? Math.Sqrt(Math.Pow(sx - p.X, 2) + Math.Pow(sy - p.Y, 2)) * map.Metres : null;
            double score = value / (1 + (distance ?? 0) / TravelMetres);
            candidates.Add(new(sx, sy, members.Length, value, members.Select(m => points[m].PetId).Distinct().ToArray(), distance, score));
        }

        // Greedy suppression: keep the best spot, drop overlapping ones, repeat.
        var chosen = new List<Hotspot>();
        foreach (var spot in candidates.OrderByDescending(c => c.Score))
        {
            if (chosen.Any(c => Math.Pow(c.X - spot.X, 2) + Math.Pow(c.Y - spot.Y, 2) < 4 * radius2))
                continue;
            chosen.Add(spot);
            if (chosen.Count == limit)
                break;
        }

        return chosen;
    }

    /// <summary>Compass bearing from the player to a spot, clockwise from map north (up).</summary>
    public static double Bearing((double X, double Y) from, (double X, double Y) to) => (Math.Atan2(to.X - from.X, -(to.Y - from.Y)) * 180 / Math.PI + 360) % 360;
}
