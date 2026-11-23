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
    private readonly WitnessHostLog _log;
    private readonly Channel<string> _outbound =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    /// <param name="log">Where the host writes why it exits (#561); none by default.</param>
    public WitnessBridge(Stream input, Stream output, IAgentLink agent, WitnessHostLog? log = null)
    {
        _input = input;
        _output = output;
        _agent = agent;
        _log = log ?? WitnessHostLog.None;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        _agent.Connected += OnAgentConnected;
        _agent.Disconnected += OnAgentDisconnected;
        _agent.MessageReceived += OnAgentMessage;
        await _agent.StartAsync(ct).ConfigureAwait(false);

        var writer = Task.Run(() => DrainOutboundAsync(ct), CancellationToken.None);
        try
        {
            while (true)
            {
                var message = await NativeMessaging.ReadMessageAsync(_input, ct).ConfigureAwait(false);
                if (message is null)
                {
                    // browser closed stdin → extension gone
                    _log.Info("Exiting: Edge closed the extension's port (stdin ended).");
                    break;
                }
                await _agent.SendAsync(message, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // host shutting down
            _log.Info("Exiting: the host was stopped.");
        }
        catch (Exception ex)
        {
            _log.Warn($"Exiting: reading from Edge failed: {WitnessHostLog.Describe(ex)}");
            throw;
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
