using System.Text.Json;
using ArnoA2.App.Map;
using ArnoA2.Tracker;
using Avalonia.Threading;

namespace ArnoA2.App;

public sealed class MapPreferences
{
    public string Target { get; set; } = "Unfinished";
    public int? PetId { get; set; }
    public double MiniOpacity { get; set; } = 0.8;
    public double MiniZoom { get; set; } = 5;
    public double FullZoom { get; set; } = 1;
    public int? MiniX { get; set; }
    public int? MiniY { get; set; }
    public double MiniWidth { get; set; } = 300;
    public double MiniHeight { get; set; } = 300;
    public bool ShowAllSpawns { get; set; } = true;
    /// <summary>Mini-map shape: "Round" or "Rectangle".</summary>
    public string MiniShape { get; set; } = "Round";
    public string? LastRegion { get; set; } = "verteron";
    public bool ReadMinimap { get; set; } = true;
    /// <summary>Tile pixels per minimap pixel, found by calibration (null = calibrate).</summary>
    public double? MinimapRatio { get; set; }
}

/// <summary>Shared live state for the map windows: follows the player's position while a map
/// is open, keeps the farming target and recomputes the ranked hotspots.</summary>
public sealed class MapHub
{
    public static readonly string[] Targets = { "Unfinished", "Pinned", "Cogni", "Fera", "Natura", "Varian", "Special", "Pet" };
    readonly AppState app;
    readonly string file;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    int open;
    MinimapTracker? minimap;
    (double X, double Y, DateTime At)? minimapFix;
    (double X, double Y)? network;
    (double X, double Y)? lastHotspotPlayer;
    string hotspotKey = "";
    public MapHub(AppState app, string root)
    {
        this.app = app;
        file = Path.Combine(root, "map-settings.json");
        try
        {
            if (File.Exists(file))
                Preferences = JsonSerializer.Deserialize<MapPreferences>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception)
        {
            Preferences = new();
        }

        timer.Tick += (_, _) => Poll();
    }

    public MapPreferences Preferences { get; } = new();
    public LiveMapView? Live { get; private set; }
    public string? Error { get; private set; }
    /// <summary>The region is remembered, not confirmed by a zone frame or nearby spawns.</summary>
    public bool Assumed { get; private set; }
    public string Region { get; private set; } = "verteron";
    public (double X, double Y)? Player { get; private set; }
    /// <summary>Where the shown position comes from: Minimap, Exact (network), Estimated.</summary>
    public string Source { get; private set; } = "";
    public bool Precise => Source is "Minimap" or "Exact";
    public string MinimapStatus { get; private set; } = "";
    public IReadOnlyList<Hotspot> Hotspots { get; private set; } = Array.Empty<Hotspot>();
    public int TargetSpot { get; set; }
    public IReadOnlyDictionary<int, double> Weights { get; private set; } = new Dictionary<int, double>();
    public WorldMap Map => WorldMaps.All[Region];
    public SpawnRegion Spawns => SpawnAtlas.Region(Region);
    public event Action? Changed;

    public void Attach()
    {
        if (open++ == 0)
        {
            timer.Start();
            SetMinimapReading(Preferences.ReadMinimap);
        }

        Poll();
    }

