using System.Text;
using ArnoA2.Protocol;
using ArnoA2.Tracker;

// Synthetic tracker checks (no account data). End-to-end parity with the original
// tracker on real recordings: tools/Replay --tracker <recording> ~/.local/share/PetTracker
int checks = 0;
void Check(bool ok, string what)
{
    checks++;
    if (!ok)
        throw new Exception("FAILED: " + what);
}

var data = GameData.Instance;
var t0 = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
int dratona = data.Pets.Values.Single(p => p.Name == "Dratona").Id;
int spirit = data.Pets.Values.Single(p => p.Name == "Ancient Spirit").Id;
int npc = data.PetOfNpc.First(p => p.Value == dratona).Key;
Check(data.Pets.Count > 300 && data.PetOfNpc.Count > 100 && data.PetOfItem.Count > 50, "game data loaded");

CollectionSnapshot Snapshot(params (int Pet, int Rank, int Souls)[] pets) => new(0, pets.Where(p => p.Rank > 0).ToDictionary(p => p.Pet, p => p.Rank), pets.ToDictionary(p => p.Pet, p => p.Souls));

// --- Collection ledger --------------------------------------------------------------
{
    var c = new Collection(data.Pets.Keys);
    c.Apply(Snapshot((dratona, 2, 70), (spirit, 1, 20)), t0);
    Check(c.Synced && c.Pets[dratona] is { Rank: 2, Balance: 70, Certainty: Certainty.Exact } && c.Pets[spirit].ProgressText == "20/25", "snapshot applied exactly");
    Check(c.Pets.Values.Count(p => p.Rank == 0) == data.Pets.Count - 2, "pets missing from the snapshot are unowned");

    c.Gain(spirit, 5);
    Check(c.Pets[spirit].RankUpReady && c.Pets[spirit].ProgressText == "25/25", "enough for rank 2, waiting for the player");
    c.Gain(spirit, 2); // gain arrives before the spend and rank messages
    c.Reached(spirit, 2);
    c.Spend(spirit, 25);
    Check(c.Pets[spirit] is { Rank: 2, Balance: 2, Certainty: Certainty.Exact }, "rank-up: balance independent of message order");

    var max = c.Gain(dratona, 5);
    Check(max == new RankChange(dratona, 2, 3) && c.Pets[dratona].Max && c.Pets[dratona].Balance == null, "rank 2 reaching 75 becomes max by itself");

    c.Spend(spirit, 10);
    Check(c.Pets[spirit].Certainty == Certainty.Unknown && c.Pets[spirit].ProgressText == "?/75", "impossible spend makes the balance unknown");

    var g = new Collection(data.Pets.Keys);
    g.Apply(Snapshot((dratona, 1, 10)), t0);
    g.Interrupt();
    Check(!g.Synced && g.Pets[dratona].Certainty == Certainty.LastKnown && g.Pets[dratona].ProgressText == "≈10/25", "gap downgrades to last known");
    g.Gain(dratona, 3);
    Check(g.Pets[dratona].ProgressText == "≈13/25" && g.Gain(dratona, 100) == null, "last-known gains are estimates and never auto-promote");

    var goal = new Collection(data.Pets.Keys);
    goal.Apply(Snapshot((dratona, 1, 20)), t0);
    goal.SetTarget(dratona, 3);
    Check(goal.Pets[dratona].SoulsToTarget == 5 + 75, "souls to target across stages");
    goal.Interrupt();
    Check(goal.Pets[dratona].SoulsToTarget == null, "no target estimate without an exact balance");
}

