using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace ArnoA2.Protocol;

public enum ChannelKind : byte
{
    /// <summary>A game connection was found; payload is a UTF-8 description.</summary>
    FlowOpened = 1,
    /// <summary>The next in-order bytes the server sent on that connection.</summary>
    Data = 2,
    /// <summary>Bytes were lost (capture drop or unrepaired loss); payload is i64 count.</summary>
    Gap = 3,
    FlowClosed = 4,
    /// <summary>Daemon state; payload is UTF-8 "state|message|flows|segments|dropped".</summary>
    Status = 5
}

public sealed record ChannelMessage(ChannelKind Kind, uint Flow, byte[] Payload)
{
    public string Text => Encoding.UTF8.GetString(Payload);
    public long LostBytes => BinaryPrimitives.ReadInt64LittleEndian(Payload);
}

/// <summary>
/// The capture daemon's link to the app: a Unix stream socket in the user's runtime
/// directory (mode 0700), carrying length-prefixed records
/// kind:u8 flow:u32 length:u32 payload. Only the server→client direction of game
/// connections is ever sent.
/// </summary>
public static class CaptureChannel
{
    public const int HeaderSize = 9, MaxPayload = 1 << 20;

    public static string DefaultPath()
    {
        // Windows 10+ supports AF_UNIX sockets; the per-user local app data folder is private.
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "arno-pet-tracker", "capture.sock");
        string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Path.GetTempPath(), "arno-pet-tracker-" + Environment.UserName);
        return Path.Combine(runtime, "arno-pet-tracker", "capture.sock");
    }

    public static void Write(Stream stream, ChannelKind kind, uint flow, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        header[0] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(header[1..], flow);
        BinaryPrimitives.WriteInt32LittleEndian(header[5..], payload.Length);
        stream.Write(header);
        stream.Write(payload);
    }

    public static void Write(Stream stream, ChannelKind kind, uint flow, string text) => Write(stream, kind, flow, Encoding.UTF8.GetBytes(text));

    public static void WriteGap(Stream stream, uint flow, long lost)
    {
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, lost);
        Write(stream, ChannelKind.Gap, flow, payload);
    }

    /// <summary>Reads one record; null at end of stream.</summary>
    public static async Task<ChannelMessage?> ReadAsync(Stream stream, CancellationToken cancel = default)
    {
        var header = new byte[HeaderSize];
        if (!await Fill(stream, header, cancel))
            return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(5));
        if (length is < 0 or > MaxPayload || header[0] is < 1 or > 5)
            throw new InvalidDataException("Malformed capture channel record");
        var payload = new byte[length];
        if (!await Fill(stream, payload, cancel))
            return null;
        return new((ChannelKind)header[0], BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1)), payload);
    }

    static async Task<bool> Fill(Stream stream, byte[] buffer, CancellationToken cancel)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), cancel);
            if (n == 0)
                return false;
            read += n;
        }

        return true;
    }

    public static async Task<Stream> ConnectAsync(string path, CancellationToken cancel = default)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancel);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
