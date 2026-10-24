using System.Text.Json;

namespace ArnoA2.Tracker;

public enum Family
{
    Unknown,
    Cogni,
    Fera,
    Natura,
    Varian,
    Special
}

public sealed record Pet(int Id, string Key, string Name, Family Family);

/// <summary>A creature whose kills can award souls for a pet.</summary>
public sealed record SoulSource(int Npc, string Name, string Region);

public sealed record Acquisition(string Kind, IReadOnlyList<string> Aliases, string? Instructions);

/// <summary>
/// Static game knowledge: the pet catalog, which creatures give souls for which pet,
/// Black Cloud Merchant items, and how pets are obtained. Loaded once from the
/// research data embedded in this assembly.
/// </summary>
public sealed class GameData
{
    public static GameData Instance { get; } = Load();

    public IReadOnlyDictionary<int, Pet> Pets { get; }
    /// <summary>Creature (NPC id) → pet whose souls it gives, when exactly one pet is claimed.</summary>
    public IReadOnlyDictionary<int, int> PetOfNpc { get; }
    public IReadOnlyDictionary<int, string> NpcNames { get; }
    public IReadOnlyDictionary<int, IReadOnlyList<SoulSource>> SourcesOfPet { get; }
    public IReadOnlyDictionary<int, Acquisition> Acquisitions { get; }
    /// <summary>Merchant item id → pet.</summary>
    public IReadOnlyDictionary<int, int> PetOfItem { get; }

    GameData(IReadOnlyDictionary<int, Pet> pets, IReadOnlyDictionary<int, int> petOfNpc, IReadOnlyDictionary<int, string> npcNames, IReadOnlyDictionary<int, IReadOnlyList<SoulSource>> sources, IReadOnlyDictionary<int, Acquisition> acquisitions, IReadOnlyDictionary<int, int> petOfItem)
    {
        Pets = pets;
        PetOfNpc = petOfNpc;
        NpcNames = npcNames;
        SourcesOfPet = sources;
        Acquisitions = acquisitions;
        PetOfItem = petOfItem;
        soldByMerchant = petOfItem.Values.ToHashSet();
    }

    public bool IsPet(int id) => Pets.ContainsKey(id);
    public string NpcName(int npc) => NpcNames.GetValueOrDefault(npc) ?? "NPC #" + npc;

    /// <summary>Pets worth watching at the Black Cloud Merchant: sold there, and known to have
    /// no creature source or to be a dungeon reward. Missing source data is not taken to
    /// mean "merchant only".</summary>
    public bool MerchantPet(int pet) =>
        soldByMerchant.Contains(pet) &&
        Acquisitions.TryGetValue(pet, out var how) && how.Aliases.Contains("Black Cloud Merchants") &&
        SourcesOfPet.TryGetValue(pet, out var sources) && (sources.Count == 0 || how.Kind == "Dungeon reward");

    readonly HashSet<int> soldByMerchant;

    static JsonDocument Resource(string name)
    {
        using var stream = typeof(GameData).Assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("Missing data " + name);
        return JsonDocument.Parse(stream);
    }

    static GameData Load()
    {
        var pets = new Dictionary<int, Pet>();
        using (var catalog = Resource("catalog.json"))
            foreach (var row in catalog.RootElement.EnumerateArray())
            {
                int id = row.GetProperty("Id").GetInt32();
                var family = row.GetProperty("Category").GetString() switch
                {
                    "Intellect" => Family.Cogni,
                    "Feral" => Family.Fera,
                    "Nature" => Family.Natura,
                    "Trans" => Family.Varian,
                    "Special" => Family.Special,
                    _ => Family.Unknown
                };
                pets[id] = new(id, row.GetProperty("CatalogName").GetString()!, row.GetProperty("Name").GetString()!, family);
            }

        // A creature counts for a pet only when every source row agrees on one pet whose
        // internal key matches the catalog; ambiguous creatures are left unlinked.
        var petOfNpc = new Dictionary<int, int>();
        var npcNames = new Dictionary<int, string>();
        using (var sources = Resource("sources.json"))
        {
            foreach (var group in sources.RootElement.GetProperty("Rows").EnumerateArray().GroupBy(r => r.GetProperty("NpcId").GetInt32()))
            {
                npcNames[group.Key] = group.First().GetProperty("NpcName").GetString() ?? "";
                var claims = group.Select(r => (Pet: r.GetProperty("PetId").GetInt32(), Key: r.GetProperty("CatalogName").GetString())).Distinct().ToArray();
                if (claims.Length == 1 && pets.TryGetValue(claims[0].Pet, out var pet) && pet.Key == claims[0].Key)
                    petOfNpc[group.Key] = pet.Id;
            }
        }

        // Corroborated in play although missing from the source data.
        foreach (var (npc, pet, key) in new[] { (2100034, 1008, "OdSpider_01"), (2100231, 1168, "WaterEle_01"), (2400240, 1163, "Basilisk_01") })
            if (pets.TryGetValue(pet, out var known) && known.Key == key)
                petOfNpc[npc] = pet;

        var soulSources = new Dictionary<int, IReadOnlyList<SoulSource>>();
        using (var links = Resource("links.json"))
            foreach (var row in links.RootElement.GetProperty("Rows").EnumerateArray())
                if (row.TryGetProperty("SoulSources", out var list) && list.ValueKind == JsonValueKind.Array)
                    soulSources[row.GetProperty("PetId").GetInt32()] = list.EnumerateArray().Select(s => new SoulSource(s.GetProperty("NpcId").GetInt32(), s.GetProperty("Name").GetString() ?? "", s.GetProperty("Region").GetString() ?? "")).ToArray();

        var acquisitions = new Dictionary<int, Acquisition>();
        using (var how = Resource("acquisitions.json"))
            foreach (var row in how.RootElement.EnumerateArray())
                acquisitions[row.GetProperty("PetId").GetInt32()] = new(row.GetProperty("Kind").GetString() ?? "", row.TryGetProperty("Aliases", out var aliases) ? aliases.EnumerateArray().Select(a => a.GetString() ?? "").ToArray() : Array.Empty<string>(), row.TryGetProperty("Instructions", out var text) ? text.GetString() : null);

        var petOfItem = new Dictionary<int, int>();
        using (var market = Resource("market.json"))
            foreach (var row in market.RootElement.EnumerateArray())
                petOfItem[row.GetProperty("ItemId").GetInt32()] = row.GetProperty("PetId").GetInt32();

        return new(pets, petOfNpc, npcNames, soulSources, acquisitions, petOfItem);
    }
}
