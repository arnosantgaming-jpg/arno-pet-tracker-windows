using System.Diagnostics;
using System.Net;
using System.Text;
using ArnoA2.Capture;
using ArnoA2.Protocol;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

// Usage: CaptureTests [daemon-path recording.pcap server-port]
// Unit checks always run; the end-to-end check runs when a recording is given.
int checks = 0;
void Check(bool ok, string what)
{
    checks++;
    if (!ok)
        throw new Exception("FAILED: " + what);
}

var t0 = DateTimeOffset.UnixEpoch;
byte[] B(string s) => Encoding.ASCII.GetBytes(s);
string Text(List<StreamEvent> events) => string.Concat(events.Select(e => e is StreamData d ? Encoding.ASCII.GetString(d.Bytes) : $"[gap {((StreamGap)e).LostBytes}]"));

// --- Reassembly -------------------------------------------------------------------
{
    var a = new StreamAssembler();
    a.Start(999);
    Check(Text(a.Add(1000, B("hello"), t0)) == "hello", "in order after SYN");
    Check(Text(a.Add(1010, B("world"), t0)) == "", "out of order waits");
    Check(Text(a.Add(1005, B(" big "), t0)) == " big world", "hole filled releases waiting bytes");
    Check(Text(a.Add(1003, B("lo bi"), t0)) == "", "retransmission trimmed");
    Check(Text(a.Add(1013, B("ld!!"), t0)) == "!!", "overlapping tail delivered once");

    var b = new StreamAssembler();
    b.Add(5000, B("abc"), t0);
    b.Add(5010, B("xyz"), t0);
    Check(Text(b.Poll(t0 + TimeSpan.FromSeconds(2.9))) == "", "hole kept inside the window");
    Check(Text(b.Poll(t0 + TimeSpan.FromSeconds(3))) == "[gap 7]xyz", "hole declared lost after 3 s");
    Check(b.Lost == 7 && b.Delivered == 6, "loss accounting");

    var c = new StreamAssembler();
    c.Start(uint.MaxValue - 2);
    Check(Text(c.Add(uint.MaxValue - 1, B("wrap"), t0)) == "wrap" && Text(c.Add(2, B("ok"), t0)) == "ok", "sequence wraps at 2^32");
}

// --- procfs ---------------------------------------------------------------------
{
    Check(GameConnections.IsGame(B("S:\\steamapps\\common\\AION2\\Aion2\\Binaries\\Win64\\AION2.exe\0-steam\0")), "Proton argv[0] Windows path");
    Check(GameConnections.IsGame(B("/usr/bin/wine64-preloader\0C:\\Games\\AION2.exe\0")), "behind the Wine loader");
    Check(!GameConnections.IsGame(B("/usr/bin/kate\0notes-about-AION2.exe.txt\0")), "a file name in arguments is not the game");

    Check(GameConnections.Endpoint("0A00000A:B2B6", out var local, out int lp) && local.Equals(IPAddress.Parse("10.0.0.10")) && lp == 45750, "IPv4 endpoint");
    var rows = new[]
    {
        "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode",
        "   0: 0A00000A:C9B6 C770CAC1:3410 01 00000000:00000000 00:00000000 00000000  1000        0 71830 1 0",
        "   1: 0A00000A:A8DA A7E81214:01BB 01 00000000:00000000 00:00000000 00000000  1000        0 71831 1 0",
        "   2: 0100007F:ADE3 0100007F:D7AA 01 00000000:00000000 00:00000000 00000000  1000        0 71832 1 0",
        "   3: 0A00000A:C9B7 C770CAC1:3410 06 00000000:00000000 00:00000000 00000000  1000        0 71833 1 0",
        "   4: 0A00000A:C9B8 C770CAC1:3410 01 00000000:00000000 00:00000000 00000000  1000        0 99999 1 0"
    };
    var found = GameConnections.Parse(rows, new HashSet<ulong> { 71830, 71831, 71832, 71833 }).ToArray();
    Check(found.Length == 1 && found[0].RemotePort == 13328 && found[0].Remote.Equals(IPAddress.Parse("193.202.112.199")), "only the game's open, non-HTTPS, non-loopback connection");
}

