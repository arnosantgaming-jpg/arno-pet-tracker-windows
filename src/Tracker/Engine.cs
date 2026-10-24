using ArnoA2.Protocol;

namespace ArnoA2.Tracker;

public enum Connection
{
    /// <summary>No game connection seen yet.</summary>
    Waiting,
    /// <summary>Game connection found; the character is not known yet.</summary>
    Identifying,
    /// <summary>Character known; collection totals are last-known (no login seen, or a gap).</summary>
    Unsynced,
    /// <summary>Character known and the collection synchronized at login without gaps since.</summary>
    Live
}

/// <summary>Something worth telling the player about.</summary>
public abstract record Notice(DateTimeOffset Time);
public sealed record RankNotice(DateTimeOffset Time, int Pet, int Rank) : Notice(Time);
public sealed record GoalNotice(DateTimeOffset Time, int Pet, int Rank) : Notice(Time);
public sealed record GapNotice(DateTimeOffset Time, long LostBytes) : Notice(Time);

/// <summary>
/// Turns the game stream into tracker state. Feed it what the capture daemon sends
/// (<see cref="Opened"/>, <see cref="Data"/>, <see cref="Gap"/>, <see cref="Closed"/>);
/// read the results from its properties. Not thread-safe: call from one thread.
/// </summary>
public sealed class Engine
{
    static readonly TimeSpan IdentityWait = TimeSpan.FromSeconds(10);
    readonly GameData data;
    readonly Store? store;
    readonly SavedState state;
    readonly Dictionary<uint, FrameStream> streams = new();
    readonly Dictionary<int, int> hints = new();
    readonly List<(Message Message, DateTimeOffset Time)> held = new();
    DateTimeOffset? holdingSince;
    uint? flow;
    bool dirty;

    public Engine(Store? store, GameData? data = null, SavedState? initial = null)
    {
        this.data = data ?? GameData.Instance;
        this.store = store;
        state = initial ?? store?.Load() ?? new SavedState();
        state.Settings.Normalize();
        World = new World(this.data);
        Merchant = new Merchant(this.data);
        Collection = new Collection(this.data.Pets.Keys);
        if (state.Profiles.Count == 0)
            state.Profiles.Add(new SavedProfile());
        Load(state.Profiles.FirstOrDefault(p => p.Id == state.ActiveProfile) ?? state.Profiles[0]);
    }

    public Collection Collection { get; private set; }
    public World World { get; }
    public RecentList Recent { get; } = new();
    public FarmingSession Session { get; } = new();
    public Merchant Merchant { get; }
    public Settings Settings => state.Settings;
    public SavedProfile Profile { get; private set; } = null!;
    public IReadOnlyList<SavedProfile> Profiles => state.Profiles;
    public int? LocalEntity { get; private set; }
    public string? Character { get; private set; }
    /// <summary>The character was recognised from game traffic without seeing its login.</summary>
    public bool Assumed { get; private set; }
    public long Frames => streams.Values.Sum(s => s.Frames);
    public long Gaps { get; private set; }
    public DateTimeOffset? LastSoulsUtc { get; private set; }
    public event Action<Notice>? Noticed;
    /// <summary>Every message decoded from the game connection, in order (for the live map).</summary>
    public event Action<Message, DateTimeOffset>? Decoded;

    public Connection Connection => flow == null ? Connection.Waiting : LocalEntity == null ? Connection.Identifying : Collection.Synced ? Connection.Live : Connection.Unsynced;

    // --- Stream input ------------------------------------------------------------

    public void Opened(uint id)
    {
        var stream = new FrameStream();
        stream.FrameDecoded += frame => Frame(id, frame);
        streams[id] = stream;
    }

    public void Data(uint id, ReadOnlySpan<byte> bytes, DateTimeOffset now)
    {
        if (!streams.ContainsKey(id))
            Opened(id);
        clock = now;
        streams[id].Push(bytes);
        Tick(now);
    }

    public void Gap(uint id, long lost, DateTimeOffset now)
    {
        if (streams.TryGetValue(id, out var stream))
            stream.Reset();
        if (id != flow)
            return;
        Gaps++;
        Collection.Interrupt();
        Session.Interrupt();
        World.Clear();
        held.Clear();
        holdingSince = null;
        dirty = true;
        Noticed?.Invoke(new GapNotice(now, lost));
    }

    public void Closed(uint id, DateTimeOffset now)
    {
        streams.Remove(id);
        if (id != flow)
            return;
        // The game connection ended (logout, crash): nothing after this is continuous.
        Gap(id, 0, now);
        flow = null;
        LocalEntity = null;
        Assumed = false;
        hints.Clear();
    }

    DateTimeOffset clock;

    void Frame(uint id, Frame frame)
    {
        var message = MessageDecoder.Decode(frame, data.IsPet, clock);
        if (message == null)
            return;
        // The game connection is the one that carries game messages; follow the newest.
        if (flow != id && message is CharacterIdentity or CollectionSnapshot or ZoneEntered || flow == null)
        {
            if (flow != null && flow != id)
                Gap(flow.Value, 0, clock);
            flow = id;
        }

        if (id != flow)
            return;
        Decoded?.Invoke(message, clock);
        if (holdingSince != null && message is SoulChange or RankReached or CollectionSnapshot)
        {
            held.Add((message, clock));
            return;
        }

        Handle(message, clock);
    }

