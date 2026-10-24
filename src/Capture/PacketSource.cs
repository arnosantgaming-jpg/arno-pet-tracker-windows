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
    const string Lib = "libpcap.so.1";
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

    IntPtr handle;
    readonly int link;
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

    /// <summary>Opens live capture on all interfaces. Needs CAP_NET_RAW.</summary>
    public static PacketSource Live()
    {
        var error = new byte[256];
        var p = pcap_create("any", error);
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
            throw new IOException(status == -8 ? "Permission denied: the capture program needs cap_net_raw (" + message + ")" : message);
        }

        return new(p, offline: false);
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
        long seconds = Marshal.ReadInt64(header), micros = Marshal.ReadInt64(header, 8);
        int captured = Marshal.ReadInt32(header, 16);
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
