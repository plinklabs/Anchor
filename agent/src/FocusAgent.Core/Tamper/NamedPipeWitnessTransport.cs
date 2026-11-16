using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Tamper;

/// <summary>
/// Serves the local named pipe the browser-launched native hosts connect to,
/// and surfaces their connect/disconnect to <see cref="ExtensionWitnessMonitor"/>
/// (#146 part 1). A connection means an extension's witness host is alive; the
/// read returning EOF means the host process exited — which the browser does
/// when the extension is disabled, removed, or the profile or browser closes.
///
/// Serves any number of hosts at once (#517): every Edge profile runs its own
/// copy of the extension with its own host, and each must be handed the
/// session. The accept loop always keeps one pipe instance listening and hands
/// each accepted connection to a task of its own, with its own outbound queue.
///
/// Two-way since #489: each line a host forwards from its extension is raised
/// as <see cref="MessageReceived"/> (its hello and keepalive pings included),
/// and <see cref="TrySend"/> / <see cref="Broadcast"/> queue lines for one or
/// all extensions, which a writer task per connection drains in order. A line
/// queued while nobody is connected is dropped: the session relay hands the
/// current session down on every connect.
///
/// Not unit-tested — it's thin OS plumbing, like <c>StatusEndpoint</c>; the
/// logic it drives lives in the unit-tested monitor, guard and relay, and the
/// real pipe is driven by <c>WitnessHostIntegrationTests</c>
/// and the agent e2e.
/// </summary>
public sealed class NamedPipeWitnessTransport : IExtensionWitnessChannel
{
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(2);

    private readonly string _pipeName;
    private readonly ILogger<NamedPipeWitnessTransport> _log;
    // The open connections, by id: what TrySend and Broadcast can reach.
    private readonly ConcurrentDictionary<long, Link> _links = new();
    // Every connection task still running, so StopAsync can wait for them.
    private readonly ConcurrentDictionary<long, Task> _serving = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lastId;
    // Pipe instances this transport holds open (listening or connected). While
    // it is zero the next instance is created as the pipe's first, which fails
    // if another process already serves the name.
    private int _instances;

    public NamedPipeWitnessTransport(ILogger<NamedPipeWitnessTransport>? log = null, string? pipeName = null)
    {
        _log = log ?? NullLogger<NamedPipeWitnessTransport>.Instance;
        _pipeName = pipeName ?? WitnessLink.PipeName;
    }

    public event EventHandler<WitnessConnection>? WitnessConnected;
    public event EventHandler<WitnessConnection>? WitnessDisconnected;
    public event EventHandler<WitnessMessage>? MessageReceived;

    public bool TrySend(WitnessConnection connection, string line) =>
        _links.TryGetValue(connection.Id, out var link) && link.Outbound.Writer.TryWrite(line);

    public int Broadcast(string line)
    {
        var sent = 0;
        foreach (var link in _links.Values)
        {
            if (link.Outbound.Writer.TryWrite(line)) sent++;
        }
        return sent;
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
                // Only this agent may serve the name: without FirstPipeInstance a
                // second process could add instances to the same pipe and take
                // some of the hosts. Later instances join our own first one.
                var options = PipeOptions.Asynchronous;
                if (Volatile.Read(ref _instances) == 0) options |= PipeOptions.FirstPipeInstance;
                server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, options);
                Interlocked.Increment(ref _instances);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another process already holds the name (e.g. a stale agent).
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
                await CloseAsync(server).ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Witness pipe accept failed.");
                await CloseAsync(server).ConfigureAwait(false);
                continue;
            }

            // Serve it on its own task and go straight back to listening, so the
            // next profile's host finds an instance to connect to.
            var link = new Link(
                new WitnessConnection(Interlocked.Increment(ref _lastId)),
                Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true }));
            var id = link.Connection.Id;
            var serving = Task.Run(() => ServeAsync(server, link, ct), CancellationToken.None);
            _serving[id] = serving;
            _ = serving.ContinueWith(
                _ => _serving.TryRemove(id, out Task? _),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        // Stopping: the connections watch the same token; wait for them to close.
        foreach (var serving in _serving.Values)
        {
            try { await serving.ConfigureAwait(false); } catch { }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server, Link link, CancellationToken ct)
    {
        var connection = link.Connection;
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writer = Task.Run(() => WriteLinesAsync(server, link.Outbound.Reader, connectionCts.Token), CancellationToken.None);
        // Reachable by Broadcast before WitnessConnected is raised, so nothing
        // broadcast after the event can miss this connection.
        _links[connection.Id] = link;

        Raise(WitnessConnected, connection, nameof(WitnessConnected));
        try
        {
            await ReadLinesUntilEofAsync(server, connection, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down — not a tamper
        }
        catch (Exception ex)
        {
            // A broken pipe is still the host going away; fall through to report.
            _log.LogDebug(ex, "Witness pipe read ended (connection {Connection}).", connection);
        }
        finally
        {
            _links.TryRemove(connection.Id, out _);
            link.Outbound.Writer.TryComplete();
            connectionCts.Cancel();
            try { await writer.ConfigureAwait(false); } catch { }
            await CloseAsync(server).ConfigureAwait(false);
        }

        if (!ct.IsCancellationRequested)
            Raise(WitnessDisconnected, connection, nameof(WitnessDisconnected));
    }

    private async Task CloseAsync(NamedPipeServerStream server)
    {
        await server.DisposeAsync().ConfigureAwait(false);
        Interlocked.Decrement(ref _instances);
    }

    private void Raise(EventHandler<WitnessConnection>? handlers, WitnessConnection connection, string name)
    {
        try
        {
            handlers?.Invoke(this, connection);
        }
        catch (Exception ex)
        {
            // A subscriber's failure must not take the connection's task down.
            _log.LogWarning(ex, "A {Event} handler threw (connection {Connection}).", name, connection);
        }
    }

    private async Task ReadLinesUntilEofAsync(Stream stream, WitnessConnection connection, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return; // host disconnected → EOF
            if (line.Length == 0) continue;
            try
            {
                MessageReceived?.Invoke(this, new WitnessMessage(connection, line));
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

    private sealed record Link(WitnessConnection Connection, Channel<string> Outbound);
}
