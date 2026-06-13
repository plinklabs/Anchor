using System.Buffers.Binary;
using System.Text;
using FocusAgent.WitnessHost;

namespace FocusAgent.WitnessHost.Tests;

public class NativeMessagingTests
{
    [Fact]
    public async Task Write_then_read_round_trips_a_message()
    {
        using var stream = new MemoryStream();
        await NativeMessaging.WriteMessageAsync(stream, "{\"type\":\"ping\"}");

        stream.Position = 0;
        var read = await NativeMessaging.ReadMessageAsync(stream);

        Assert.Equal("{\"type\":\"ping\"}", read);
    }

    [Fact]
    public async Task Read_returns_null_at_clean_eof()
    {
        using var empty = new MemoryStream();
        Assert.Null(await NativeMessaging.ReadMessageAsync(empty));
    }

    [Fact]
    public async Task Read_throws_on_a_truncated_header()
    {
        using var stream = new MemoryStream([0x01, 0x02]); // 2 of the 4 length bytes
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeMessaging.ReadMessageAsync(stream));
    }

    [Fact]
    public async Task Read_rejects_an_oversize_length_without_allocating_it()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)NativeMessaging.MaxMessageBytes + 1);
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessaging.ReadMessageAsync(stream));
    }

    [Fact]
    public async Task Length_prefix_is_little_endian()
    {
        using var stream = new MemoryStream();
        await NativeMessaging.WriteMessageAsync(stream, "hi"); // 2 UTF-8 bytes

        var bytes = stream.ToArray();
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x00 }, bytes[..4]);
        Assert.Equal("hi", Encoding.UTF8.GetString(bytes[4..]));
    }
}
