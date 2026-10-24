using System.Globalization;
using System.Net;

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
