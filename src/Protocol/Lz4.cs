namespace ArnoA2.Protocol;

public enum Lz4Result
{
    Done,
    NeedMore,
    Corrupt
}

/// <summary>
/// LZ4 block decoder for game bundles. The bundle header states the decompressed
/// size; decoding stops as soon as that many bytes are produced, because the game
/// does not terminate the block and the following stream bytes are the next frame.
/// </summary>
public static class Lz4
{
    /// <param name="consumed">Input bytes used when the result is <see cref="Lz4Result.Done"/>.</param>
    public static Lz4Result Decode(ReadOnlySpan<byte> input, Span<byte> output, out int consumed)
    {
        consumed = 0;
        int read = 0, written = 0;
        while (true)
        {
            if (read >= input.Length)
                return Lz4Result.NeedMore;
            int token = input[read++];

            int literals = token >> 4;
            if (literals == 15 && !Extend(input, ref read, ref literals, out var state))
                return state;
            if (literals > output.Length - written)
                return Lz4Result.Corrupt;
            if (literals > input.Length - read)
                return Lz4Result.NeedMore;
            input.Slice(read, literals).CopyTo(output[written..]);
            read += literals;
            written += literals;
            if (written == output.Length)
            {
                consumed = read;
                return Lz4Result.Done;
            }

            if (input.Length - read < 2)
                return Lz4Result.NeedMore;
            int distance = input[read] | input[read + 1] << 8;
            read += 2;
            if (distance == 0 || distance > written)
                return Lz4Result.Corrupt;

            int length = token & 15;
            if (length == 15 && !Extend(input, ref read, ref length, out state))
                return state;
            length += 4;
            if (length > output.Length - written)
                return Lz4Result.Corrupt;
            // Overlapping copies repeat the pattern, so copy byte by byte.
            for (int i = 0; i < length; i++, written++)
                output[written] = output[written - distance];
            if (written == output.Length)
            {
                consumed = read;
                return Lz4Result.Done;
            }
        }
    }

    static bool Extend(ReadOnlySpan<byte> input, ref int read, ref int value, out Lz4Result state)
    {
        state = Lz4Result.Done;
        byte b;
        do
        {
            if (read >= input.Length)
            {
                state = Lz4Result.NeedMore;
                return false;
            }

            b = input[read++];
            value += b;
            if (value > FrameStream.MaxBundle)
            {
                state = Lz4Result.Corrupt;
                return false;
            }
        }
        while (b == 255);

        return true;
    }
}
