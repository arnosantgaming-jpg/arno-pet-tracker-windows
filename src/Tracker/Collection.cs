using ArnoA2.Protocol;

namespace ArnoA2.Tracker;

public enum Certainty
{
    /// <summary>No balance known.</summary>
    Unknown,
    /// <summary>From a save or from before a capture gap; later gains are added as an estimate.</summary>
    LastKnown,
    /// <summary>Login snapshot plus every gain and spend since, with no gap.</summary>
    Exact
}

/// <summary>One pet's standing. Balance is the soul counter toward the next rank (none at max).</summary>
public sealed class PetProgress
{
    public int Pet { get; init; }
    public int? Rank { get; set; }
    public int? Balance { get; set; }
    public Certainty Certainty { get; set; }
    public int? TargetRank { get; set; }
    public DateTimeOffset? SyncedUtc { get; set; }

    public bool Max => Rank == 3;
    public int? Requirement => Rank is int rank ? Ranks.Requirement(rank) : null;
    /// <summary>Enough souls for the next rank; the player still has to confirm it in game (except rank 2 → 3).</summary>
    public bool RankUpReady => Certainty == Certainty.Exact && Rank is 0 or 1 && Balance >= Requirement;
    public double Fraction => Max ? 1 : Balance is int b && Requirement is int r ? Math.Clamp((double)b / r, 0, 1) : 0;

    public string ProgressText => Max ? "MAX"
        : Balance is int b && Requirement is int r ? (Certainty == Certainty.Exact ? "" : "≈") + $"{Math.Min(b, r)}/{r}"
        : Requirement is int need ? $"?/{need}" : "?";

    /// <summary>Souls still needed to reach the target rank, when known exactly.</summary>
    public int? SoulsToTarget
    {
        get
        {
            if (TargetRank is not int target || Rank is not int rank)
                return null;
            if (rank >= target)
                return 0;
            if (Certainty != Certainty.Exact || Balance is not int balance)
                return null;
            int left = Math.Max(0, Ranks.Requirement(rank)!.Value - balance);
            for (int stage = rank + 1; stage < target; stage++)
                left += Ranks.Requirement(stage)!.Value;
            return left;
        }
    }
}

public sealed record RankChange(int Pet, int From, int To);

/// <summary>
/// The account's pet collection and how it changes:
/// balance = login value + gains − spends, independent of message order; rank comes
/// from rank messages, except that a rank-2 pet at 75 souls becomes max on its own
/// (confirmed in game). Any capture gap downgrades exact balances to last-known.
/// </summary>
public sealed class Collection
{
    readonly Dictionary<int, PetProgress> pets = new();
    readonly HashSet<int> ranksSeenThisSession = new();

    public Collection(IEnumerable<int> catalog)
    {
        foreach (int id in catalog)
            pets[id] = new() { Pet = id };
    }

    public IReadOnlyDictionary<int, PetProgress> Pets => pets;
    public bool Synced { get; private set; }
    public DateTimeOffset? SyncedUtc { get; private set; }

    /// <summary>Restore saved standings: always last-known, since play may have happened elsewhere.</summary>
    public void Restore(int pet, int? rank, int? balance, int? targetRank, DateTimeOffset? synced)
    {
        if (!pets.TryGetValue(pet, out var p) || rank is < 0 or > 3)
            return;
        p.Rank = rank;
        p.Balance = rank == 3 ? null : balance is >= 0 ? balance : null;
        p.Certainty = p.Balance.HasValue ? Certainty.LastKnown : Certainty.Unknown;
        p.TargetRank = targetRank is >= 1 and <= 3 ? targetRank : null;
        p.SyncedUtc = synced;
    }

    public List<RankChange> Apply(CollectionSnapshot snapshot, DateTimeOffset now)
    {
        var changes = new List<RankChange>();
        foreach (var p in pets.Values)
        {
            int rank = snapshot.Ranks.GetValueOrDefault(p.Pet);
            if (p.Rank is int before && rank > before && ranksSeenThisSession.Contains(p.Pet))
                changes.Add(new(p.Pet, before, rank));
            p.Rank = rank;
            p.Balance = rank == 3 ? null : snapshot.Souls.GetValueOrDefault(p.Pet);
            p.Certainty = Certainty.Exact;
            p.SyncedUtc = now;
            ranksSeenThisSession.Add(p.Pet);
        }

        Synced = true;
        SyncedUtc = now;
        return changes;
    }

    public RankChange? Gain(int pet, int amount)
    {
        if (!pets.TryGetValue(pet, out var p) || amount <= 0 || p.Max)
            return null;
        if (p.Balance is int balance)
            p.Balance = balance + amount;
        if (p.Certainty == Certainty.Exact && p.Rank == 2 && p.Balance >= Ranks.Requirement(2))
            return Promote(p, 3);
        return null;
    }

    public void Spend(int pet, int amount)
    {
        if (!pets.TryGetValue(pet, out var p) || amount <= 0 || p.Max)
            return;
        if (p.Balance is int balance && balance >= amount)
            p.Balance = balance - amount;
        else
        {
            // A spend larger than the known balance means the balance was wrong.
            p.Balance = null;
            p.Certainty = Certainty.Unknown;
        }
    }

    public RankChange? Reached(int pet, int rank)
    {
        if (!pets.TryGetValue(pet, out var p) || rank is < 1 or > 3 || p.Rank == rank)
            return null;
        return Promote(p, rank);
    }

    RankChange? Promote(PetProgress p, int rank)
    {
        int? before = p.Rank;
        p.Rank = rank;
        if (rank == 3)
            p.Balance = null;
        ranksSeenThisSession.Add(p.Pet);
        return before is int from && rank > from ? new(p.Pet, from, rank) : null;
    }

    /// <summary>Bytes were lost: nothing is exact any more until the next login snapshot.</summary>
    public void Interrupt()
    {
        foreach (var p in pets.Values)
            if (p.Certainty == Certainty.Exact)
                p.Certainty = p.Balance.HasValue ? Certainty.LastKnown : Certainty.Unknown;
        Synced = false;
    }

    public void SetTarget(int pet, int? rank)
    {
        if (pets.TryGetValue(pet, out var p))
            p.TargetRank = rank is >= 1 and <= 3 ? rank : null;
    }
}
