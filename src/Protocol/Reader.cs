using System.Buffers.Binary;

namespace ArnoA2.Protocol;

/// <summary>
/// Forward-only reader over a message body. Every read is bounds-checked and
/// returns false instead of throwing, so decoders can reject malformed input
/// with a single early return. Integers are little-endian.
/// </summary>
public ref struct Reader(ReadOnlySpan<byte> data)
{
    readonly ReadOnlySpan<byte> data = data;
    public int Position { get; private set; }
    public readonly int Remaining => data.Length - Position;
    public readonly ReadOnlySpan<byte> Rest => data[Position..];

    public bool Skip(int count)
    {
        if (count < 0 || count > Remaining)
            return false;
        Position += count;
        return true;
    }

    public bool Byte(out byte value)
    {
        value = 0;
        if (Remaining < 1)
            return false;
        value = data[Position++];
        return true;
    }

    public readonly bool Peek(int offset, out byte value)
    {
        value = 0;
        if (offset < 0 || offset >= Remaining)
            return false;
        value = data[Position + offset];
        return true;
    }

    public bool U16(out ushort value)
    {
        value = 0;
        if (Remaining < 2)
            return false;
        value = BinaryPrimitives.ReadUInt16LittleEndian(data[Position..]);
        Position += 2;
        return true;
    }

    public bool I32(out int value)
    {
        value = 0;
        if (Remaining < 4)
            return false;
        value = BinaryPrimitives.ReadInt32LittleEndian(data[Position..]);
        Position += 4;
        return true;
    }

    public bool U32(out uint value)
    {
        value = 0;
        if (Remaining < 4)
            return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(data[Position..]);
        Position += 4;
        return true;
    }

    public bool I64(out long value)
    {
        value = 0;
        if (Remaining < 8)
            return false;
        value = BinaryPrimitives.ReadInt64LittleEndian(data[Position..]);
        Position += 8;
        return true;
    }

    public bool F32(out float value)
    {
        value = 0;
        if (Remaining < 4)
            return false;
        value = BinaryPrimitives.ReadSingleLittleEndian(data[Position..]);
        Position += 4;
        return true;
    }

    /// <summary>Unsigned LEB128 of at most five bytes that fits in an int. Rejects
    /// over-long encodings (a zero final byte after the first), which never occur
    /// in game traffic and usually mean the reader is misaligned.</summary>
    public bool VarInt(out int value)
    {
        value = 0;
        int start = Position;
        uint result = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            if (Position >= data.Length)
                break;
            byte b = data[Position++];
            if (shift == 28 && b > 0x07)
                break;
            result |= (uint)(b & 0x7f) << shift;
            if (b < 0x80)
            {
                if (shift > 0 && b == 0 || result > int.MaxValue)
                    break;
                value = (int)result;
                return true;
            }
        }

        Position = start;
        return false;
    }

    public bool Bytes(int count, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (count < 0 || count > Remaining)
            return false;
        value = data.Slice(Position, count);
        Position += count;
        return true;
    }
}
