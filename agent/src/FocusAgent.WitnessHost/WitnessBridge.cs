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
///     <c>agent_unavailable</c> native messages, so the extension can report
///     <c>agent_unavailable</c> if the agent dies mid-session.
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
    private readonly Channel<string> _outbound =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    public WitnessBridge(Stream input, Stream output, IAgentLink agent)
    {
        _input = input;
        _output = output;
        _agent = agent;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        _agent.Connected += OnAgentConnected;
        _agent.Disconnected += OnAgentDisconnected;
        await _agent.StartAsync(ct).ConfigureAwait(false);

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
}
