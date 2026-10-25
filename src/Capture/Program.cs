using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ArnoA2.Capture;
using ArnoA2.Protocol;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

// arnoa2-capture [--socket PATH] [--parent PID] [--read FILE --own-port PORT]
//   Live (default): capture with cap_net_raw, then drop privileges.
//   --read: replay a capture file instead (no privileges; for tests), treating
//   connections whose server port is PORT as the game's.
var options = Options.Parse(args);
try
{
    return new Daemon(options).Run();
}
catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("arnoa2-capture: " + ex.Message);
    return 1;
}

sealed record Options(string Socket, int? Parent, string? ReadFile, int? OwnPort)
{
    public static Options Parse(string[] args)
    {
        string? Value(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        return new(Value("--socket") ?? CaptureChannel.DefaultPath(), int.TryParse(Value("--parent"), out int p) ? p : null, Value("--read"), int.TryParse(Value("--own-port"), out int port) ? port : null);
    }
}

sealed class Daemon(Options options)
{
    static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2), MinRescan = TimeSpan.FromMilliseconds(250), PendingLimit = TimeSpan.FromSeconds(3), Idle = TimeSpan.FromMinutes(2), NoClientExit = TimeSpan.FromMinutes(1);
    readonly GameConnections scanner = new();
    readonly Dictionary<Connection, Flow> flows = new();
    readonly Dictionary<Connection, (DateTimeOffset First, List<Segment> Segments)> pending = new();
    readonly Dictionary<Connection, DateTimeOffset> foreign = new();
    readonly BlockingCollection<(ChannelKind Kind, uint Flow, byte[] Payload)> outbox = new(boundedCapacity: 4096);
    HashSet<Connection> owned = new();
    DateTimeOffset lastScan = DateTimeOffset.MinValue, lastStatus = DateTimeOffset.MinValue, noClientSince = DateTimeOffset.UtcNow;
    Socket? listener;
    volatile Stream? client;
    uint nextFlow;
    long segments, dropped;
    DateTimeOffset lastParentCheck = DateTimeOffset.MinValue;
    string state = "Starting", message = "";

    sealed class Flow(uint id, Connection connection)
    {
        public uint Id { get; } = id;
        public Connection Connection { get; } = connection;
        public StreamAssembler Assembler { get; } = new();
        public DateTimeOffset LastSeen { get; set; }
    }

    public int Run()
    {
        var sources = options.ReadFile is { } file ? new[] { PacketSource.File(file) } : PacketSource.LiveAll();
        bool offline = options.ReadFile != null;
        try
        {
            if (!offline && OperatingSystem.IsLinux())
                Privileges.DropAll();
            // Incoming TCP only; HTTPS and loopback can never be the game connection.
            const string filter = "tcp and not port 443 and not net 127.0.0.0/8";
            foreach (var source in sources)
            {
                try
                {
                    source.Filter("inbound and " + filter);
                }
                catch (IOException)
                {
                    source.Filter(filter); // Windows adapters and capture files lack direction information
                }
            }

            Listen();
            var writer = new Thread(WriteLoop) { IsBackground = true, Name = "channel writer" };
            writer.Start();
            state = offline ? "Replaying" : "Capturing";
            message = offline ? "" : OperatingSystem.IsLinux() ? "Privileges dropped: " + Privileges.Current() : $"Npcap on {sources.Count} adapter(s)";
            if (offline)
                Replay(sources[0]);
            else
                Capture(sources);
            Finish(writer);
            return 0;
        }
        finally
        {
            foreach (var source in sources)
                source.Dispose();
        }
    }

