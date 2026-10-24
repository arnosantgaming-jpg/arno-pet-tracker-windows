using ArnoA2.Protocol;

namespace ArnoA2.Replay;

/// <summary>Connects to a running capture daemon and prints what it decodes. Names are
/// never printed: the character is shown only by entity id and name length.</summary>
public static class Live
{
    public static async Task<int> Run(int seconds)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        Stream link;
        try
        {
            link = await CaptureChannel.ConnectAsync(CaptureChannel.DefaultPath(), stop.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not connect to the capture daemon at {CaptureChannel.DefaultPath()}: {ex.Message}");
            return 1;
        }

        var pets = ArnoA2.Tracker.GameData.Instance;
        var streams = new Dictionary<uint, FrameStream>();
        var counts = new SortedDictionary<string, int>();
        long gaps = 0;
        var lastPrint = DateTimeOffset.UtcNow;
        try
        {
            while (await CaptureChannel.ReadAsync(link, stop.Token) is { } m)
            {
                switch (m.Kind)
                {
                    case ChannelKind.FlowOpened:
                        Console.WriteLine($"flow {m.Flow} opened: {m.Text}");
                        var stream = streams[m.Flow] = new FrameStream();
                        stream.FrameDecoded += frame =>
                        {
                            var message = MessageDecoder.Decode(frame, pets.IsPet, DateTimeOffset.UtcNow);
                            if (message == null)
                                return;
                            string name = message.GetType().Name;
                            counts[name] = counts.GetValueOrDefault(name) + 1;
                            if (message is CharacterIdentity c)
                                Console.WriteLine($"  character: entity {c.Entity}, name of {c.Name.Length} letters");
                            if (message is CollectionSnapshot s)
                                Console.WriteLine($"  collection: {s.Ranks.Count} owned pets");
                            if (message is SoulChange { Spent: false } g)
                                Console.WriteLine($"  souls gained: {string.Join(", ", g.Entries.Select(e => $"pet {e.Pet} +{e.Amount}"))}");
                        };
                        break;
                    case ChannelKind.Data when streams.TryGetValue(m.Flow, out var s2):
                        s2.Push(m.Payload);
                        break;
                    case ChannelKind.Gap:
                        gaps++;
                        if (streams.TryGetValue(m.Flow, out var s3))
                            s3.Reset();
                        Console.WriteLine($"  gap on flow {m.Flow}: {m.LostBytes} bytes lost");
                        break;
                    case ChannelKind.FlowClosed:
                        Console.WriteLine($"flow {m.Flow} closed");
                        streams.Remove(m.Flow);
                        break;
                    case ChannelKind.Status:
                        var parts = m.Text.Split('|');
                        if (DateTimeOffset.UtcNow - lastPrint >= TimeSpan.FromSeconds(10))
                        {
                            lastPrint = DateTimeOffset.UtcNow;
                            Console.WriteLine($"[{parts[0]}] {parts[1]} · flows {parts[2]} · segments {parts[3]} · dropped {parts[4]} · frames {streams.Values.Sum(x => x.Frames)} · {string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}"))}");
                        }

                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine($"\nframes {streams.Values.Sum(x => x.Frames)}, faults {streams.Values.Sum(x => x.Faults)}, gaps {gaps}");
        foreach (var (name, count) in counts)
            Console.WriteLine($"  {name,-20} {count}");
        return 0;
    }
}