// --- Engine: identity, holding, kills, notices ----------------------------------------
Engine NewEngine(SavedState? state = null) => new(store: null, initial: state ?? new SavedState());
{
    var e = NewEngine();
    var notices = new List<Notice>();
    e.Noticed += notices.Add;
    byte[] Frame(byte op, byte family, byte[] body) => Cat(Var(body.Length + 6), new[] { op, family }, body);
    var name = Encoding.UTF8.GetBytes("Tester");
    var identity = Frame(0x33, 0x36, Cat(Var(4242), I32(0), new byte[] { 7, (byte)name.Length }, name, new byte[2], new byte[8]));
    var snapshot = Frame(0x00, 0x90, Cat(I32(0), Var(1), I32(dratona), I32(dratona), I32(2), Var(1), I32(dratona), I32(74)));
    var gain = Frame(0x0D, 0x90, Cat(Var(1), I32(dratona), I32(1)));

    Check(e.Connection == ArnoA2.Tracker.Connection.Waiting, "waiting before any traffic");
    e.SetGoal(dratona, 3);
    e.Data(1, Cat(snapshot, gain), t0); // the snapshot comes before the identity
    Check(e.Collection.Pets[dratona].Certainty != Certainty.Exact, "snapshot held until the character is known");
    e.Data(1, identity, t0);
    Check(e.Connection == ArnoA2.Tracker.Connection.Live && e.Character == "Tester" && e.LocalEntity == 4242, "identity binds the session");
    Check(e.Collection.Pets[dratona].Max && notices.OfType<RankNotice>().Any(n => n.Pet == dratona && n.Rank == 3) && notices.OfType<GoalNotice>().Any(), "held gain applied in order: max and goal reached");
    Check(e.Profile.Characters.Contains("Tester"), "new character joins the account");

    var appear = Frame(0x41, 0x36, Cat(Var(777), new byte[] { 0x0C, 0, 0 }, I32(npc), new byte[] { 0, 2 }, new byte[8]));
    var hit = Frame(0x04, 0x38, Cat(Var(777), Var(4), Var(0), Var(4242), BitConverter.GetBytes(12_000_000u), Var(1), new byte[8], Var(0), Var(500)));
    var death = Frame(0x42, 0x36, Cat(Var(777), new byte[] { 0, 3 }));
    e.Data(1, Cat(appear, hit, death), t0.AddSeconds(5));
    Check(e.Session.Kills == 1 && e.KillsFor(dratona) == 1 && e.Recent.Rows[0].Creature.Length > 0, "kill counted when the player hit the creature");
    e.Data(1, Cat(appear, death), t0.AddSeconds(6));
    Check(e.Session.Kills == 1, "a death without the player's hit is not counted");

    e.Gap(1, 100, t0.AddSeconds(7));
    Check(e.Connection == ArnoA2.Tracker.Connection.Unsynced && notices.OfType<GapNotice>().Any() && e.Session.Incomplete, "gap: unsynced and session incomplete");

    var late = NewEngine();
    late.Data(1, Cat(snapshot), t0);
    late.Tick(t0.AddSeconds(11));
    Check(late.Collection.Pets[dratona].Rank == 2 && late.Collection.Synced, "without an identity the snapshot applies to the active account after 10 s");

    var hinted = NewEngine();
    var hints = Enumerable.Range(0, 12).SelectMany(_ => Frame(0x00, 0x8D, Cat(Var(9000), new byte[4]))).ToArray();
    hinted.Data(1, hints, t0);
    Check(hinted.LocalEntity == 9000 && hinted.Assumed && hinted.Connection == ArnoA2.Tracker.Connection.Unsynced, "player recognised mid-session from self hints");
}

