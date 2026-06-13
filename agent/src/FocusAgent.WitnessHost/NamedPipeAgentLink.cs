using System.IO.Pipes;
using System.Text;

namespace FocusAgent.WitnessHost;

/// <summary>
/// Connects to the agent's named pipe and keeps the connection alive, raising
/// <see cref="Connected"/>/<see cref="Disconnected"/> as it comes and goes
/// (#146 part 1). Write-only from the host's side: the host forwards the
/// extension's pings and emits its own keepalives so it can notice the agent
/// dying (the next write throws) without needing a read channel.
/// </summary>
public sealed class NamedPipeAgentLink : IAgentLink, IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private NamedPipeClientStream? _pipe;

    public NamedPipeAgentLink(string pipeName)
    {
        _pipeName = pipeName;
    }

    public event Action? Connected;
    public event Action? Disconnected;

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => ConnectLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(ConnectTimeout);
                await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Agent not running yet — back off and retry.
                await pipe.DisposeAsync().ConfigureAwait(false);
                await DelayQuietly(RetryDelay, ct).ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }

            _pipe = pipe;
            Connected?.Invoke();
            try
            {
                await WriteLineAsync("{\"type\":\"hello\"}", ct).ConfigureAwait(false);
                // Keepalives let us notice the agent dying: the write throws when
                // the pipe breaks, which drops us into the reconnect path below.
                while (!ct.IsCancellationRequested)
                {
                    await DelayQuietly(KeepaliveInterval, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) break;
                    await WriteLineAsync("{\"type\":\"ping\"}", ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception)
            {
                // Pipe broke — the agent stopped. Fall through to signal + retry.
            }
            finally
            {
                _pipe = null;
                await pipe.DisposeAsync().ConfigureAwait(false);
            }

            if (!ct.IsCancellationRequested)
            {
                Disconnected?.Invoke();
                await DelayQuietly(RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    public Task SendAsync(string line, CancellationToken ct = default) => WriteLineAsync(line, ct, bestEffort: true);

    private async Task WriteLineAsync(string line, CancellationToken ct, bool bestEffort = false)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected)
        {
            if (bestEffort) return;
            throw new IOException("Agent pipe not connected.");
        }

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
        }
        catch when (bestEffort)
        {
            // Drop the forwarded ping; the keepalive loop owns drop detection.
        }
        finally
        {
            _writeGate.Release();
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

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _writeGate.Dispose();
    }
}
