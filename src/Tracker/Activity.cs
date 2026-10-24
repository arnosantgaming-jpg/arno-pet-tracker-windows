namespace ArnoA2.Tracker;

/// <summary>A pet that was active recently: souls gained, a creature killed, or a rank reached.</summary>
public sealed record RecentPet(int Pet, string Creature, DateTimeOffset SeenUtc, DateTimeOffset? SoulsUtc);

/// <summary>A pet whose creatures are currently in view.</summary>
public sealed record NearbyPet(int Pet, string Creatures, DateTimeOffset SeenUtc);

/// <summary>
/// What is around the player, from appearances, movement, hits and removals. Appearance
/// means the game sent the creature to the client, not a measured distance. Nothing
/// here is saved, and none of it changes souls or ranks.
/// </summary>
public sealed class World
{
    const int Capacity = 4096;
    sealed class Life
    {
        public required int Npc;
        public required int Pet;
        public DateTimeOffset Seen;
        public bool Participated;
    }

    readonly Dictionary<int, Life> lives = new();
    readonly GameData data;

    public World(GameData data) => this.data = data;

    public void Clear() => lives.Clear();

    public void Appeared(int entity, int npc, DateTimeOffset now)
    {
        if (!data.PetOfNpc.TryGetValue(npc, out int pet))
        {
            lives.Remove(entity); // the id was reused by an unrelated creature
            return;
        }

        if (lives.TryGetValue(entity, out var life) && life.Npc == npc)
        {
            life.Seen = now; // repeated appearance keeps participation
            return;
        }

        if (lives.Count >= Capacity)
            lives.Remove(lives.MinBy(l => l.Value.Seen).Key);
        lives[entity] = new() { Npc = npc, Pet = pet, Seen = now };
    }

    public void Seen(int entity, DateTimeOffset now)
    {
        if (lives.TryGetValue(entity, out var life))
            life.Seen = now;
    }

    public void Hit(int actor, int target, int local, DateTimeOffset now)
    {
        if (lives.TryGetValue(target, out var life))
        {
            life.Seen = now;
            if (actor == local)
                life.Participated = true;
        }
    }

    /// <summary>A creature left view. Returns its NPC and pet when the player helped kill it.</summary>
    public (int Npc, int Pet)? Removed(int entity, bool died)
    {
        if (!lives.Remove(entity, out var life))
            return null;
        return died && life.Participated ? (life.Npc, life.Pet) : null;
    }

    public IReadOnlyList<NearbyPet> Nearby(DateTimeOffset now, TimeSpan stale) =>
        lives.Values.Where(l => l.Seen <= now && now - l.Seen < stale)
            .GroupBy(l => l.Pet)
            .Select(g => new NearbyPet(g.Key, string.Join(", ", g.Select(l => data.NpcName(l.Npc)).Distinct().Order()), g.Max(l => l.Seen)))
            .OrderByDescending(p => p.SeenUtc).ToArray();
}

/// <summary>Most recent first, one row per pet, at most 50.</summary>
public sealed class RecentList
{
    readonly List<RecentPet> rows = new();
    public IReadOnlyList<RecentPet> Rows => rows;

    public void Touch(int pet, string creature, DateTimeOffset now, bool souls)
    {
        int index = rows.FindIndex(r => r.Pet == pet);
        var previous = index >= 0 ? rows[index] : null;
        if (index >= 0)
            rows.RemoveAt(index);
        rows.Insert(0, new(pet, creature.Length > 0 ? creature : previous?.Creature ?? "", now, souls ? now : previous?.SoulsUtc));
        if (rows.Count > 50)
            rows.RemoveRange(50, rows.Count - 50);
    }

    public void Clear() => rows.Clear();
}

public sealed record SessionRow(int Pet, long Souls, int RankUps);

/// <summary>Souls received and ranks reached since the session started. Only observed
/// rewards count, never differences between snapshots; a gap marks it incomplete.</summary>
public sealed class FarmingSession
{
    readonly Dictionary<int, long> souls = new();
    readonly Dictionary<int, HashSet<int>> ranks = new();
    public DateTimeOffset? StartedUtc { get; private set; }
    public bool Incomplete { get; private set; }
    public long Kills { get; private set; }

    public void Souls(int pet, int amount, DateTimeOffset now)
    {
        StartedUtc ??= now;
        souls[pet] = souls.GetValueOrDefault(pet) + amount;
    }

    public void Kill(DateTimeOffset now)
    {
        StartedUtc ??= now;
        Kills++;
    }

    public void Ranked(int pet, int rank)
    {
        if (!souls.ContainsKey(pet))
            return; // only rank-ups this session's souls contributed to
        if (!ranks.TryGetValue(pet, out var seen))
            ranks[pet] = seen = new();
        seen.Add(rank);
    }

    public void Interrupt()
    {
        if (StartedUtc != null)
            Incomplete = true;
    }

    public void Reset()
    {
        souls.Clear();
        ranks.Clear();
        StartedUtc = null;
        Incomplete = false;
        Kills = 0;
    }

    public IReadOnlyList<SessionRow> Rows => souls.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Select(p => new SessionRow(p.Key, p.Value, ranks.GetValueOrDefault(p.Key)?.Count ?? 0)).ToArray();
    public long TotalSouls => souls.Values.Sum();
}