// --- Rows, pins, merchant -----------------------------------------------------------
{
    var e = NewEngine();
    e.Collection.Apply(Snapshot((dratona, 1, 3), (spirit, 3, 0)), t0);
    e.Recent.Touch(spirit, "", t0, true);
    e.Recent.Touch(dratona, "", t0.AddSeconds(1), true);
    Check(e.Rows(t0.AddSeconds(2)).Select(r => r.Pet).SequenceEqual(new[] { dratona }), "maxed pets hidden");
    Check(e.Rows(t0.AddSeconds(30)).Count == 0, "inactive rows expire");
    Check(e.Pin(spirit, true) && e.Rows(t0.AddSeconds(30)).First() is { Pet: var p, Pinned: true } && p == spirit, "pins stay on top even when maxed or inactive");
    e.ChangeSettings(s => s.PinLimit = 1);
    Check(!e.Pin(dratona, true), "pin limit enforced");

    int merchantPet = data.Pets.Keys.First(data.MerchantPet);
    int item = data.PetOfItem.First(i => i.Value == merchantPet).Key;
    var stock = new MerchantStock(104, t0.AddMinutes(30), new[] { new MerchantOffer(1, item, 50_000), new MerchantOffer(2, item, 40_000) });
    var m = NewEngine();
    m.Merchant.Stock(stock, t0);
    Check(m.MerchantList(t0).Count == 0, "no suggestions before the collection is synced");
    m.Collection.Apply(Snapshot(), t0);
    var list = m.MerchantList(t0.AddSeconds(1));
    Check(list.Count == 1 && list[0].Price == 40_000 && !list[0].Bought, "cheapest offer for an unowned merchant pet");
    m.Merchant.Souls(merchantPet, t0.AddSeconds(5));
    Check(m.MerchantList(t0.AddSeconds(6))[0].Bought && m.MerchantList(t0.AddSeconds(30)).Count == 0, "bought: shown briefly, then hidden");
    Check(m.MerchantList(t0.AddMinutes(3)).Count == 0, "stock older than two minutes is not trusted");
}

// --- Storage and import ---------------------------------------------------------------
{
    string root = Path.Combine(Environment.GetEnvironmentVariable("ARNOA2_TEST_DIR") ?? Path.GetTempPath(), "arnoa2-tracker-" + Environment.ProcessId);
    try
    {
        var store = new Store(Path.Combine(root, "v2"));
        var e = new Engine(store);
        e.Collection.Apply(Snapshot((dratona, 2, 40)), t0);
        e.SetGoal(dratona, 3);
        Check(e.SaveIfChanged() && !e.SaveIfChanged(), "saves only when changed");
        var again = new Engine(store);
        Check(again.Collection.Pets[dratona] is { Rank: 2, Balance: 40, Certainty: Certainty.LastKnown, TargetRank: 3 }, "restored as last known with its goal");

        string v1 = Path.Combine(root, "v1");
        Directory.CreateDirectory(Path.Combine(v1, "profiles"));
        File.WriteAllText(Path.Combine(v1, "profiles.json"), """{"Profiles":[{"AccountName":"Main","CharacterName":"Tester","LinkedCharacters":["Alt"],"ProfileKey":"abc"}],"SelectedProfileKey":"abc"}""");
        File.WriteAllText(Path.Combine(v1, "profiles", "abc.json"), "\uFEFF{\"Version\":1,\"Pets\":{\"" + dratona + "\":{\"Id\":" + dratona + ",\"Level\":2,\"Souls\":30,\"EstimatedSouls\":33,\"TargetRank\":3},\"999999\":{\"Level\":1}},\"Creatures\":{\"" + npc + "\":{\"NpcId\":" + npc + ",\"Total\":12}}}");
        File.WriteAllText(Path.Combine(v1, "settings.json"), """{"TrackerMode":"Nearby","PinnedPetIds":[1001],"PinLimit":2,"HideMaxed":false}""");
        var imported = Store.ImportV1(v1)!;
        var profile = imported.Profiles.Single();
        Check(profile.Name == "Main" && profile.Characters.SequenceEqual(new[] { "Tester", "Alt" }) && profile.Pets[dratona] is { Rank: 2, Balance: 33, TargetRank: 3 } && !profile.Pets.ContainsKey(999999) && profile.Kills[npc] == 12, "v1 collection, characters and kills imported");
        Check(imported.Settings is { Mode: "Nearby", PinLimit: 2, HideMaxed: false } && imported.Settings.Pins.SequenceEqual(new[] { 1001 }), "v1 settings imported");
    }
    finally
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}

Console.WriteLine($"{checks} tracker checks passed.");

static byte[] Var(int value)
{
    var bytes = new List<byte>();
    uint v = (uint)value;
    do
    {
        byte b = (byte)(v & 0x7f);
        v >>= 7;
        bytes.Add(v != 0 ? (byte)(b | 0x80) : b);
    }
    while (v != 0);
    return bytes.ToArray();
}

static byte[] I32(int v) => BitConverter.GetBytes(v);
static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