    public void Detach()
    {
        if (--open <= 0)
        {
            open = 0;
            timer.Stop();
            minimap?.Dispose();
            minimap = null;
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(Preferences));
        }
        catch (Exception)
        {
        }
    }

    public void SetMinimapReading(bool enabled)
    {
        Preferences.ReadMinimap = enabled;
        if (enabled && minimap == null && open > 0)
            minimap = new MinimapTracker(this);
        else if (!enabled && minimap != null)
        {
            minimap.Dispose();
            minimap = null;
            minimapFix = null;
            MinimapStatus = "";
        }
    }

    /// <summary>Forget the minimap scale (after changing resolution or minimap zoom).</summary>
    public void RecalibrateMinimap()
    {
        Preferences.MinimapRatio = null;
        Save();
        if (minimap != null)
        {
            minimap.Dispose();
            minimap = open > 0 && Preferences.ReadMinimap ? new MinimapTracker(this) : null;
        }
    }

    public void AcceptMinimap(MinimapFix? fix, string? status, string? region)
    {
        MinimapStatus = fix != null ? $"Minimap lock ({fix.Score:F2})" : status ?? "";
        if (fix != null && region == Region)
        {
            minimapFix = (fix.X, fix.Y, DateTime.UtcNow);
            Move((fix.X, fix.Y), "Minimap");
        }

        Changed?.Invoke();
    }

    bool MinimapFresh => minimapFix is { } m && (DateTime.UtcNow - m.At).TotalSeconds < 1.5;

    void Move((double X, double Y) next, string source)
    {
        if (Player is { } previous)
        {
            double metres = Math.Sqrt(Math.Pow(next.X - previous.X, 2) + Math.Pow(next.Y - previous.Y, 2)) * Map.Metres;
            if (metres < 1.5 && source != "Minimap")
                next = previous;
        }

        Player = next;
        Source = source;
    }

    public void SetRegion(string region)
    {
        if (Region == region || !WorldMaps.All.ContainsKey(region))
            return;
        Region = region;
        Refresh(force: true);
    }

    public void SetTarget(string target, int? petId = null)
    {
        Preferences.Target = target;
        Preferences.PetId = petId;
        Save();
        TargetSpot = 0;
        Refresh(force: true);
    }

    /// <summary>The collection or settings changed: re-rank farming spots.</summary>
    public void CollectionChanged() => Refresh(force: false);

    void Poll()
    {
        Error = app.CaptureProblem ? app.CaptureStatus : null;
        Accept(app.Positions.View(DateTimeOffset.UtcNow));
    }

    public void Accept(LiveMapView live)
    {
        // Zone 0 means no zone frame was seen (started mid-session) and no pet mob placed
        // the player yet: assume the last region a map was shown for until one does.
        string? region = live.Region ?? (live.Zone == 0 ? Preferences.LastRegion : null);
        Assumed = live.Region == null && region != null;
        Live = live;
        if (region != null)
            Region = region;
        if (live.Region != null && Preferences.LastRegion != live.Region)
        {
            Preferences.LastRegion = live.Region;
            Save();
        }

        if (region != null && live.X is float x && live.Y is float y)
        {
            network = Map.Normalize(x, y);
            minimap?.Update(new(region, network.Value.X, network.Value.Y, live.Exact));
            // A fresh minimap match is more precise than anything the network sends.
            if (!MinimapFresh)
                Move(network.Value, live.Exact ? "Exact" : "Estimated");
        }
        else if (region == null)
        {
            network = null;
            minimap?.Update(null);
            Player = null;
            Source = "";
        }

        Refresh(force: false);
    }

    void Refresh(bool force)
    {
        var collection = app.Engine.Collection;
        var pins = app.Engine.Settings.Pins;
        var weights = new Dictionary<int, double>();
        foreach (var entry in GameData.Instance.Pets.Values)
        {
            var pet = collection.Pets.GetValueOrDefault(entry.Id);
            bool wanted = Preferences.Target switch
            {
                "Pinned" => pins.Contains(entry.Id),
                "Pet" => Preferences.PetId == entry.Id,
                "Unfinished" => true,
                string family => entry.Family.ToString() == family
            };
            double weight = wanted ? FarmingHotspots.Weight(pet) : 0;
            if (weight > 0)
                weights[entry.Id] = weight;
        }

        string key = Region + "|" + string.Join(",", weights.OrderBy(w => w.Key).Select(w => w.Key + ":" + w.Value.ToString("F2")));
        bool moved = Player is { } p && (lastHotspotPlayer is not { } last || Math.Sqrt(Math.Pow(p.X - last.X, 2) + Math.Pow(p.Y - last.Y, 2)) * Map.Metres > 40);
        if (force || moved || key != hotspotKey)
        {
            hotspotKey = key;
            lastHotspotPlayer = Player;
            Weights = weights;
            Hotspots = FarmingHotspots.Find(Spawns, Map, weights, Player);
            if (TargetSpot >= Hotspots.Count)
                TargetSpot = 0;
        }

        Changed?.Invoke();
    }

    public Hotspot? Target => Hotspots.Count > TargetSpot ? Hotspots[TargetSpot] : null;

    public string PetNames(IEnumerable<int> ids, int limit = 3)
    {
        var names = ids.Select(id => GameData.Instance.Pets.GetValueOrDefault(id)?.Name).Where(n => n != null).ToArray();
        return names.Length <= limit ? string.Join(", ", names) : string.Join(", ", names.Take(limit)) + $" +{names.Length - limit}";
    }

    public static string Compass(double bearing) => new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[(int)Math.Round(bearing / 45) % 8];

    public string Guidance()
    {
        if (Live?.Region == null && Live != null && !Assumed)
            return Live.Zone == 0 ? "Waiting for zone (log in with the tracker open)" : $"No map for zone {Live.Zone}";
        if (Target is not { } spot)
            return Weights.Count == 0 ? "No pets match this target" : "No spawn data for these pets here";
        if (Player is not { } player)
            return $"Best spot: {PetNames(spot.PetIds, 2)} · position unknown";
        double distance = spot.DistanceMetres ?? 0;
        return distance < FarmingHotspots.RadiusMetres
            ? $"You're in the spot · {PetNames(spot.PetIds, 2)}"
            : $"{distance:F0} m {Compass(FarmingHotspots.Bearing(player, (spot.X, spot.Y)))} · {PetNames(spot.PetIds, 2)}";
    }
}
