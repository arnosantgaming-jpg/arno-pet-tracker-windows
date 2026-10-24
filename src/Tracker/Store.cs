using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArnoA2.Tracker;

public sealed class SavedPet
{
    public int? Rank { get; set; }
    public int? Balance { get; set; }
    public int? TargetRank { get; set; }
    public DateTimeOffset? SyncedUtc { get; set; }
}

/// <summary>An account's collection. Pet collections are account-wide in AION 2, so every
/// character of the account shares one; Characters lists the names seen with it.</summary>
public sealed class SavedProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "My account";
    public List<string> Characters { get; set; } = new();
    public Dictionary<int, SavedPet> Pets { get; set; } = new();
    /// <summary>Creature (NPC id) → kills the player took part in.</summary>
    public Dictionary<int, long> Kills { get; set; } = new();
}

public sealed class Settings
{
    public string Mode { get; set; } = "Recent"; // Recent | Nearby
    public string Sort { get; set; } = "Unfinished first"; // | Recent only | Closest to next rank
    public bool HideMaxed { get; set; } = true;
    public bool ExpireInactive { get; set; } = true;
    public int InactiveSeconds { get; set; } = 20;
    public int NearbyStaleSeconds { get; set; } = 60;
    public int RowLimit { get; set; } = 5;
    public List<int> Pins { get; set; } = new();
    public int PinLimit { get; set; } = 3;
    public bool MerchantSuggestions { get; set; } = true;
    /// <summary>New characters join the active account's collection automatically.</summary>
    public bool NewCharactersJoin { get; set; } = true;

    public void Normalize()
    {
        Mode = Mode is "Nearby" ? "Nearby" : "Recent";
        Sort = Sort is "Recent only" or "Closest to next rank" ? Sort : "Unfinished first";
        InactiveSeconds = Math.Clamp(InactiveSeconds, 5, 600);
        NearbyStaleSeconds = Math.Clamp(NearbyStaleSeconds, 10, 600);
        RowLimit = Math.Clamp(RowLimit, 1, 20);
        PinLimit = Math.Clamp(PinLimit, 1, 5);
        Pins = Pins.Where(GameData.Instance.IsPet).Distinct().Take(PinLimit).ToList();
    }
}

public sealed class SavedState
{
    public int Version { get; set; } = 2;
    public string? ActiveProfile { get; set; }
    public List<SavedProfile> Profiles { get; set; } = new();
    public Settings Settings { get; set; } = new();
}