    /// <summary>Time-based work: give up waiting for an identity after a snapshot.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (holdingSince is { } since && now - since >= IdentityWait)
            Release(now);
    }

    void Handle(Message message, DateTimeOffset now, bool released = false)
    {
        switch (message)
        {
            case CharacterIdentity identity:
                Identify(identity.Entity, identity.Name, assumed: false);
                if (holdingSince != null)
                    Release(now);
                break;
            case CollectionSnapshot snapshot:
                if (!released && (Character == null || Assumed))
                {
                    // The login snapshot comes just before the character's identity. Hold it
                    // (and anything after it) so it lands in the right account's collection.
                    holdingSince = now;
                    held.Add((snapshot, now));
                    break;
                }

                foreach (var change in Collection.Apply(snapshot, now))
                    Ranked(change, now);
                dirty = true;
                break;
            case SoulChange { Spent: false } gain:
                foreach (var (pet, amount) in gain.Entries)
                {
                    LastSoulsUtc = now;
                    Session.Souls(pet, amount, now);
                    Recent.Touch(pet, "", now, souls: true);
                    Merchant.Souls(pet, now);
                    if (Collection.Gain(pet, amount) is { } change)
                        Ranked(change, now);
                }

                dirty = true;
                break;
            case SoulChange spend:
                foreach (var (pet, amount) in spend.Entries)
                    Collection.Spend(pet, amount);
                dirty = true;
                break;
            case RankReached reached:
                if (Collection.Reached(reached.Pet, reached.Rank) is { } rankChange)
                    Ranked(rankChange, now);
                dirty = true;
                break;
            case NpcAppeared appeared:
                World.Appeared(appeared.Entity, appeared.Npc, now);
                break;
            case EntityMoved moved:
                World.Seen(moved.Entity, now);
                break;
            case PositionReport report:
                World.Seen(report.Entity, now);
                break;
            case SkillHit hit when LocalEntity is int local:
                World.Hit(hit.Actor, hit.Target, local, now);
                break;
            case EntityRemoved removed:
                if (World.Removed(removed.Entity, removed.Died) is { } kill && LocalEntity != null)
                {
                    Profile.Kills[kill.Npc] = Profile.Kills.GetValueOrDefault(kill.Npc) + 1;
                    Session.Kill(now);
                    Recent.Touch(kill.Pet, data.NpcName(kill.Npc), now, souls: false);
                    dirty = true;
                }

                break;
            case SelfHint hint:
                Hint(hint.Entity);
                break;
            case MerchantStock stock:
                Merchant.Stock(stock, now);
                break;
            case MerchantCleared:
                Merchant.Clear();
                break;
        }
    }

    void Release(DateTimeOffset now)
    {
        holdingSince = null;
        var queue = held.ToArray();
        held.Clear();
        if (Character == null)
            Identify(LocalEntity ?? 0, null, assumed: true); // apply to the active account
        foreach (var (message, time) in queue)
            Handle(message, time, released: true);
    }

    // --- Identity --------------------------------------------------------------------

    // Stat updates and a few other frames name the player's own entity far more often than
    // any other (204 of 323 in a capture, next 13). A clear leader identifies the player
    // when the login was missed.
    void Hint(int entity)
    {
        if (Character != null && !Assumed)
            return;
        hints[entity] = hints.GetValueOrDefault(entity) + 1;
        int total = hints.Values.Sum();
        if (total < 12)
            return;
        var ranked = hints.OrderByDescending(h => h.Value).Take(2).ToArray();
        if (ranked[0].Value >= 6 && ranked[0].Value >= 3 * (ranked.Length > 1 ? ranked[1].Value : 0) && ranked[0].Key != LocalEntity)
            Identify(ranked[0].Key, null, assumed: true);
        if (total >= 400)
            hints.Clear();
    }

    void Identify(int entity, string? name, bool assumed)
    {
        if (entity > 0)
            LocalEntity = entity;
        Assumed = assumed;
        if (name == null)
        {
            Character ??= Profile.Characters.FirstOrDefault() ?? "";
            return;
        }

        if (Character == name)
            return;
        Character = name;
        var owner = state.Profiles.FirstOrDefault(p => p.Characters.Contains(name, StringComparer.Ordinal));
        if (owner == null)
        {
            owner = Settings.NewCharactersJoin ? Profile : new SavedProfile { Name = name + "'s account" };
            if (!state.Profiles.Contains(owner))
                state.Profiles.Add(owner);
            owner.Characters.Add(name);
            dirty = true;
        }

        if (owner != Profile)
            Switch(owner);
    }

    // --- Profiles and saving ----------------------------------------------------------

    void Load(SavedProfile profile)
    {
        Profile = profile;
        state.ActiveProfile = profile.Id;
        Collection = new Collection(data.Pets.Keys);
        foreach (var (pet, saved) in profile.Pets)
            Collection.Restore(pet, saved.Rank, saved.Balance, saved.TargetRank, saved.SyncedUtc);
    }

    void Switch(SavedProfile profile)
    {
        Snapshot();
        Load(profile);
        Recent.Clear();
        Session.Reset();
        Merchant.Clear();
        dirty = true;
    }

    /// <summary>Copy live standings into the saved profile.</summary>
    void Snapshot()
    {
        foreach (var p in Collection.Pets.Values)
        {
            if (p.Rank == null && p.TargetRank == null)
            {
                Profile.Pets.Remove(p.Pet);
                continue;
            }

            Profile.Pets[p.Pet] = new SavedPet { Rank = p.Rank, Balance = p.Balance, TargetRank = p.TargetRank, SyncedUtc = p.SyncedUtc };
        }
    }

    public bool SaveIfChanged()
    {
        if (!dirty)
            return false;
        Snapshot();
        store?.Save(state);
        dirty = false;
        return true;
    }

    // --- Player actions ----------------------------------------------------------------

    public void SetGoal(int pet, int? rank)
    {
        Collection.SetTarget(pet, rank);
        dirty = true;
    }

    public void ChangeSettings(Action<Settings> change)
    {
        change(state.Settings);
        state.Settings.Normalize();
        dirty = true;
    }

    public bool Pin(int pet, bool pinned)
    {
        var pins = state.Settings.Pins;
        if (pinned && !pins.Contains(pet))
        {
            if (pins.Count >= state.Settings.PinLimit || !data.IsPet(pet))
                return false;
            pins.Add(pet);
        }
        else if (!pinned)
            pins.Remove(pet);
        dirty = true;
        return true;
    }

    public void ResetSession()
    {
        Session.Reset();
        Recent.Clear();
    }

    void Ranked(RankChange change, DateTimeOffset now)
    {
        Session.Ranked(change.Pet, change.To);
        Recent.Touch(change.Pet, "", now, souls: false);
        Noticed?.Invoke(new RankNotice(now, change.Pet, change.To));
        if (Collection.Pets[change.Pet].TargetRank is int target && change.From < target && change.To >= target)
            Noticed?.Invoke(new GoalNotice(now, change.Pet, target));
    }

    // --- Views -----------------------------------------------------------------------

    public sealed record Row(int Pet, PetProgress Progress, string Creature, DateTimeOffset SeenUtc, bool Pinned, bool Nearby);

    /// <summary>
    /// The tracker list: pinned pets first (in pin order), then recent or nearby pets.
    /// Maxed pets can be hidden; recent rows can expire after inactivity. Rows are
    /// ordered deterministically so background traffic cannot shuffle them.
    /// </summary>
    public IReadOnlyList<Row> Rows(DateTimeOffset now)
    {
        var s = state.Settings;
        IEnumerable<Row> source = s.Mode == "Nearby"
            ? World.Nearby(now, TimeSpan.FromSeconds(s.NearbyStaleSeconds)).Select(n => new Row(n.Pet, Collection.Pets[n.Pet], n.Creatures, n.SeenUtc, false, true))
            : Recent.Rows.Where(r => !s.ExpireInactive || now - r.SeenUtc < TimeSpan.FromSeconds(s.InactiveSeconds)).Select(r => new Row(r.Pet, Collection.Pets[r.Pet], r.Creature, r.SeenUtc, false, false));
        var rows = source.Where(r => !s.HideMaxed || !r.Progress.Max).ToList();
        string Name(Row r) => data.Pets[r.Pet].Name;
        IEnumerable<Row> ordered = s.Sort switch
        {
            "Recent only" => rows.OrderByDescending(r => r.SeenUtc).ThenBy(r => r.Pet),
            "Closest to next rank" => rows.OrderBy(r => r.Progress.Max).ThenByDescending(r => r.Progress.Fraction).ThenBy(Name, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(r => r.Progress.Max).ThenByDescending(r => s.Mode == "Nearby" ? DateTimeOffset.MinValue : r.SeenUtc).ThenBy(Name, StringComparer.OrdinalIgnoreCase)
        };
        var pinned = s.Pins.Select(id => rows.FirstOrDefault(r => r.Pet == id) is { } live ? live with { Pinned = true } : new Row(id, Collection.Pets[id], "", DateTimeOffset.MinValue, true, false));
        return pinned.Concat(ordered.Where(r => !s.Pins.Contains(r.Pet))).Take(Math.Max(s.RowLimit, s.Pins.Count + 1)).ToArray();
    }

    public IReadOnlyList<MerchantSuggestion> MerchantList(DateTimeOffset now) => state.Settings.MerchantSuggestions ? Merchant.Suggestions(Collection, now) : Array.Empty<MerchantSuggestion>();

    public long KillsFor(int pet) => Profile.Kills.Where(k => data.PetOfNpc.GetValueOrDefault(k.Key) == pet).Sum(k => k.Value);
}
