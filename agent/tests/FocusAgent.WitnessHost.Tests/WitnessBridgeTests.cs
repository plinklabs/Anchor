using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using FocusAgent.WitnessHost;

namespace FocusAgent.WitnessHost.Tests;

public class WitnessBridgeTests
{
    [Fact]
    public async Task Forwards_browser_messages_to_the_agent_then_exits_on_eof()
    {
        using var input = new MemoryStream(Frame("{\"type\":\"ping\"}"));
        using var output = new MemoryStream();
        var agent = new FakeAgentLink();

        await new WitnessBridge(input, output, agent).RunAsync();

        Assert.Equal("{\"type\":\"ping\"}", Assert.Single(agent.Sent));
        Assert.True(agent.Stopped);
    }

    [Fact]
    public async Task Relays_agent_up_and_down_to_the_browser()
    {
        using var input = new MemoryStream(); // immediate EOF
        using var output = new MemoryStream();
        // The link reports the agent appearing then disappearing during startup.
        var agent = new FakeAgentLink { OnStart = link => { link.RaiseConnected(); link.RaiseDisconnected(); } };

        await new WitnessBridge(input, output, agent).RunAsync();

        output.Position = 0;
        var frames = ReadAllFrames(output);
        Assert.Equal(
            new[] { "{\"type\":\"agent_available\"}", "{\"type\":\"agent_unavailable\"}" },
            frames);
    }

    private static byte[] Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        return [.. header, .. payload];
    }

    private static List<string> ReadAllFrames(Stream stream)
    {
        var frames = new List<string>();
        var header = new byte[4];
        while (stream.Read(header, 0, 4) == 4)
        {
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
            var payload = new byte[len];
            stream.ReadExactly(payload, 0, len);
            frames.Add(Encoding.UTF8.GetString(payload));
        }
        return frames;
    }

    private sealed class FakeAgentLink : IAgentLink
    {
        public ConcurrentQueue<string> Sent { get; } = new();
        public bool Stopped { get; private set; }
        public Action<FakeAgentLink>? OnStart { get; init; }

        public event Action? Connected;
        public event Action? Disconnected;

        public void RaiseConnected() => Connected?.Invoke();
        public void RaiseDisconnected() => Disconnected?.Invoke();

        public Task StartAsync(CancellationToken ct = default)
        {
            OnStart?.Invoke(this);
            return Task.CompletedTask;
        }

        public Task SendAsync(string line, CancellationToken ct = default)
        {
            Sent.Enqueue(line);
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }
}
