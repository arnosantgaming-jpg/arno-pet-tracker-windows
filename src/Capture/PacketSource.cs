using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace ArnoA2.Capture;

/// <summary>An incoming TCP segment with payload (or a SYN), as captured.</summary>
public readonly record struct Segment(DateTimeOffset Time, IPAddress Source, int SourcePort, IPAddress Destination, int DestinationPort, uint Sequence, bool Syn, bool Fin, bool Reset, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Minimal libpcap binding: live capture on the "any" device (one copy of every packet,
/// whatever the interface or VPN) or offline reading of a capture file. Only what the
/// daemon needs; errors throw <see cref="IOException"/> with libpcap's message.
/// </summary>
public sealed class PacketSource : IDisposable
{
    // Resolved per system: libpcap on Linux, Npcap's wpcap.dll on Windows.
    const string Lib = "pcap";
    [DllImport(Lib)] static extern int pcap_findalldevs(out IntPtr devices, byte[] error);
    [DllImport(Lib)] static extern void pcap_freealldevs(IntPtr devices);
    [DllImport(Lib)] static extern IntPtr pcap_create(string source, byte[] error);
    [DllImport(Lib)] static extern IntPtr pcap_open_offline(string path, byte[] error);
    [DllImport(Lib)] static extern int pcap_set_snaplen(IntPtr p, int snaplen);
    [DllImport(Lib)] static extern int pcap_set_promisc(IntPtr p, int on);
    [DllImport(Lib)] static extern int pcap_set_timeout(IntPtr p, int milliseconds);
    [DllImport(Lib)] static extern int pcap_set_immediate_mode(IntPtr p, int on);
    [DllImport(Lib)] static extern int pcap_set_buffer_size(IntPtr p, int bytes);
    [DllImport(Lib)] static extern int pcap_activate(IntPtr p);
    [DllImport(Lib)] static extern int pcap_datalink(IntPtr p);
    [DllImport(Lib)] static extern int pcap_compile(IntPtr p, ref BpfProgram program, string filter, int optimize, uint netmask);
    [DllImport(Lib)] static extern int pcap_setfilter(IntPtr p, ref BpfProgram program);
    [DllImport(Lib)] static extern void pcap_freecode(ref BpfProgram program);
    [DllImport(Lib)] static extern int pcap_next_ex(IntPtr p, out IntPtr header, out IntPtr data);
    [DllImport(Lib)] static extern void pcap_breakloop(IntPtr p);
    [DllImport(Lib)] static extern IntPtr pcap_geterr(IntPtr p);
    [DllImport(Lib)] static extern void pcap_close(IntPtr p);

    [StructLayout(LayoutKind.Sequential)]
    struct BpfProgram
    {
        public uint Length;
        public IntPtr Instructions;
    }

    static PacketSource() => NativeLibrary.SetDllImportResolver(typeof(PacketSource).Assembly, (name, assembly, path) =>
    {
        if (name != Lib)
            return IntPtr.Zero;
        if (OperatingSystem.IsWindows())
        {
            // Npcap installs wpcap.dll in System32\Npcap unless "WinPcap compatible mode" was chosen.
            string npcap = Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll");
            if (NativeLibrary.TryLoad(npcap, out var found) || NativeLibrary.TryLoad("wpcap.dll", out found))
                return found;
            throw new DllNotFoundException("Npcap is not installed. Install it from https://npcap.com/ and restart the tracker.");
        }

        return NativeLibrary.TryLoad("libpcap.so.1", out var lib) || NativeLibrary.TryLoad("libpcap.so", out lib) ? lib : IntPtr.Zero;
    });

    IntPtr handle;
    readonly int link;
    public string Device { get; init; } = "";
    public bool Offline { get; }

    PacketSource(IntPtr handle, bool offline)
    {
        this.handle = handle;
        Offline = offline;
        link = pcap_datalink(handle);
        if (Header(link) < 0)
        {
            pcap_close(handle);
            throw new IOException("Unsupported capture link type " + link);
        }
    }

    /// <summary>
    /// Live capture on every interface: Linux's "any" device (one copy of each packet), or on
    /// Windows each adapter, Npcap's loopback adapter included (Windows has no "any" device; a
    /// packet seen on two adapters is a duplicate the reassembler drops). Linux needs CAP_NET_RAW; Npcap's
    /// default install lets normal users capture.
    /// </summary>
    public static IReadOnlyList<PacketSource> LiveAll()
    {
        if (!OperatingSystem.IsWindows())
            return new[] { Live("any") };
        var sources = new List<PacketSource>();
        var errors = new List<string>();
        foreach (var (name, description) in Devices())
        {
            try
            {
                sources.Add(Live(name));
            }
            catch (IOException ex)
            {
                errors.Add(description + ": " + ex.Message);
            }
        }

        return sources.Count > 0 ? sources : throw new IOException("No network adapter could be opened for capture. " + string.Join("; ", errors));
    }

    /// <summary>Capture devices, as (name, description). Loopback is kept: behind a local relay
    /// (ExitLag and similar) the game's traffic only appears there.</summary>
    static List<(string Name, string Description)> Devices()
    {
        var error = new byte[256];
        if (pcap_findalldevs(out var list, error) != 0)
            throw new IOException(Text(error));
        var found = new List<(string, string)>();
        try
        {
            // struct pcap_if { pcap_if* next; char* name; char* description; pcap_addr* addresses; bpf_u_int32 flags; }
            for (var at = list; at != IntPtr.Zero; at = Marshal.ReadIntPtr(at))
            {
                string name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(at, IntPtr.Size)) ?? "";
                string description = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(at, 2 * IntPtr.Size)) ?? name;
                if (name.Length > 0)
                    found.Add((name, description));
            }
        }
        finally
        {
            pcap_freealldevs(list);
        }

        return found;
    }

    static PacketSource Live(string device)
    {
        var error = new byte[256];
        var p = pcap_create(device, error);
        if (p == IntPtr.Zero)
            throw new IOException(Text(error));
        pcap_set_snaplen(p, 65535);
        pcap_set_promisc(p, 0);
        pcap_set_timeout(p, 100);
        pcap_set_immediate_mode(p, 1);
        pcap_set_buffer_size(p, 16 << 20);
        int status = pcap_activate(p);
        if (status < 0)
        {
            string message = Marshal.PtrToStringUTF8(pcap_geterr(p)) ?? "pcap_activate failed";
            pcap_close(p);
            if (status == -8)
                message = OperatingSystem.IsWindows()
                    ? "Permission denied: Npcap was installed with \"Restrict Npcap driver's access to Administrators only\". Reinstall Npcap without that option. (" + message + ")"
                    : "Permission denied: the capture program needs cap_net_raw (" + message + ")";
            throw new IOException(message);
        }

        return new(p, offline: false) { Device = device };
    }

    public static PacketSource File(string path)
    {
        var error = new byte[256];
        var p = pcap_open_offline(path, error);
        return p == IntPtr.Zero ? throw new IOException(Text(error)) : new(p, offline: true);
    }

    static string Text(byte[] error) => System.Text.Encoding.UTF8.GetString(error).TrimEnd('\0');

    public void Filter(string expression)
    {
        var program = new BpfProgram();
        if (pcap_compile(handle, ref program, expression, 1, 0xffffffff) != 0)
            throw new IOException("Bad capture filter: " + Marshal.PtrToStringUTF8(pcap_geterr(handle)));
        try
        {
            if (pcap_setfilter(handle, ref program) != 0)
                throw new IOException("Could not set capture filter: " + Marshal.PtrToStringUTF8(pcap_geterr(handle)));
        }
        finally
        {
            pcap_freecode(ref program);
        }
    }

    /// <summary>Next TCP segment, null on read timeout. Throws at end of file or error.</summary>
    public Segment? Next(out bool end)
    {
        end = false;
        int result = pcap_next_ex(handle, out var header, out var data);
        if (result == 0)
            return null;
        if (result == -2)
        {
            end = true;
            return null;
        }

        if (result < 0)
            throw new IOException(Marshal.PtrToStringUTF8(pcap_geterr(handle)) ?? "Capture failed");
        // struct pcap_pkthdr { struct timeval ts; bpf_u_int32 caplen; bpf_u_int32 len; } (LP64)
        // struct pcap_pkthdr { struct timeval ts; bpf_u_int32 caplen; bpf_u_int32 len; }:
        // timeval fields are 64-bit on Linux (LP64) but 32-bit on Windows (LLP64).
        long seconds, micros;
        int captured;
        if (OperatingSystem.IsWindows())
        {
            seconds = (uint)Marshal.ReadInt32(header);
            micros = Marshal.ReadInt32(header, 4);
            captured = Marshal.ReadInt32(header, 8);
        }
        else
        {
            seconds = Marshal.ReadInt64(header);
            micros = Marshal.ReadInt64(header, 8);
            captured = Marshal.ReadInt32(header, 16);
        }
        var bytes = new byte[captured];
        Marshal.Copy(data, bytes, 0, captured);
        var time = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(micros * 10);
        return Parse(time, bytes, link);
    }

    public void Stop() => pcap_breakloop(handle);

    /// <summary>Link-layer header length, or -1 when unsupported.</summary>
    static int Header(int link) => link switch
    {
        1 => 14, // Ethernet
        113 => 16, // LINUX_SLL ("any", older libpcap)
        276 => 20, // LINUX_SLL2
        0 or 108 => 4, // BSD loopback
        12 or 101 => 0, // raw IP
        _ => -1
    };

    public static Segment? Parse(DateTimeOffset time, byte[] packet, int link)
    {
        int at = Header(link);
        if (at < 0 || packet.Length < at + 20)
            return null;
        var ip = packet.AsSpan(at);
        int version = ip[0] >> 4, ipHeader, protocol, total;
        IPAddress source, destination;
        if (version == 4)
        {
            ipHeader = (ip[0] & 15) * 4;
            protocol = ip[9];
            total = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
            // Fragments other than the first carry no TCP header.
            if ((BinaryPrimitives.ReadUInt16BigEndian(ip[6..]) & 0x1fff) != 0)
                return null;
            source = new IPAddress(ip.Slice(12, 4));
            destination = new IPAddress(ip.Slice(16, 4));
        }
        else if (version == 6 && ip.Length >= 40)
        {
            ipHeader = 40;
            protocol = ip[6]; // extension headers are not used by the game connection
            total = 40 + BinaryPrimitives.ReadUInt16BigEndian(ip[4..]);
            source = new IPAddress(ip.Slice(8, 16));
            destination = new IPAddress(ip.Slice(24, 16));
        }
        else
            return null;

        if (protocol != 6 || total > ip.Length || ipHeader + 20 > total)
            return null;
        var tcp = ip[ipHeader..total];
        int tcpHeader = (tcp[12] >> 4) * 4;
        if (tcpHeader < 20 || tcpHeader > tcp.Length)
            return null;
        byte flags = tcp[13];
        var payload = packet.AsMemory(at + ipHeader + tcpHeader, total - ipHeader - tcpHeader);
        return new(time, source, BinaryPrimitives.ReadUInt16BigEndian(tcp), destination, BinaryPrimitives.ReadUInt16BigEndian(tcp[2..]), BinaryPrimitives.ReadUInt32BigEndian(tcp[4..]), (flags & 0x02) != 0, (flags & 0x01) != 0, (flags & 0x04) != 0, payload);
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
            pcap_close(handle);
        handle = IntPtr.Zero;
    }
}