/// <summary>Atomic JSON storage in the user's data folder, with one backup generation.</summary>
public sealed class Store(string folder)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public string Folder { get; } = folder;
    string File => Path.Combine(Folder, "data.json");

    public static string DefaultFolder() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "arno-pet-tracker")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } home ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "arno-pet-tracker");

    public SavedState Load()
    {
        foreach (string path in new[] { File, File + ".bak" })
        {
            try
            {
                if (System.IO.File.Exists(path) && JsonSerializer.Deserialize<SavedState>(System.IO.File.ReadAllText(path), Json) is { Version: 2 } state)
                {
                    state.Settings.Normalize();
                    return state;
                }
            }
            catch (JsonException)
            {
                // fall back to the backup, then to a fresh state
            }
        }

        return new SavedState();
    }

    public void Save(SavedState state)
    {
        Directory.CreateDirectory(Folder);
        string temp = File + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        if (System.IO.File.Exists(File))
            System.IO.File.Replace(temp, File, File + ".bak");
        else
            System.IO.File.Move(temp, File);
    }

    /// <summary>
    /// One-time import from the original tracker's data (~/.local/share/PetTracker):
    /// ranks, last known balances, farming goals, kill counts, linked character names
    /// and the main tracker settings. The original files are only read.
    /// </summary>
    public static SavedState? ImportV1(string v1Folder)
    {
        string book = Path.Combine(v1Folder, "profiles.json");
        if (!System.IO.File.Exists(book))
            return null;
        var state = new SavedState();
        using var index = JsonDocument.Parse(System.IO.File.ReadAllText(book));
        string? selected = index.RootElement.TryGetProperty("SelectedProfileKey", out var s) ? s.GetString() : null;
        foreach (var entry in index.RootElement.GetProperty("Profiles").EnumerateArray())
        {
            string key = entry.GetProperty("ProfileKey").GetString() ?? "";
            string path = Path.Combine(v1Folder, "profiles", key + ".json");
            if (key.Length == 0 || !System.IO.File.Exists(path))
                continue;
            var profile = new SavedProfile { Name = entry.TryGetProperty("AccountName", out var n) && n.GetString() is { Length: > 0 } name ? name : "My account" };
            if (entry.TryGetProperty("CharacterName", out var c) && c.GetString() is { Length: > 0 } character)
                profile.Characters.Add(character);
            if (entry.TryGetProperty("LinkedCharacters", out var linked))
                profile.Characters.AddRange(linked.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
            profile.Characters = profile.Characters.Distinct().ToList();

            using var saved = JsonDocument.Parse(System.IO.File.ReadAllText(path));
            foreach (var pet in saved.RootElement.GetProperty("Pets").EnumerateObject())
            {
                if (!int.TryParse(pet.Name, out int id) || !GameData.Instance.IsPet(id))
                    continue;
                var v = pet.Value;
                int? Int(string field) => v.TryGetProperty(field, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : null;
                var row = new SavedPet { Rank = Int("Level"), Balance = Int("EstimatedSouls") ?? Int("Souls"), TargetRank = Int("TargetRank") };
                if (v.TryGetProperty("SyncedUtc", out var synced) && synced.ValueKind == JsonValueKind.String)
                    row.SyncedUtc = synced.GetDateTimeOffset();
                if (row.Rank != null || row.TargetRank != null)
                    profile.Pets[id] = row;
            }

            if (saved.RootElement.TryGetProperty("Creatures", out var creatures))
                foreach (var creature in creatures.EnumerateObject())
                    if (int.TryParse(creature.Name, out int npc) && creature.Value.TryGetProperty("Total", out var total) && total.GetInt64() > 0)
                        profile.Kills[npc] = total.GetInt64();
            state.Profiles.Add(profile);
            if (key == selected)
                state.ActiveProfile = profile.Id;
        }

        string settings = Path.Combine(v1Folder, "settings.json");
        if (System.IO.File.Exists(settings))
        {
            using var old = JsonDocument.Parse(System.IO.File.ReadAllText(settings));
            var root = old.RootElement;
            var target = state.Settings;
            if (root.TryGetProperty("TrackerMode", out var mode)) target.Mode = mode.GetString() ?? target.Mode;
            if (root.TryGetProperty("Sort", out var sort)) target.Sort = sort.GetString() ?? target.Sort;
            if (root.TryGetProperty("HideMaxed", out var hide)) target.HideMaxed = hide.GetBoolean();
            if (root.TryGetProperty("ExpireInactive", out var expire)) target.ExpireInactive = expire.GetBoolean();
            if (root.TryGetProperty("InactiveSeconds", out var inactive)) target.InactiveSeconds = inactive.GetInt32();
            if (root.TryGetProperty("NearbyStaleSeconds", out var stale)) target.NearbyStaleSeconds = stale.GetInt32();
            if (root.TryGetProperty("RowLimit", out var rows)) target.RowLimit = rows.GetInt32();
            if (root.TryGetProperty("PinLimit", out var limit)) target.PinLimit = limit.GetInt32();
            if (root.TryGetProperty("PinnedPetIds", out var pins) && pins.ValueKind == JsonValueKind.Array) target.Pins = pins.EnumerateArray().Select(p => p.GetInt32()).ToList();
            if (root.TryGetProperty("BlackMarketAlerts", out var merchant)) target.MerchantSuggestions = merchant.GetBoolean();
            if (root.TryGetProperty("ShareAllCharacters", out var share)) target.NewCharactersJoin = share.GetBoolean() || state.Profiles.Count <= 1;
            target.Normalize();
        }

        state.ActiveProfile ??= state.Profiles.FirstOrDefault()?.Id;
        return state.Profiles.Count > 0 ? state : null;
    }
}
