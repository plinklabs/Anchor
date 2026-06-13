using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Tamper;

/// <summary>
/// Serves the local named pipe the browser-launched native host connects to,
/// and surfaces its connect/disconnect to <see cref="ExtensionWitnessMonitor"/>
/// (#146 part 1). A connection means the extension's witness host is alive; the
/// read returning EOF means the host process exited — which the browser does
/// when the extension is disabled, removed, or the browser closes. Read-only:
/// the host writes a hello and keepalive pings (drained and discarded here),
/// the agent never writes back.
///
/// Not unit-tested — it's thin OS plumbing, like <c>StatusEndpoint</c>; the
/// connect→disconnect→report logic it drives lives in the unit-tested monitor,
/// and the end-to-end behaviour is covered by the verify run.
/// </summary>
public sealed class NamedPipeWitnessTransport : IExtensionWitnessTransport
{
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(2);

    private readonly string _pipeName;
    private readonly ILogger<NamedPipeWitnessTransport> _log;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public NamedPipeWitnessTransport(ILogger<NamedPipeWitnessTransport>? log = null, string? pipeName = null)
    {
        _log = log ?? NullLogger<NamedPipeWitnessTransport>.Instance;
        _pipeName = pipeName ?? WitnessLink.PipeName;
    }

    public event EventHandler? WitnessConnected;
    public event EventHandler? WitnessDisconnected;

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
                    _pipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
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

            WitnessConnected?.Invoke(this, EventArgs.Empty);
            try
            {
                await DrainUntilEofAsync(server, ct).ConfigureAwait(false);
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
                await server.DisposeAsync().ConfigureAwait(false);
            }

            if (!ct.IsCancellationRequested)
                WitnessDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private static async Task DrainUntilEofAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[256];
        while (!ct.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) return; // host disconnected → EOF
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