// --- Windows TCP tables (layout of MIB_TCP(6)TABLE_OWNER_PID) ------------------
{
    static byte[] Row4(uint state, string local, int localPort, string remote, int remotePort, int pid)
    {
        var row = new byte[24];
        BitConverter.TryWriteBytes(row.AsSpan(0), state);
        IPAddress.Parse(local).GetAddressBytes().CopyTo(row, 4);
        row[8] = (byte)(localPort >> 8); row[9] = (byte)localPort;
        IPAddress.Parse(remote).GetAddressBytes().CopyTo(row, 12);
        row[16] = (byte)(remotePort >> 8); row[17] = (byte)remotePort;
        BitConverter.TryWriteBytes(row.AsSpan(20), pid);
        return row;
    }

    var rows = new[]
    {
        Row4(5, "10.0.0.10", 51638, "193.202.112.199", 13328, 4728), // game, established
        Row4(5, "10.0.0.10", 43162, "20.18.232.167", 443, 4728),     // game, HTTPS
        Row4(5, "127.0.0.1", 44587, "127.0.0.1", 55466, 4728),       // game through a local relay
        Row4(2, "0.0.0.0", 13328, "0.0.0.0", 0, 4728),               // listening
        Row4(5, "10.0.0.10", 50000, "193.202.112.199", 13328, 999),  // another process
        Row4(3, "10.0.0.10", 51640, "193.202.112.199", 13328, 4728)  // game, connecting
    };
    var table = BitConverter.GetBytes(rows.Length).Concat(rows.SelectMany(r => r)).ToArray();
    var game = GameConnections.ParseTable4(table, new HashSet<int> { 4728 }).ToArray();
    Check(game.Length == 3 && game[0].LocalPort == 51638 && game[0].RemotePort == 13328 && game[1].Remote.Equals(IPAddress.Loopback) && game[1].RemotePort == 55466 && game[2].LocalPort == 51640,
        "Windows IPv4 table: the game's open, non-HTTPS connections, loopback included (local relays such as ExitLag)");

    var row6 = new byte[56];
    IPAddress.Parse("2001:db8::10").GetAddressBytes().CopyTo(row6, 0);
    row6[20] = 0xC9; row6[21] = 0xB6;
    IPAddress.Parse("2001:db8::99").GetAddressBytes().CopyTo(row6, 24);
    row6[44] = 0x34; row6[45] = 0x10;
    BitConverter.TryWriteBytes(row6.AsSpan(48), 5u);
    BitConverter.TryWriteBytes(row6.AsSpan(52), 4728);
    var six = GameConnections.ParseTable6(BitConverter.GetBytes(1).Concat(row6).ToArray(), new HashSet<int> { 4728 }).ToArray();
    Check(six.Length == 1 && six[0].RemotePort == 13328 && six[0].LocalPort == 0xC9B6 && six[0].Remote.Equals(IPAddress.Parse("2001:db8::99")), "Windows IPv6 table row");
}

// --- End to end -------------------------------------------------------------------
if (args.Length >= 3)
{
    // ARNOA2_TEST_DIR overrides where the test socket goes (sandboxes may restrict /tmp).
    string socket = Path.Combine(Environment.GetEnvironmentVariable("ARNOA2_TEST_DIR") ?? Path.GetTempPath(), $"arnoa2-test-{Environment.ProcessId}.sock");
    var daemon = Process.Start(new ProcessStartInfo(args[0], new[] { "--read", args[1], "--own-port", args[2], "--socket", socket }) { RedirectStandardError = true })!;
    Stream? link = null;
    for (int i = 0; i < 100 && link == null; i++)
    {
        try
        {
            link = await CaptureChannel.ConnectAsync(socket);
        }
        catch (Exception) when (i < 99)
        {
            await Task.Delay(50);
        }
    }

    var frames = new FrameStream();
    long data = 0, gaps = 0, opened = 0;
    while (await CaptureChannel.ReadAsync(link!) is { } m)
    {
        switch (m.Kind)
        {
            case ChannelKind.FlowOpened:
                opened++;
                break;
            case ChannelKind.Data:
                data += m.Payload.Length;
                frames.Push(m.Payload);
                break;
            case ChannelKind.Gap:
                gaps++;
                frames.Reset();
                break;
        }
    }

    daemon.WaitForExit(10_000);
    Check(daemon.ExitCode == 0, "daemon exits cleanly at end of file: " + daemon.StandardError.ReadToEnd());
    Check(opened >= 1 && data > 0 && frames.Frames > 0 && frames.Faults == 0, "game stream forwarded and decodable");
    Console.WriteLine($"end to end: {opened} flow(s), {data:N0} bytes, {gaps} gaps, {frames.Frames} frames, {frames.SkippedBytes} bytes skipped");
    if (args.Length >= 4)
        Check(frames.Frames == long.Parse(args[3]), $"frame count matches the replay ({args[3]})");
}

Console.WriteLine($"{checks} capture checks passed.");
