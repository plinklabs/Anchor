using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
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

    [Fact]
    public async Task Writes_nothing_to_the_browser_until_the_agent_link_says_something()
    {
        // #495: the bridge used to open with a backend_url and an auth_config for
        // extensions from before #489. It has nothing of its own to hand down now.
        using var input = new MemoryStream(); // immediate EOF
        using var output = new MemoryStream();
        var agent = new FakeAgentLink();

        await new WitnessBridge(input, output, agent).RunAsync();

        output.Position = 0;
        Assert.Empty(ReadAllFrames(output));
    }

    [Fact]
    public async Task Forwards_the_agents_messages_to_the_browser_after_it_comes_up()
    {
        // #489: the agent hands the extension its session over the link; the host
        // passes each line on as a native message, in order, after the
        // agent_available it raised on connecting.
        using var input = new MemoryStream(); // immediate EOF
        using var output = new MemoryStream();
        const string started = "{\"type\":\"session_started\",\"sessionId\":\"s1\",\"domains\":[]}";
        const string ended = "{\"type\":\"session_ended\",\"sessionId\":\"s1\"}";
        var agent = new FakeAgentLink
        {
            OnStart = link =>
            {
                link.RaiseConnected();
                link.RaiseMessage(started);
                link.RaiseMessage(ended);
            },
        };

        await new WitnessBridge(input, output, agent).RunAsync();

        output.Position = 0;
        Assert.Equal(new[] { "{\"type\":\"agent_available\"}", started, ended }, ReadAllFrames(output));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"just a string\"")]
    public async Task Drops_an_agent_line_that_is_not_a_json_object(string line)
    {
        // Edge closes the whole port on a message that isn't JSON, which would look
        // like the extension being switched off; such a line must never reach it.
        using var input = new MemoryStream(); // immediate EOF
        using var output = new MemoryStream();
        var agent = new FakeAgentLink { OnStart = link => link.RaiseMessage(line) };

        await new WitnessBridge(input, output, agent).RunAsync();

        output.Position = 0;
        Assert.Empty(ReadAllFrames(output));
    }

    private static byte[] Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        return [.. header, .. payload];
    }

    [Fact]
    public async Task Its_own_messages_are_the_ones_the_witness_contract_lists_from_the_host()
    {
        // contracts/witness.json (#573): the host adds agent_available and
        // agent_unavailable, with no fields, to what it forwards; the extension's
        // unit tests check that it takes them as the file lists them.
        using var input = new MemoryStream(); // immediate EOF
        using var output = new MemoryStream();
        var agent = new FakeAgentLink { OnStart = link => { link.RaiseConnected(); link.RaiseDisconnected(); } };

        await new WitnessBridge(input, output, agent).RunAsync();

        output.Position = 0;
        var fromHost = WitnessContract()["fromHost"]!.AsObject();
        var sent = ReadAllFrames(output).Select(f => JsonNode.Parse(f)!.AsObject()).ToList();
        Assert.Equal(
            fromHost.Select(m => m.Key).Order(StringComparer.Ordinal),
            sent.Select(m => m["type"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        foreach (var message in sent)
        {
            var type = message["type"]!.GetValue<string>();
            Assert.Equal(
                fromHost[type]!.AsObject().Select(f => f.Key).Append("type").Order(StringComparer.Ordinal),
                message.Select(f => f.Key).Order(StringComparer.Ordinal));
        }
    }

    private static JsonObject WitnessContract()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "contracts", "witness.json");
            if (File.Exists(path))
                return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        }
        throw new FileNotFoundException($"No contracts/witness.json above {AppContext.BaseDirectory}.");
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
        public event Action<string>? MessageReceived;

        public void RaiseConnected() => Connected?.Invoke();
        public void RaiseDisconnected() => Disconnected?.Invoke();
        public void RaiseMessage(string line) => MessageReceived?.Invoke(line);

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
