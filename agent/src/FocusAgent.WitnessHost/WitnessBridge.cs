using System.Threading.Channels;

namespace FocusAgent.WitnessHost;

/// <summary>
/// Bridges the browser's native-messaging stdio to the agent's named pipe
/// (#146 part 1):
///
///   * browser stdin → forwarded to the agent (keeps the pipe live; carries
///     pings). When stdin hits EOF the extension is gone (disabled/removed or
///     the browser closed), so the bridge exits — which closes the pipe and is
///     exactly what the agent's witness watches for.
///   * agent up/down → relayed to the browser as <c>agent_available</c> /
///     <c>agent_unavailable</c> native messages.
///   * agent messages → forwarded to the browser unchanged (#489): the agent is
///     the extension's only source of the session (<c>session_started</c>,
///     <c>allowlist_replaced</c>, <c>session_ended</c>, …), and the extension's
///     reports travel the other way as ordinary forwarded lines.
///
/// Browser-bound messages flow through a channel drained by a single writer
/// task, so the agent link's background up/down callbacks never write stdout
/// concurrently and shutdown flushes whatever is queued.
/// </summary>
public sealed class WitnessBridge
{
    private const string AgentAvailableMessage = "{\"type\":\"agent_available\"}";
    private const string AgentUnavailableMessage = "{\"type\":\"agent_unavailable\"}";

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly IAgentLink _agent;
    private readonly string? _backendUrlMessage;
    private readonly string? _authConfigMessage;
    private readonly Channel<string> _outbound =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    /// <param name="backendUrlMessage">
    /// Optional pre-built <c>backend_url</c> native message (#204) the host hands
    /// the extension as soon as the link opens. Only an extension from before #489
    /// uses it (it connects to the hub itself); a current one ignores it.
    /// </param>
    /// <param name="authConfigMessage">
    /// Optional pre-built <c>auth_config</c> native message (#289) the host hands
    /// the extension up front, for the same older extensions: they sign the student
    /// in through the browser with it. Null in dev.
    /// </param>
    public WitnessBridge(
        Stream input,
        Stream output,
        IAgentLink agent,
        string? backendUrlMessage = null,
        string? authConfigMessage = null)
    {
        _input = input;
        _output = output;
        _agent = agent;
        _backendUrlMessage = backendUrlMessage;
        _authConfigMessage = authConfigMessage;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        _agent.Connected += OnAgentConnected;
        _agent.Disconnected += OnAgentDisconnected;
        _agent.MessageReceived += OnAgentMessage;
        await _agent.StartAsync(ct).ConfigureAwait(false);

        // Hand the extension its backend URL up front (#204). Queued before the
        // read loop and drained by the single writer task, so it lands ahead of
        // any agent up/down message and never races stdout.
        if (_backendUrlMessage is not null)
            _outbound.Writer.TryWrite(_backendUrlMessage);

        // ...and its production auth config (#289), the same way. Both are for an
        // extension from before #489, which still signs in and connects itself.
        if (_authConfigMessage is not null)
            _outbound.Writer.TryWrite(_authConfigMessage);

        var writer = Task.Run(() => DrainOutboundAsync(ct), CancellationToken.None);
        try
        {
            while (true)
            {
                var message = await NativeMessaging.ReadMessageAsync(_input, ct).ConfigureAwait(false);
                if (message is null) break; // browser closed stdin → extension gone
                await _agent.SendAsync(message, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // host shutting down
        }
        finally
        {
            _agent.Connected -= OnAgentConnected;
            _agent.Disconnected -= OnAgentDisconnected;
            _agent.MessageReceived -= OnAgentMessage;
            _outbound.Writer.TryComplete();
            try { await writer.ConfigureAwait(false); } catch { }
            await _agent.StopAsync().ConfigureAwait(false);
        }
    }

    private async Task DrainOutboundAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var json in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await NativeMessaging.WriteMessageAsync(_output, json, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The browser end is gone; the stdin read loop will hit EOF and exit.
        }
    }

    private void OnAgentConnected() => _outbound.Writer.TryWrite(AgentAvailableMessage);

    private void OnAgentDisconnected() => _outbound.Writer.TryWrite(AgentUnavailableMessage);

    // Forward the agent's line as a native message. Edge drops the whole port on a
    // message that isn't JSON, which would read as the extension going away, so a
    // line that isn't a JSON object never reaches it.
    private void OnAgentMessage(string line)
    {
        if (IsJsonObject(line))
            _outbound.Writer.TryWrite(line);
    }

    internal static bool IsJsonObject(string line)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
