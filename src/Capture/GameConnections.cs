using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;

namespace ArnoA2.Capture;

/// <summary>A TCP connection of the game process, seen from this machine.</summary>
public readonly record struct Connection(IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort)
{
    public override string ToString() => $"{Local}:{LocalPort} ⇄ {Remote}:{RemotePort}";
}

/// <summary>
/// Finds the game's TCP connections through procfs: processes whose program is
/// AION2.exe (Proton/Wine keep the Windows path in argv[0], or argv[1] behind the
/// Wine loader), their socket inodes in /proc/&lt;pid&gt;/fd, and those inodes in
/// /proc/net/tcp and tcp6. Only open connections to non-loopback peers that
/// are not HTTPS (port 443: launcher, telemetry) are kept, including connections
/// still being opened. Nothing outside the game
/// process is ever selected.
/// </summary>
public sealed class GameConnections(string procRoot = "/proc")
{
    static readonly string[] GameNames = { "aion2.exe", "aion2-win64-shipping.exe" };

    public IReadOnlyList<int> Processes { get; private set; } = Array.Empty<int>();

    public IReadOnlyList<Connection> Scan()
    {
        if (OperatingSystem.IsWindows())
            return ScanWindows();
        var pids = new List<int>();
        var inodes = new HashSet<ulong>();
        foreach (string directory in Directory.EnumerateDirectories(procRoot))
        {
            if (!int.TryParse(Path.GetFileName(directory), out int pid))
                continue;
            try
            {
                if (!IsGame(File.ReadAllBytes(Path.Combine(directory, "cmdline"))))
                    continue;
                pids.Add(pid);
                foreach (string fd in Directory.EnumerateFileSystemEntries(Path.Combine(directory, "fd")))
                {
                    string? target = new FileInfo(fd).LinkTarget;
                    if (target is not null && target.StartsWith("socket:[", StringComparison.Ordinal) && ulong.TryParse(target.AsSpan(8, target.Length - 9), out ulong inode))
                        inodes.Add(inode);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Exited during the scan, or another user's process.
            }
        }

        Processes = pids;
        var found = new List<Connection>();
        foreach (string table in new[] { "net/tcp", "net/tcp6" })
        {
            string path = Path.Combine(procRoot, table);
            if (inodes.Count > 0 && File.Exists(path))
                found.AddRange(Parse(File.ReadLines(path), inodes));
        }

        return found.Distinct().ToArray();
    }

    // --- Windows: the TCP tables with owning process ids (iphlpapi) -------------------

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
    const int AfInet = 2, AfInet6 = 23, TcpTableOwnerPidAll = 5;

    IReadOnlyList<Connection> ScanWindows()
    {
        var pids = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
                if (GameNames.Contains(process.ProcessName.ToLowerInvariant() + ".exe"))
                    pids.Add(process.Id);
        }

        Processes = pids.ToArray();
        if (pids.Count == 0)
            return Array.Empty<Connection>();
        var found = new List<Connection>();
        if (Table(AfInet) is { } v4)
            found.AddRange(ParseTable4(v4, pids));
        if (Table(AfInet6) is { } v6)
            found.AddRange(ParseTable6(v6, pids));
        return found.Distinct().ToArray();
    }

