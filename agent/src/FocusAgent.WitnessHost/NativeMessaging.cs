using System.Buffers.Binary;
using System.Text;

namespace FocusAgent.WitnessHost;

/// <summary>
/// Chrome/Edge native-messaging stdio framing: each message is a 4-byte
/// little-endian length prefix followed by that many UTF-8 JSON bytes. The
/// browser closes stdin when the port disconnects, which we surface as a clean
/// EOF (null) so the host can exit — the very signal the agent witness watches
/// for (#146 part 1).
///
/// Pure and stream-based so the framing is unit-tested with MemoryStreams,
/// without a real browser or pipe.
/// </summary>
public static class NativeMessaging
{
    /// <summary>
    /// Chrome caps a single host→browser message at 1 MB; browser→host at 4 GB.
    /// We only ever exchange tiny control messages, so reject anything larger as
    /// a framing error rather than allocating on a bogus length.
    /// </summary>
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>
    /// Reads one length-prefixed UTF-8 message. Returns <c>null</c> at a clean
    /// end-of-stream (the browser closed stdin), and throws
    /// <see cref="EndOfStreamException"/> on a truncated frame.
    /// </summary>
    public static async Task<string?> ReadMessageAsync(Stream input, CancellationToken ct = default)
    {
        var lengthBuf = new byte[4];

        // Distinguish a clean EOF (0 bytes available) from a truncated header.
        var firstRead = await input.ReadAsync(lengthBuf.AsMemory(0, 1), ct).ConfigureAwait(false);
        if (firstRead == 0) return null;

        await input.ReadExactlyAsync(lengthBuf.AsMemory(1, 3), ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuf);
        if (length == 0) return string.Empty;
        if (length > MaxMessageBytes)
            throw new InvalidDataException($"Native message length {length} exceeds {MaxMessageBytes}.");

        var payload = new byte[length];
        await input.ReadExactlyAsync(payload.AsMemory(0, (int)length), ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    /// <summary>Writes one length-prefixed UTF-8 message and flushes.</summary>
    public static async Task WriteMessageAsync(Stream output, string json, CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var lengthBuf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthBuf, (uint)payload.Length);
        await output.WriteAsync(lengthBuf, ct).ConfigureAwait(false);
        await output.WriteAsync(payload, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }
}
