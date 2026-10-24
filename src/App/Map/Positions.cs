using ArnoA2.Protocol;
using ArnoA2.Tracker;

namespace ArnoA2.App.Map;

/// <summary>
/// The player's position for the live map, from decoded game messages. Units are game
/// centimetres. The server reports the player's exact place only after skills, hits,
/// effects and teleports; between those the trimmed middle of recently active nearby
/// entities is used, held still while it stays within its error band and eased toward
/// otherwise. Pet creatures in view are kept with their places for the map.
/// </summary>
public sealed class Positions
{
    public static readonly TimeSpan FixTrust = TimeSpan.FromSeconds(4);
    const double AroundSeconds = 5, SmoothSeconds = 1.5, MaxSpeed = 1500, HoldRadius = 1800;
    readonly Dictionary<int, (float X, float Y, DateTimeOffset At)> around = new();
    readonly Dictionary<int, LiveMob> mobs = new();
    (float X, float Y, DateTimeOffset At)? fix, smooth;
    string? detected;

    public int Zone { get; private set; }

    public void Clear()
    {
        around.Clear();
        mobs.Clear();
        fix = smooth = null;
    }

    /// <param name="local">The player's entity, when known.</param>
    public void Observe(Message message, int? local, DateTimeOffset now)
    {
        switch (message)
        {
            case ZoneEntered zone:
                if (zone.Zone != Zone)
                    detected = null;
                Zone = zone.Zone;
                if (zone.X is float zx && zone.Y is float zy)
                {
                    // A teleport or loading screen: what was around belongs to the old place.
                    Clear();
                    fix = (zx, zy, now);
                }

                break;
            case PositionReport report when report.Entity == local:
                fix = (report.X, report.Y, now);
                break;
            case PositionReport other:
                Moved(other.Entity, other.X, other.Y, now);
                break;
            case EntityMoved moved when moved.Entity != local:
                Moved(moved.Entity, moved.X, moved.Y, now);
                break;
            case EffectCast effect when effect.Caster == local:
                fix = (effect.X, effect.Y, now);
                break;
            case NpcAppeared appeared when appeared.X is float x && appeared.Y is float y:
                if (mobs.Count > 1000)
                    mobs.Clear();
                mobs[appeared.Entity] = new(appeared.Entity, appeared.Npc, x, y, now);
                Moved(appeared.Entity, x, y, now);
                break;
            case EntityRemoved removed:
                mobs.Remove(removed.Entity);
                around.Remove(removed.Entity);
                break;
        }
    }

    void Moved(int entity, float x, float y, DateTimeOffset now)
    {
        if (around.Count > 1500)
            around.Clear();
        around[entity] = (x, y, now);
        if (mobs.TryGetValue(entity, out var mob))
            mobs[entity] = mob with { X = x, Y = y, SeenUtc = now };
    }

    /// <summary>The region: from the zone when it has a map, else recognised from the pet
    /// creatures around (tracker started after the zone message), else unknown.</summary>
    public string? Region()
    {
        if (WorldMaps.RegionOfZone(Zone) is { } known)
            return known;
        if (Zone != 0)
            return null; // a zone without a bundled map (an instance)
        return detected = WorldMaps.Detect(mobs.Values) ?? detected;
    }

    public LiveMapView View(DateTimeOffset now)
    {
        var data = GameData.Instance;
        var best = Best(now);
        var pets = mobs.Values.Where(m => data.PetOfNpc.ContainsKey(m.Npc)).Select(m => new LiveMapMob(m.Entity, m.Npc, data.PetOfNpc[m.Npc], data.NpcName(m.Npc), m.X, m.Y)).ToArray();
        return new(Zone, Region(), best?.X, best?.Y, best?.Exact == true, best == null ? 0 : (now - best.Value.At).TotalSeconds, pets);
    }

    (float X, float Y, DateTimeOffset At, bool Exact)? Best(DateTimeOffset now)
    {
        if (fix is { } f && now - f.At <= FixTrust)
        {
            smooth = (f.X, f.Y, now);
            return (f.X, f.Y, f.At, true);
        }

        var guess = Around(now, AroundSeconds) ?? Around(now, 4 * AroundSeconds);
        // An estimate further from the last exact place than the player could have run since is not taken.
        if (guess is { } g && (fix is not { } last || Distance(g.X, g.Y, last.X, last.Y) <= 3000 + 1000 * Math.Min((now - last.At).TotalSeconds, 12)))
        {
            if (smooth is not { } prev)
                smooth = (g.X, g.Y, now);
            else if (Distance(g.X, g.Y, prev.X, prev.Y) <= HoldRadius)
                smooth = (prev.X, prev.Y, now); // within the estimate's error: hold still
            else
            {
                double dt = Math.Max(0, (now - prev.At).TotalSeconds), alpha = 1 - Math.Exp(-dt / SmoothSeconds);
                double dx = (g.X - prev.X) * alpha, dy = (g.Y - prev.Y) * alpha, step = Math.Sqrt(dx * dx + dy * dy), limit = MaxSpeed * dt + 200;
                if (step > limit)
                {
                    dx *= limit / step;
                    dy *= limit / step;
                }

                smooth = ((float)(prev.X + dx), (float)(prev.Y + dy), now);
            }

            return (smooth.Value.X, smooth.Value.Y, now, false);
        }

        return fix is { } old ? (old.X, old.Y, old.At, false) : null;
    }

    static double Distance(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    /// <summary>Mean of recently active entities within 45 m of their median (edge-of-range outliers dropped).</summary>
    (float X, float Y)? Around(DateTimeOffset now, double seconds)
    {
        var points = around.Values.Where(e => (now - e.At).TotalSeconds <= seconds).ToList();
        if (points.Count < 3)
            return null;
        float cx = points.Select(p => p.X).Order().ElementAt(points.Count / 2), cy = points.Select(p => p.Y).Order().ElementAt(points.Count / 2);
        for (int round = 0; round < 3; round++)
        {
            var near = points.Where(p => (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy) <= 4500f * 4500f).ToList();
            if (near.Count < 3)
                break;
            (cx, cy) = (near.Average(p => p.X), near.Average(p => p.Y));
        }

        return (cx, cy);
    }
}
