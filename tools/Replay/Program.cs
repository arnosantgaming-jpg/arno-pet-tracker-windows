using ArnoA2.Protocol;
using ArnoA2.Replay;
using ArnoA2.Tracker;

// replay <recording>            decode a recording and count the messages
// replay --tracker <recording>  run it through the tracker engine and print the outcome
// replay --live [seconds]       connect to a running capture daemon and print what it sees
// Recordings: a capture file (pcap) of the game connection, or a stream dump.
// They contain your account's game traffic: keep them private.
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: replay <recording> | --tracker <recording> | --live [seconds]");
    return 2;
}

if (args[0] == "--live")
    return await Live.Run(args.Length > 1 && int.TryParse(args[1], out int seconds) ? seconds : 60);
if (args[0] == "--tracker" && args.Length > 1)
    return Tracker(args[1]);
return Decode(args[0]);

static int Decode(string path)
{
    var data = GameData.Instance;
    var streams = new Dictionary<string, FrameStream>();
    var counts = new SortedDictionary<string, int>();
    int faults = 0;
    DateTimeOffset now = default;
    foreach (var chunk in Recording.Read(path))
    {
        now = chunk.Time;
        if (chunk.Data.Length == 0)
        {
            foreach (var s in streams.Values)
                s.Reset(); // capture gap
            continue;
        }

        if (!streams.TryGetValue(chunk.Flow, out var stream))
        {
            stream = streams[chunk.Flow] = new FrameStream();
            stream.Faulted += _ => faults++;
            stream.FrameDecoded += frame =>
            {
                var message = MessageDecoder.Decode(frame, data.IsPet, now);
                string name = message?.GetType().Name ?? "(other)";
                counts[name] = counts.GetValueOrDefault(name) + 1;
                if (message is CollectionSnapshot snapshot)
                    Console.WriteLine($"{now:HH:mm:ss} collection: {snapshot.Ranks.Count} owned");
                else if (message is CharacterIdentity identity)
                    Console.WriteLine($"{now:HH:mm:ss} character: entity {identity.Entity}, name of {identity.Name.Length} letters");
                else if (message is ZoneEntered zone)
                    Console.WriteLine($"{now:HH:mm:ss} zone {zone.Zone}");
            };
        }

        stream.Push(chunk.Data);
    }

    Console.WriteLine($"\n{streams.Values.Sum(s => s.Frames)} frames, {streams.Values.Sum(s => s.SkippedBytes)} bytes skipped while resyncing, {faults} faults");
    foreach (var (name, count) in counts)
        Console.WriteLine($"  {name,-20} {count}");
    return 0;
}

static int Tracker(string path)
{
    var engine = new Engine(store: null);
    var notices = new List<Notice>();
    engine.Noticed += notices.Add;
    var flows = new Dictionary<string, uint>();
    DateTimeOffset last = default;
    foreach (var chunk in Recording.Read(path))
    {
        last = chunk.Time;
        if (chunk.Data.Length == 0)
        {
            foreach (uint id in flows.Values)
                engine.Gap(id, 1, chunk.Time);
            continue;
        }

        if (!flows.TryGetValue(chunk.Flow, out uint flow))
            flows[chunk.Flow] = flow = (uint)flows.Count + 1;
        engine.Data(flow, chunk.Data, chunk.Time);
    }

    engine.Tick(last + TimeSpan.FromSeconds(30));
    var pets = engine.Collection.Pets.Values;
    Console.WriteLine($"state {engine.Connection}{(engine.Assumed ? " (character assumed)" : "")}, {engine.Frames} frames, {engine.Gaps} gaps");
    Console.WriteLine($"collection: {pets.Count(p => p.Rank > 0)} owned, {pets.Count(p => p.Max)} max, {pets.Count(p => p.Certainty == Certainty.Exact)} exact balances");
    Console.WriteLine($"session: {engine.Session.TotalSouls} souls, {engine.Session.Kills} kills, {notices.OfType<RankNotice>().Count()} rank-ups");
    foreach (var row in engine.Session.Rows.Take(10))
        Console.WriteLine($"  {GameData.Instance.Pets[row.Pet].Name,-24} +{row.Souls} souls{(row.RankUps > 0 ? $", {row.RankUps} rank-up" : "")}");
    return 0;
}
