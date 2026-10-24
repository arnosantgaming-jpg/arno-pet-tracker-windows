namespace ArnoA2.Capture;

/// <summary>Output of reassembly: in-order bytes, or a gap of known size.</summary>
public abstract record StreamEvent;
public sealed record StreamData(byte[] Bytes) : StreamEvent;
public sealed record StreamGap(long LostBytes) : StreamEvent;

/// <summary>
/// Puts one direction of a TCP connection back in order. Retransmitted and
/// overlapping bytes are trimmed. Out-of-order segments wait for the missing bytes
/// for up to <see cref="Window"/> — a lost packet is normally resent well within
/// that — after which the hole is declared a gap and delivery continues.
/// Sequence numbers wrap at 2^32; comparisons use signed differences.
/// </summary>
public sealed class StreamAssembler
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);
    public const int MaxBufferedBytes = 4 << 20, MaxSegments = 2048;

    readonly SortedDictionary<long, byte[]> waiting = new(); // keyed by offset from `next`'s epoch
    uint? next;
    long epoch; // unwrapped position of `next`
    int buffered;
    DateTimeOffset? holeSince;

    public long Delivered { get; private set; }
    public long Lost { get; private set; }

    /// <summary>The connection (re)started: the first data byte follows this SYN.</summary>
    public void Start(uint synSequence)
    {
        waiting.Clear();
        buffered = 0;
        holeSince = null;
        next = unchecked(synSequence + 1);
        epoch = 0;
    }

    public List<StreamEvent> Add(uint sequence, ReadOnlySpan<byte> payload, DateTimeOffset now)
    {
        var output = new List<StreamEvent>();
        if (payload.IsEmpty)
            return output;
        next ??= sequence; // joined mid-stream: start at the first byte seen
        long at = epoch + unchecked((int)(sequence - next.Value));
        long end = at + payload.Length;
        if (end <= epoch)
            return output; // entirely old (retransmission)
        if (at < epoch)
        {
            payload = payload[(int)(epoch - at)..];
            at = epoch;
        }

        if (at == epoch)
        {
            Deliver(payload.ToArray(), output);
            Drain(output);
        }
        else if (!waiting.TryGetValue(at, out var existing) || existing.Length < payload.Length)
        {
            if (existing != null)
                buffered -= existing.Length;
            waiting[at] = payload.ToArray();
            buffered += payload.Length;
            holeSince ??= now;
            while (buffered > MaxBufferedBytes || waiting.Count > MaxSegments)
                SkipHole(output, now);
        }

        holeSince = waiting.Count == 0 ? null : holeSince ?? now;
        return output;
    }

    /// <summary>Declare a hole lost once it is older than the window.</summary>
    public List<StreamEvent> Poll(DateTimeOffset now)
    {
        var output = new List<StreamEvent>();
        if (holeSince is { } since && now - since >= Window)
            SkipHole(output, now);
        return output;
    }

    void Deliver(byte[] bytes, List<StreamEvent> output)
    {
        output.Add(new StreamData(bytes));
        epoch += bytes.Length;
        next = unchecked(next!.Value + (uint)bytes.Length);
        Delivered += bytes.Length;
    }

    void Drain(List<StreamEvent> output)
    {
        while (waiting.Count > 0)
        {
            var (at, bytes) = waiting.First();
            if (at > epoch)
                break;
            waiting.Remove(at);
            buffered -= bytes.Length;
            long overlap = epoch - at;
            if (overlap < bytes.Length)
                Deliver(bytes[(int)overlap..], output);
        }

        if (waiting.Count == 0)
            holeSince = null;
    }

    void SkipHole(List<StreamEvent> output, DateTimeOffset now)
    {
        if (waiting.Count == 0)
            return;
        long first = waiting.Keys.First(), lost = first - epoch;
        next = unchecked(next!.Value + (uint)lost);
        epoch = first;
        Lost += lost;
        output.Add(new StreamGap(lost));
        Drain(output);
        // A further hole gets its own full window from now.
        holeSince = waiting.Count == 0 ? null : now;
    }
}
