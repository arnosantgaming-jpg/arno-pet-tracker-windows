using System.Buffers.Binary;
using System.Text;
using ArnoA2.Protocol;

// Synthetic protocol checks. Real captures contain account data and are never committed;
// run tools/Replay --compare against your own recordings for end-to-end checks.
int checks = 0;
void Check(bool ok, string what)
{
    checks++;
    if (!ok)
        throw new Exception("FAILED: " + what);
}

var pets = new HashSet<int> { 1001, 1002, 1147 };
var now = DateTimeOffset.UtcNow;
Message? Decode(byte op, byte family, byte[] body) => MessageDecoder.Decode(new(op, family, body), pets.Contains, now);

// --- Builders -------------------------------------------------------------------
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
static byte[] F32(float v) => BitConverter.GetBytes(v);
static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
static byte[] FrameBytes(byte op, byte family, byte[] body) => Cat(Var(body.Length + 6), new[] { op, family }, body);

// Literal-only LZ4 block: one sequence carrying all bytes (valid LZ4).
static byte[] Lz4Literal(byte[] data)
{
    var output = new List<byte>();
    int n = data.Length;
    output.Add((byte)(Math.Min(n, 15) << 4));
    if (n >= 15)
    {
        int rest = n - 15;
        while (rest >= 255)
        {
            output.Add(255);
            rest -= 255;
        }

        output.Add((byte)rest);
    }

    output.AddRange(data);
    return output.ToArray();
}

static byte[] Bundle(byte[] frames) => Cat(new byte[] { 0xFF, 0xFF }, I32(frames.Length), Lz4Literal(frames));

List<Frame> Split(params byte[][] chunks)
{
    var stream = new FrameStream();
    var frames = new List<Frame>();
    stream.FrameDecoded += frames.Add;
    foreach (var chunk in chunks)
        stream.Push(chunk);
    return frames;
}

// --- Reader ---------------------------------------------------------------------
{
    var r = new Reader(Cat(Var(300), Var(0), new byte[] { 0x80, 0x00 }));
    Check(r.VarInt(out int a) && a == 300, "varint 300");
    Check(r.VarInt(out int b) && b == 0, "varint 0");
    Check(!r.VarInt(out _) && r.Position == 3, "over-long varint rejected without consuming");
    var empty = new Reader(Array.Empty<byte>());
    Check(!empty.I32(out _) && !empty.Byte(out _) && !empty.Skip(1), "reads past end fail");
}

// --- LZ4 ------------------------------------------------------------------------
{
    // "abcabcabcabc": 3 literals then a match of 9 at distance 3 (overlapping copy).
    var block = new byte[] { 0x35, (byte)'a', (byte)'b', (byte)'c', 3, 0 };
    var output = new byte[12];
    Check(Lz4.Decode(block, output, out int used) == Lz4Result.Done && used == 6 && Encoding.ASCII.GetString(output) == "abcabcabcabc", "LZ4 overlapping match");
    Check(Lz4.Decode(block.AsSpan(0, 3), new byte[12], out _) == Lz4Result.NeedMore, "LZ4 waits for more input");
    Check(Lz4.Decode(new byte[] { 0x10, 1, 9, 0 }, new byte[8], out _) == Lz4Result.Corrupt, "LZ4 rejects distance beyond output");
    var big = Enumerable.Range(0, 600).Select(i => (byte)i).ToArray();
    var bigOut = new byte[600];
    Check(Lz4.Decode(Lz4Literal(big), bigOut, out _) == Lz4Result.Done && bigOut.SequenceEqual(big), "LZ4 long literal run");
}

// --- Framing --------------------------------------------------------------------
{
    var a = FrameBytes(0x11, 0x22, new byte[] { 1, 2, 3 });
    var b = FrameBytes(0x33, 0x44, Enumerable.Repeat((byte)7, 200).ToArray()); // two-byte length
    var frames = Split(Cat(a, b));
    Check(frames.Count == 2 && frames[0].Code == 0x1122 && frames[0].Body.SequenceEqual(new byte[] { 1, 2, 3 }) && frames[1].Body.Length == 200, "two frames, one and two-byte lengths");

    var both = Cat(a, b);
    var pieces = Enumerable.Range(0, both.Length).Select(i => new[] { both[i] }).ToArray();
    Check(Split(pieces).Count == 2, "frames split one byte at a time");

    var bundled = Split(Bundle(Cat(a, b)));
    Check(bundled.Count == 2 && bundled[1].Code == 0x3344, "stream bundle");

    var nested = Split(FrameBytes(0xFF, 0xFF, Cat(I32(a.Length), Lz4Literal(a))));
    Check(nested.Count == 1 && nested[0].Code == 0x1122, "nested bundle inside a frame");

    var stream = new FrameStream();
    var resynced = new List<Frame>();
    stream.FrameDecoded += resynced.Add;
    // Joined mid-message: an over-long length (80 00) and an impossibly short frame (02)
    // cannot start a frame, so they are skipped before the real frame.
    stream.Push(Cat(new byte[] { 0x80, 0x00, 0x02 }, a));
    Check(resynced.Count == 1 && resynced[0].Code == 0x1122 && stream.SkippedBytes == 3, "resync after garbage");

    var faulty = new FrameStream();
    int faults = 0;
    faulty.Faulted += _ => faults++;
    faulty.Push(Cat(new byte[] { 0xFF, 0xFF }, I32(10), new byte[] { 0x10, 1, 9, 0 }));
    Check(faults == 1, "corrupt bundle reported");
}

