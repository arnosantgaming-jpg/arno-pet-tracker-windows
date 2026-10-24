using System.Buffers.Binary;

namespace ArnoA2.Protocol;

/// <summary>One game message: a two-byte code (op, family) and its body.</summary>
public sealed record Frame(byte Op, byte Family, byte[] Body)
{
    public ushort Code => (ushort)(Op << 8 | Family);
    public override string ToString() => $"{Op:X2}:{Family:X2} ({Body.Length} bytes)";
}

/// <summary>
/// Splits the server→client byte stream into frames.
///
/// Wire format (all observed in AION 2 captures):
///   frame  = length:varint(1–3 bytes, minimal) op:u8 family:u8 body
///            where the frame occupies (length + varint width − 4) bytes in total;
///   bundle = FF FF size:i32 lz4-block  (decompresses to exactly size bytes of frames);
///   a frame whose op and family are both FF carries a nested bundle as its body.
///
/// The stream may be joined mid-message (tracker started late, capture gap). Bytes
/// that cannot start a frame are skipped one at a time until the stream lines up.
/// Bundles are complete units, so errors inside them are reported, not skipped over.
/// </summary>
public sealed class FrameStream
{
    public const int MaxBundle = 1_000_000, MaxPending = 1 << 20, MaxNesting = 3;
    byte[] buffer = new byte[64 * 1024];
    int start, end;

    public long Frames { get; private set; }
    public long SkippedBytes { get; private set; }
    public long Faults { get; private set; }
    public event Action<Frame>? FrameDecoded;
    public event Action<string>? Faulted;

    /// <summary>Drop buffered bytes, e.g. after a TCP gap.</summary>
    public void Reset() => start = end = 0;

    public void Push(ReadOnlySpan<byte> bytes)
    {
        if (end - start + bytes.Length > MaxPending)
        {
            Reset();
            Fault("Pending data exceeded " + MaxPending + " bytes");
            return;
        }

        Append(bytes);
        while (start < end)
        {
            var pending = buffer.AsSpan(start, end - start);
            int used = Next(pending);
            if (used == 0)
                break; // wait for more bytes
            start += used;
        }

        if (start == end)
            start = end = 0;
    }

    void Append(ReadOnlySpan<byte> bytes)
    {
        if (end + bytes.Length > buffer.Length)
        {
            int length = end - start;
            var target = length + bytes.Length > buffer.Length ? new byte[Math.Max(buffer.Length * 2, length + bytes.Length)] : buffer;
            Buffer.BlockCopy(buffer, start, target, 0, length);
            buffer = target;
            start = 0;
            end = length;
        }

        bytes.CopyTo(buffer.AsSpan(end));
        end += bytes.Length;
    }

    /// <summary>Consume one unit from the front: returns bytes used, 0 to wait.</summary>
    int Next(ReadOnlySpan<byte> data)
    {
        if (data[0] == 0xFF)
        {
            if (data.Length < 2)
                return 0;
            if (data[1] == 0xFF)
                return Bundle(data);
        }

        switch (FrameLength(data, out int width, out int total))
        {
            case Length.Incomplete:
                return 0;
            case Length.Invalid:
                SkippedBytes++;
                return 1;
        }

        if (data.Length < total)
            return 0;
        Emit(data.Slice(width, total - width), 0);
        return total;
    }

    int Bundle(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6)
            return 0;
        int size = BinaryPrimitives.ReadInt32LittleEndian(data[2..]);
        if (size is <= 0 or > MaxBundle)
        {
            SkippedBytes++;
            return 1;
        }

        var output = new byte[size];
        switch (Lz4.Decode(data[6..], output, out int consumed))
        {
            case Lz4Result.NeedMore:
                return 0;
            case Lz4Result.Corrupt:
                Fault("Corrupt LZ4 bundle");
                SkippedBytes += 2;
                return 2;
        }

        ReadAll(output, 1);
        return 6 + consumed;
    }

    enum Length
    {
        Ok,
        Incomplete,
        Invalid
    }

    static Length FrameLength(ReadOnlySpan<byte> data, out int width, out int total)
    {
        width = total = 0;
        int value = 0;
        for (int i = 0; i < 3; i++)
        {
            if (i >= data.Length)
                return Length.Incomplete;
            byte b = data[i];
            value |= (b & 0x7f) << (7 * i);
            if (b >= 0x80)
                continue;
            if (i > 0 && b == 0)
                return Length.Invalid; // over-long encoding
            width = i + 1;
            total = value + width - 4;
            // A frame holds at least its length, op and family.
            return total < width + 2 || total > MaxPending ? Length.Invalid : Length.Ok;
        }

        return Length.Invalid;
    }

    /// <summary>Frames inside a decompressed bundle must tile it exactly.</summary>
    void ReadAll(ReadOnlySpan<byte> data, int depth)
    {
        int at = 0;
        while (at < data.Length)
        {
            if (FrameLength(data[at..], out int width, out int total) != Length.Ok || at + total > data.Length)
            {
                Fault("Bad frame boundary inside a bundle");
                return;
            }

            Emit(data.Slice(at + width, total - width), depth);
            at += total;
        }
    }

    void Emit(ReadOnlySpan<byte> frame, int depth)
    {
        byte op = frame[0], family = frame[1];
        var body = frame[2..];
        if (op == 0xFF && family == 0xFF)
        {
            if (depth >= MaxNesting || body.Length < 4)
            {
                Fault("Bad nested bundle header");
                return;
            }

            int size = BinaryPrimitives.ReadInt32LittleEndian(body);
            if (size is <= 0 or > MaxBundle)
            {
                Fault("Bad nested bundle size");
                return;
            }

            var output = new byte[size];
            if (Lz4.Decode(body[4..], output, out _) != Lz4Result.Done)
            {
                Fault("Corrupt nested bundle");
                return;
            }

            ReadAll(output, depth + 1);
            return;
        }

        Frames++;
        FrameDecoded?.Invoke(new(op, family, body.ToArray()));
    }

    void Fault(string reason)
    {
        Faults++;
        Faulted?.Invoke(reason);
    }
}
