using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Tamper;

/// <summary>
/// Serves the local named pipe the browser-launched native host connects to,
/// and surfaces its connect/disconnect to <see cref="ExtensionWitnessMonitor"/>
/// (#146 part 1). A connection means the extension's witness host is alive; the
/// read returning EOF means the host process exited — which the browser does
/// when the extension is disabled, removed, or the browser closes.
///
/// Two-way since #489: each line the host forwards from the extension is raised
/// as <see cref="MessageReceived"/> (its hello and keepalive pings included),
/// and <see cref="TrySend"/> queues lines for the extension, which a writer task
/// per connection drains in order. A line queued while nobody is connected is
/// dropped: the session relay hands the current session down on every connect.
///
/// Not unit-tested — it's thin OS plumbing, like <c>StatusEndpoint</c>; the
/// connect→disconnect→report logic it drives lives in the unit-tested monitor
/// and relay, and the real pipe is driven by <c>WitnessHostIntegrationTests</c>
/// and the agent e2e.
/// </summary>
public sealed class NamedPipeWitnessTransport : IExtensionWitnessChannel
{
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(2);

    private readonly string _pipeName;
    private readonly ILogger<NamedPipeWitnessTransport> _log;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    // The open connection's outbound queue, or null between connections.
    private Channel<string>? _outbound;

    public NamedPipeWitnessTransport(ILogger<NamedPipeWitnessTransport>? log = null, string? pipeName = null)
    {
        _log = log ?? NullLogger<NamedPipeWitnessTransport>.Instance;
        _pipeName = pipeName ?? WitnessLink.PipeName;
    }

    public event EventHandler? WitnessConnected;
    public event EventHandler? WitnessDisconnected;
    public event EventHandler<string>? MessageReceived;

    public bool TrySend(string line)
    {
        var outbound = Volatile.Read(ref _outbound);
        return outbound is not null && outbound.Writer.TryWrite(line);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log.LogInformation("Witness pipe server listening on \\\\.\\pipe\\{PipeName}", _pipeName);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            catch (IOException ex)
            {
                // Another instance already holds the name (e.g. a stale agent).
                _log.LogWarning(ex, "Witness pipe unavailable; retrying.");
                await DelayQuietly(BusyRetryDelay, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Witness pipe accept failed.");
                await server.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            var outbound = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var writer = Task.Run(() => WriteLinesAsync(server, outbound.Reader, connectionCts.Token), CancellationToken.None);
            Volatile.Write(ref _outbound, outbound);

            WitnessConnected?.Invoke(this, EventArgs.Empty);
            try
            {
                await ReadLinesUntilEofAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down — not a tamper
            }
            catch (Exception ex)
            {
                // A broken pipe is still the host going away; fall through to report.
                _log.LogDebug(ex, "Witness pipe read ended.");
            }
            finally
            {
                Interlocked.CompareExchange(ref _outbound, null, outbound);
                outbound.Writer.TryComplete();
                connectionCts.Cancel();
                try { await writer.ConfigureAwait(false); } catch { }
                await server.DisposeAsync().ConfigureAwait(false);
            }

            if (!ct.IsCancellationRequested)
                WitnessDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ReadLinesUntilEofAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return; // host disconnected → EOF
            if (line.Length == 0) continue;
            try
            {
                MessageReceived?.Invoke(this, line);
            }
            catch (Exception ex)
            {
                // A subscriber's failure must not end the link: that would read
                // as the extension being switched off.
                _log.LogWarning(ex, "A witness message handler threw.");
            }
        }
    }

    private async Task WriteLinesAsync(Stream stream, ChannelReader<string> lines, CancellationToken ct)
    {
        try
        {
            await foreach (var line in lines.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // The host went away mid-write; the read loop sees the EOF and
            // reports the drop.
            _log.LogDebug(ex, "Witness pipe write ended.");
        }
    }

    private static async Task DelayQuietly(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _cts?.Dispose();
        _cts = null;
    }
}