// --- Messages -------------------------------------------------------------------
{
    var name = Encoding.UTF8.GetBytes("Tester");
    var identity = Decode(0x33, 0x36, Cat(Var(4242), I32(0), new byte[] { 7, (byte)name.Length }, name, new byte[] { 0xD2, 0x04 }, new byte[20]));
    Check(identity is CharacterIdentity { Entity: 4242, Name: "Tester", Server: 1234 }, "identity");
    Check(Decode(0x33, 0x36, Cat(Var(4242), I32(0), new byte[] { 6, 3 }, "abc"u8.ToArray(), new byte[2])) == null, "identity without name flag rejected");

    var zone = Decode(0x21, 0x36, Cat(I32(1), I32(1010), new byte[8], F32(-72190f), F32(-69614f), new byte[20]));
    Check(zone is ZoneEntered { Zone: 1010, X: -72190f }, "zone with place");

    var snapshot = Decode(0x00, 0x90, Cat(I32(1147), Var(2), I32(1147), I32(1147), I32(2), I32(1001), I32(1001), I32(1), Var(2), I32(1147), I32(17), I32(1002), I32(9)));
    Check(snapshot is CollectionSnapshot s && s.Ranks[1147] == 2 && s.Souls[1147] == 17 && s.Souls[1002] == 9, "collection snapshot (unowned pet may exceed 5)");
    Check(Decode(0x00, 0x90, Cat(I32(0), Var(1), I32(1001), I32(1001), I32(1), Var(1), I32(1001), I32(26))) == null, "rank-1 counter above 25 rejected");
    Check(Decode(0x00, 0x90, Cat(I32(0), Var(1), I32(1001), I32(1002), I32(1), Var(0))) == null, "mismatched duplicate id rejected");

    Check(Decode(0x0D, 0x90, Cat(Var(2), I32(1147), I32(3), I32(1001), I32(1))) is SoulChange { Spent: false, Entries.Count: 2 }, "soul gain batch");
    Check(Decode(0x0E, 0x90, Cat(Var(1), I32(1147), I32(75))) is SoulChange { Spent: true }, "soul spend");
    Check(Decode(0x0D, 0x90, Cat(Var(1), I32(1147), I32(3), new byte[1])) == null, "soul batch with trailing byte rejected");
    Check(Decode(0x0D, 0x90, Cat(Var(1), I32(4242), I32(3))) == null, "unknown pet rejected");

    Check(Decode(0x14, 0x90, Cat(new byte[2], I32(1147), I32(3), new byte[13])) is RankReached { Pet: 1147, Rank: 3 }, "rank reached");

    Check(Decode(0x42, 0x36, Cat(Var(555), new byte[] { 0, 3 })) is EntityRemoved { Entity: 555, Died: true }, "death removal");
    var appear = Decode(0x41, 0x36, Cat(Var(777), new byte[] { 0x0C, 0, 0 }, I32(2100075), new byte[] { 0, 2 }, F32(-73000f), F32(-70500f), new byte[8]));
    Check(appear is NpcAppeared { Entity: 777, Npc: 2100075, X: -73000f }, "npc appearance with place");
    Check(Decode(0x41, 0x36, Cat(Var(778), new byte[] { 0x1F, 0, 0 }, I32(2100075), new byte[] { 0, 2 })) == null, "summon skipped");

    Check(Decode(0x3C, 0x37, Cat(Var(4242), new byte[] { 3 }, F32(-80000f), F32(-61000f), new byte[18])) is PositionReport { Entity: 4242, X: -80000f }, "position report");
    Check(Decode(0x1D, 0x37, Cat(Var(5), new byte[2], F32(1f), F32(2f))) == null, "unlisted movement opcode ignored");
    Check(Decode(0x1A, 0x37, Cat(Var(5), new byte[2], F32(-1234f), F32(5678f))) is EntityMoved { Entity: 5, Y: 5678f }, "entity moved");

    var effect = Decode(0x2A, 0x38, Cat(Var(900), new byte[] { 1, 0 }, Var(33), BitConverter.GetBytes(12_000_000u), new byte[16], Var(4242), new byte[3], F32(-80496f), F32(-60666f), F32(120f)));
    Check(effect is EffectCast { Caster: 4242, X: -80496f }, "effect with caster place");

    Check(Decode(0x00, 0x8D, Cat(Var(4242), new byte[6])) is SelfHint { Entity: 4242 }, "self hint");

    // Merchant: build a valid 357-byte tab with ten offers.
    var shop = new byte[357];
    shop[0] = 104;
    shop[1] = 10;
    int at = 2;
    for (int i = 0; i < 10; i++)
    {
        if (i is 1 or 5 or 9)
            at++;
        BinaryPrimitives.WriteInt32LittleEndian(shop.AsSpan(at), 100 + i);
        BinaryPrimitives.WriteInt32LittleEndian(shop.AsSpan(at + 4), 5000 + i);
        BinaryPrimitives.WriteInt64LittleEndian(shop.AsSpan(at + 8), 1);
        shop[at + 16] = 1; // 80 %
        shop[at + 17] = 0x5d;
        BinaryPrimitives.WriteInt64LittleEndian(shop.AsSpan(at + 18), 100_000);
        BinaryPrimitives.WriteInt64LittleEndian(shop.AsSpan(at + 26), 80_000);
        at += 34;
    }

    shop[36] = shop[173] = 0x55;
    shop[310] = 5;
    BinaryPrimitives.WriteInt64LittleEndian(shop.AsSpan(345), now.AddMinutes(30).ToUnixTimeMilliseconds());
    Check(Decode(0x91, 0x56, shop) is MerchantStock { Tab: 104, Offers.Count: 10 } m && m.Offers[0].Price == 80_000, "merchant stock");
    shop[at - 34 + 26] = 1;
    Check(Decode(0x91, 0x56, shop) == null, "merchant with inconsistent price rejected");
}

Console.WriteLine($"{checks} protocol checks passed.");
