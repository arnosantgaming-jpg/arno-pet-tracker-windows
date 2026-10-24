using System.Buffers.Binary;

namespace ArnoA2.Replay;

/// <summary>One chunk of server→client stream bytes from a recording.</summary>
public sealed record Chunk(DateTimeOffset Time, string Flow, byte[] Data);

/// <summary>Reads recordings: the tracker's own stream dump (PETTRACKER_DUMP) or a
/// tcpdump/libpcap file of the game connection (incoming direction, reordered by
/// sequence number; retransmitted bytes are trimmed).</summary>
public static class Recording
{
    public static IEnumerable<Chunk> Read(string path, int serverPort = 13328)
    {
        using var probe = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        probe.ReadExactly(magic);
        probe.Close();
        uint m = BinaryPrimitives.ReadUInt32LittleEndian(magic);
        return m is 0xa1b2c3d4 or 0xa1b23c4d ? Pcap(path, serverPort) : Dump(path);
    }

    static IEnumerable<Chunk> Dump(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var time = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            reader.ReadInt32();
            reader.ReadInt32();
            string flow = reader.ReadString();
            var data = reader.ReadBytes(reader.ReadInt32());
            yield return new(time, flow, data);
        }
    }

    static List<Chunk> Pcap(string path, int serverPort)
    {
        var chunks = new List<Chunk>();
        var file = File.ReadAllBytes(path);
        bool nanos = BinaryPrimitives.ReadUInt32LittleEndian(file) == 0xa1b23c4d;
        int link = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(20));
        int header = link switch
        {
            276 => 20, // LINUX_SLL2
            113 => 16, // LINUX_SLL
            1 => 14, // Ethernet
            _ => throw new InvalidDataException("Unsupported pcap link type " + link)
        };
        uint? next = null;
        for (int at = 24; at + 16 <= file.Length;)
        {
            uint seconds = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at)), fraction = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4));
            int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 8));
            var packet = file.AsSpan(at + 16, length);
            at += 16 + length;
            var time = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos ? fraction / 100 : fraction * 10L);
            var ip = packet[header..];
            if (ip.Length < 20 || ip[0] >> 4 != 4 || ip[9] != 6)
                continue;
            int ihl = (ip[0] & 15) * 4, total = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
            var tcp = ip[ihl..Math.Min(total, ip.Length)];
            if (BinaryPrimitives.ReadUInt16BigEndian(tcp) != serverPort)
                continue;
            uint sequence = BinaryPrimitives.ReadUInt32BigEndian(tcp[4..]);
            var payload = tcp[((tcp[12] >> 4) * 4)..];
            if (payload.IsEmpty)
                continue;
            next ??= sequence;
            int delta = unchecked((int)(sequence - next.Value));
            if (delta < 0)
            {
                if (-delta >= payload.Length)
                    continue; // retransmission of bytes already delivered
                payload = payload[-delta..];
                sequence = next.Value;
            }
            else if (delta > 0)
                chunks.Add(new(time, "gap", Array.Empty<byte>())); // bytes missing from the capture

            next = unchecked(sequence + (uint)payload.Length);
            chunks.Add(new(time, "game", payload.ToArray()));
        }

        return chunks;
    }
}
