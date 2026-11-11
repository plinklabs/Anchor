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

    [Fact]
    public async Task Hands_down_backend_url_then_auth_config_up_front()
    {
        using var input = new MemoryStream(); // immediate EOF
        using var output = new MemoryStream();
        var agent = new FakeAgentLink();

        await new WitnessBridge(
            input, output, agent,
            backendUrlMessage: "{\"type\":\"backend_url\",\"url\":\"https://anchor.example\"}",
            authConfigMessage: "{\"type\":\"auth_config\",\"tenantId\":\"t\",\"clientId\":\"c\",\"scope\":\"s\"}")
            .RunAsync();

        output.Position = 0;
        var frames = ReadAllFrames(output);
        // Both config messages land before any liveness traffic, backend URL first so
        // the extension knows where to point before it tries to authenticate (#289).
        Assert.Equal(
            new[]
            {
                "{\"type\":\"backend_url\",\"url\":\"https://anchor.example\"}",
                "{\"type\":\"auth_config\",\"tenantId\":\"t\",\"clientId\":\"c\",\"scope\":\"s\"}",
            },
            frames);
    }

    [Fact]
    public async Task Omits_auth_config_when_none_supplied()
    {
        using var input = new MemoryStream(); // immediate EOF
        using var output = new MemoryStream();
        var agent = new FakeAgentLink();

        await new WitnessBridge(
            input, output, agent,
            backendUrlMessage: "{\"type\":\"backend_url\",\"url\":\"https://anchor.example\"}")
            .RunAsync();

        output.Position = 0;
        var frames = ReadAllFrames(output);
        // Dev / no-auth deployment: only the backend URL goes down; the extension then
        // stays on the dev_impersonate_oid path.
        Assert.Equal(
            new[] { "{\"type\":\"backend_url\",\"url\":\"https://anchor.example\"}" },
            frames);
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
