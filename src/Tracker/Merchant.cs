using ArnoA2.Protocol;

namespace ArnoA2.Tracker;

/// <summary>A merchant offer worth buying. Bought: a soul reward for the pet arrived after
/// the offer was seen, so it is shown briefly as done and then hidden for this stock.</summary>
public sealed record MerchantSuggestion(int Pet, long Price, int Tab, DateTimeOffset RefreshUtc, bool Bought);

/// <summary>
/// Black Cloud Merchant stock, per tab. Stock is a snapshot of what the shop window showed:
/// it is trusted for two minutes and never past the shop's own refresh time, and only
/// while the collection is synced (otherwise a guess could suggest a pet you already have).
/// </summary>
public sealed class Merchant
{
    static readonly TimeSpan Trust = TimeSpan.FromMinutes(2), Feedback = TimeSpan.FromSeconds(20);
    readonly GameData data;
    readonly Dictionary<int, (MerchantStock Stock, DateTimeOffset Seen)> tabs = new();
    readonly Dictionary<int, (DateTimeOffset Refresh, DateTimeOffset Seen, DateTimeOffset? Bought)> shown = new();

    public Merchant(GameData data) => this.data = data;

    public void Stock(MerchantStock stock, DateTimeOffset now) => tabs[stock.Tab] = (stock, now);

    public void Clear()
    {
        tabs.Clear();
        shown.Clear();
    }

    /// <summary>A soul reward for a shown pet within two minutes of seeing the offer reads as a purchase.</summary>
    public void Souls(int pet, DateTimeOffset now)
    {
        if (shown.TryGetValue(pet, out var s) && s.Bought == null && now > s.Seen && now - s.Seen < Trust && now < s.Refresh)
            shown[pet] = s with { Bought = now };
    }

    public IReadOnlyList<MerchantSuggestion> Suggestions(Collection collection, DateTimeOffset now)
    {
        if (!collection.Synced)
        {
            shown.Clear();
            return Array.Empty<MerchantSuggestion>();
        }

        var best = new Dictionary<int, MerchantSuggestion>();
        foreach (var (tab, (stock, seen)) in tabs)
        {
            if (now - seen >= Trust || now >= stock.RefreshUtc)
                continue;
            foreach (var offer in stock.Offers)
            {
                if (!data.PetOfItem.TryGetValue(offer.Item, out int pet) || !data.MerchantPet(pet))
                    continue;
                var progress = collection.Pets.GetValueOrDefault(pet);
                if (progress is not { Certainty: Certainty.Exact, Rank: >= 0 and < 3 })
                    continue;
                if (!best.TryGetValue(pet, out var current) || offer.Price < current.Price)
                    best[pet] = new(pet, offer.Price, tab, stock.RefreshUtc, false);
            }
        }

        foreach (var (pet, suggestion) in best)
            if (!shown.TryGetValue(pet, out var s) || s.Refresh != suggestion.RefreshUtc)
                shown[pet] = (suggestion.RefreshUtc, now, null);
        foreach (var pet in shown.Keys.ToArray())
            if (shown[pet].Refresh <= now)
                shown.Remove(pet);

        var result = new List<MerchantSuggestion>();
        foreach (var (pet, s) in shown)
        {
            if (s.Bought is { } at)
            {
                if (now - at < Feedback && best.TryGetValue(pet, out var done))
                    result.Add(done with { Bought = true });
            }
            else if (best.TryGetValue(pet, out var open))
                result.Add(open);
        }

        return result.OrderBy(r => data.Pets[r.Pet].Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