    /// <summary>A capture file, in order, on this thread.</summary>
    void Replay(PacketSource source)
    {
        WaitForClient(TimeSpan.FromSeconds(10));
        while (true)
        {
            var segment = source.Next(out bool end);
            if (end)
                return;
            if (segment is { } s)
                Handle(s, s.Time);
            Accept();
            Housekeeping(segment?.Time ?? DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Live capture: one reader per adapter feeding a queue handled on this thread.</summary>
    void Capture(IReadOnlyList<PacketSource> sources)
    {
        using var queue = new BlockingCollection<Segment>(boundedCapacity: 65536);
        using var stopping = new CancellationTokenSource();
        var readers = sources.Select(source => new Thread(() =>
        {
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    if (source.Next(out bool end) is { } segment && !queue.TryAdd(segment with { Payload = segment.Payload.ToArray() }))
                        Interlocked.Increment(ref dropped);
                    if (end)
                        return;
                }
            }
            catch (IOException ex)
            {
                message = $"Capture stopped on {source.Device}: {ex.Message}";
            }
        }) { IsBackground = true, Name = "capture " + source.Device }).ToArray();
        foreach (var reader in readers)
            reader.Start();
        while (!ShouldExit())
        {
            if (queue.TryTake(out var segment, 100))
                Handle(segment, segment.Time);
            Accept();
            Housekeeping(DateTimeOffset.UtcNow);
        }

        stopping.Cancel();
        foreach (var source in sources)
            source.Stop();
    }

    void Finish(Thread writer)
    {
        foreach (var flow in flows.Values)
            Send(ChannelKind.FlowClosed, flow.Id, Array.Empty<byte>());
        outbox.CompleteAdding();
        writer.Join(TimeSpan.FromSeconds(5));
        listener?.Dispose();
        File.Delete(options.Socket);
    }

    void Handle(Segment s, DateTimeOffset now)
    {
        segments++;
        var key = new Connection(s.Destination, s.DestinationPort, s.Source, s.SourcePort);
        if (flows.TryGetValue(key, out var flow))
        {
            Feed(flow, s, now);
            return;
        }

        if (foreign.TryGetValue(key, out var until) && until > now)
            return;
        if (Owns(key, now))
        {
            Open(key, now, new[] { s });
            return;
        }

        // Unknown yet: the game may have just opened it. Hold briefly and rescan.
        if (!pending.TryGetValue(key, out var held))
            pending[key] = held = (now, new List<Segment>());
        if (held.Segments.Count < 512)
            held.Segments.Add(s with { Payload = s.Payload.ToArray() });
    }

    bool Owns(Connection key, DateTimeOffset now)
    {
        if (options.OwnPort is int port)
            return key.RemotePort == port;
        if (!owned.Contains(key) && DateTimeOffset.UtcNow - lastScan >= MinRescan)
            Rescan();
        return owned.Contains(key);
    }

    void Rescan()
    {
        lastScan = DateTimeOffset.UtcNow;
        try
        {
            owned = scanner.Scan().ToHashSet();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            message = "Could not read /proc: " + ex.Message;
        }
    }

    void Open(Connection key, DateTimeOffset now, IEnumerable<Segment> held)
    {
        var flow = new Flow(++nextFlow, key) { LastSeen = now };
        flows[key] = flow;
        Send(ChannelKind.FlowOpened, flow.Id, System.Text.Encoding.UTF8.GetBytes(key.ToString()));
        foreach (var s in held.OrderBy(h => h.Time))
            Feed(flow, s, s.Time);
    }

    void Feed(Flow flow, Segment s, DateTimeOffset now)
    {
        flow.LastSeen = now;
        if (s.Syn)
            flow.Assembler.Start(s.Sequence);
        Emit(flow, flow.Assembler.Add(s.Syn ? unchecked(s.Sequence + 1) : s.Sequence, s.Payload.Span, now));
        if (s.Fin || s.Reset)
            Close(flow);
    }

    void Emit(Flow flow, List<StreamEvent> events)
    {
        foreach (var e in events)
        {
            if (e is StreamData data)
                Send(ChannelKind.Data, flow.Id, data.Bytes);
            else if (e is StreamGap gap)
            {
                var payload = new byte[8];
                BitConverter.TryWriteBytes(payload, gap.LostBytes);
                Send(ChannelKind.Gap, flow.Id, payload);
            }
        }
    }

    void Close(Flow flow)
    {
        flows.Remove(flow.Connection);
        Send(ChannelKind.FlowClosed, flow.Id, Array.Empty<byte>());
    }

    void Housekeeping(DateTimeOffset now)
    {
        if (DateTimeOffset.UtcNow - lastScan >= ScanInterval && options.OwnPort == null)
            Rescan();
        foreach (var (key, held) in pending.ToArray())
        {
            if (Owns(key, now))
            {
                pending.Remove(key);
                Open(key, now, held.Segments);
            }
            else if (now - held.First >= PendingLimit)
            {
                pending.Remove(key);
                foreign[key] = now + TimeSpan.FromSeconds(30);
            }
        }

        foreach (var flow in flows.Values.ToArray())
        {
            Emit(flow, flow.Assembler.Poll(now));
            if (now - flow.LastSeen > Idle)
                Close(flow);
        }

        foreach (var (key, until) in foreign.ToArray())
            if (until <= now)
                foreign.Remove(key);
        if (DateTimeOffset.UtcNow - lastStatus >= TimeSpan.FromSeconds(2))
        {
            lastStatus = DateTimeOffset.UtcNow;
            Send(ChannelKind.Status, 0, System.Text.Encoding.UTF8.GetBytes($"{state}|{message}|{flows.Count}|{segments}|{dropped}"));
        }
    }

    // --- Channel ---------------------------------------------------------------

    void Listen()
    {
        string directory = Path.GetDirectoryName(options.Socket)!;
        Directory.CreateDirectory(directory);
        // Windows: the folder sits in the user's private local app data.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (File.Exists(options.Socket))
            File.Delete(options.Socket); // stale socket from a previous run
        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(options.Socket));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(options.Socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(1);
        listener.Blocking = false;
    }

    void Accept()
    {
        if (client != null || listener == null)
            return;
        try
        {
            var socket = listener.Accept();
            socket.Blocking = true;
            client = new NetworkStream(socket, ownsSocket: true);
            noClientSince = DateTimeOffset.MaxValue;
            // A new reader starts mid-stream: tell it which flows exist.
            foreach (var flow in flows.Values)
                Send(ChannelKind.FlowOpened, flow.Id, System.Text.Encoding.UTF8.GetBytes(flow.Connection.ToString()));
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
        {
        }
    }

    void WaitForClient(TimeSpan limit)
    {
        var until = DateTimeOffset.UtcNow + limit;
        while (client == null && DateTimeOffset.UtcNow < until)
        {
            Accept();
            Thread.Sleep(20);
        }
    }

    void Send(ChannelKind kind, uint flow, byte[] payload)
    {
        if (client == null)
            return;
        if (!outbox.TryAdd((kind, flow, payload)))
        {
            // The app is not keeping up. Losing bytes is reported as a gap so the
            // decoder resynchronises instead of misreading.
            dropped += payload.Length;
            if (kind == ChannelKind.Data)
                outbox.TryAdd((ChannelKind.Gap, flow, BitConverter.GetBytes((long)payload.Length)), 1000);
        }
    }

    void WriteLoop()
    {
        foreach (var (kind, flow, payload) in outbox.GetConsumingEnumerable())
        {
            var stream = client;
            if (stream == null)
                continue;
            try
            {
                CaptureChannel.Write(stream, kind, flow, payload);
            }
            catch (IOException)
            {
                client = null;
                noClientSince = DateTimeOffset.UtcNow;
                stream.Dispose();
            }
        }

        client?.Flush();
        client?.Dispose();
    }

    static bool Alive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    bool ShouldExit()
    {
        // Looking a process up is costly; once a second is plenty.
        if (options.Parent is int parent && DateTimeOffset.UtcNow - lastParentCheck >= TimeSpan.FromSeconds(1))
        {
            lastParentCheck = DateTimeOffset.UtcNow;
            if (!Alive(parent))
                return true;
        }
        return client == null && DateTimeOffset.UtcNow - noClientSince > NoClientExit;
    }
}