    static byte[]? Table(int family)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidAll, 0);
        for (int attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                int request = size;
                uint result = GetExtendedTcpTable(buffer, ref request, false, family, TcpTableOwnerPidAll, 0);
                if (result == 0)
                {
                    var bytes = new byte[request];
                    Marshal.Copy(buffer, bytes, 0, request);
                    return bytes;
                }

                size = request + 1024; // the table grew between calls
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    // MIB_TCP_STATE: 3 SYN sent, 5 established.
    static bool Open(uint state) => state is 3 or 5;
    static int Port(ReadOnlySpan<byte> dword) => BinaryPrimitives.ReadUInt16BigEndian(dword);

    /// <summary>MIB_TCPTABLE_OWNER_PID: count, then rows of state, local addr, local port,
    /// remote addr, remote port, pid (six DWORDs; addresses and ports in network order).</summary>
    public static IEnumerable<Connection> ParseTable4(byte[] table, IReadOnlySet<int> pids)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(table);
        for (int i = 0; i < count && 4 + (i + 1) * 24 <= table.Length; i++)
        {
            var row = table.AsSpan(4 + i * 24, 24);
            uint state = BinaryPrimitives.ReadUInt32LittleEndian(row);
            int pid = BinaryPrimitives.ReadInt32LittleEndian(row[20..]);
            if (!Open(state) || !pids.Contains(pid))
                continue;
            var remote = new IPAddress(row.Slice(12, 4));
            int remotePort = Port(row[16..]);
            // Loopback stays in: game boosters such as ExitLag route the game through a
            // local relay, so its game connection goes to 127.0.0.1.
            if (remotePort == 443)
                continue;
            yield return new(new IPAddress(row.Slice(4, 4)), Port(row[8..]), remote, remotePort);
        }
    }

    /// <summary>MIB_TCP6TABLE_OWNER_PID rows: local addr[16], scope, port, remote addr[16],
    /// scope, port, state, pid (56 bytes).</summary>
    public static IEnumerable<Connection> ParseTable6(byte[] table, IReadOnlySet<int> pids)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(table);
        for (int i = 0; i < count && 4 + (i + 1) * 56 <= table.Length; i++)
        {
            var row = table.AsSpan(4 + i * 56, 56);
            uint state = BinaryPrimitives.ReadUInt32LittleEndian(row[48..]);
            int pid = BinaryPrimitives.ReadInt32LittleEndian(row[52..]);
            if (!Open(state) || !pids.Contains(pid))
                continue;
            var remote = new IPAddress(row.Slice(24, 16));
            int remotePort = Port(row[44..]);
            if (remote.IsIPv4MappedToIPv6)
                remote = remote.MapToIPv4();
            if (remotePort == 443)
                continue;
            var local = new IPAddress(row[..16]);
            yield return new(local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local, Port(row[20..]), remote, remotePort);
        }
    }

    // --- Linux: procfs ---------------------------------------------------------------

    /// <summary>argv[0], or argv[1] behind a Wine loader, names the game executable.</summary>
    public static bool IsGame(ReadOnlySpan<byte> cmdline)
    {
        var args = System.Text.Encoding.UTF8.GetString(cmdline).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length == 0)
            return false;
        string program = FileName(args[0]);
        if (program is "wine" or "wine64" or "wine-preloader" or "wine64-preloader" && args.Length > 1)
            program = FileName(args[1]);
        return GameNames.Contains(program);
    }

    static string FileName(string path) => path.Replace('\\', '/').Split('/')[^1].ToLowerInvariant();

    /// <summary>Rows of /proc/net/tcp[6] owned by the given inodes, open, not loopback, not HTTPS.</summary>
    public static IEnumerable<Connection> Parse(IEnumerable<string> lines, IReadOnlySet<ulong> inodes)
    {
        foreach (string line in lines.Skip(1))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // sl local remote st tx:rx tr:when retrnsmt uid timeout inode …
            // 01 established, 02 SYN sent (connecting: the server's first bytes follow at once).
            if (fields.Length < 10 || fields[3] is not ("01" or "02") || !ulong.TryParse(fields[9], out ulong inode) || !inodes.Contains(inode))
                continue;
            if (!Endpoint(fields[1], out var local, out int localPort) || !Endpoint(fields[2], out var remote, out int remotePort))
                continue;
            if (IPAddress.IsLoopback(remote) || remotePort == 443)
                continue;
            yield return new(local, localPort, remote, remotePort);
        }
    }

    /// <summary>"0100007F:1F90": address words in host byte order (little-endian here), port big-endian hex.</summary>
    public static bool Endpoint(string text, out IPAddress address, out int port)
    {
        address = IPAddress.None;
        port = 0;
        int colon = text.IndexOf(':');
        if (colon is not (8 or 32) || !int.TryParse(text.AsSpan(colon + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out port))
            return false;
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(text.AsSpan(0, colon));
        }
        catch (FormatException)
        {
            return false;
        }

        for (int i = 0; i < bytes.Length; i += 4)
            Array.Reverse(bytes, i, 4);
        address = new IPAddress(bytes);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return true;
    }
}
