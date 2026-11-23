using System.IO.Pipes;
using System.Text;

namespace FocusAgent.WitnessHost;

/// <summary>
/// Connects to the agent's named pipe and keeps the connection alive, raising
/// <see cref="Connected"/>/<see cref="Disconnected"/> as it comes and goes
/// (#146 part 1). The host forwards the extension's messages to the agent and
/// emits its own keepalives, so it notices the agent dying (the next write
/// throws); since #489 it also reads the agent's lines — the session it hands the
/// extension — and raises each as <see cref="MessageReceived"/>. Whichever side
/// of the connection ends first (a failed write, or the agent closing its end)
/// counts as the agent going away.
/// </summary>
public sealed class NamedPipeAgentLink : IAgentLink, IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(5);
    // Messages the extension sent before the pipe was up, kept for when it is.
    private const int MaxPending = 64;
    // Failed connect attempts logged one by one (#561); after that one line a
    // minute (every RetryDelay), so an agent that stays away for hours doesn't
    // fill the log.
    private const int AttemptsLoggedEach = 5;
    private const int AttemptsLoggedEvery = 30;

    private readonly string _pipeName;
    private readonly WitnessHostLog _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private NamedPipeClientStream? _pipe;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pending = new();

    /// <param name="log">Where each connect attempt and drop is written (#561); none by default.</param>
    public NamedPipeAgentLink(string pipeName, WitnessHostLog? log = null)
    {
        _pipeName = pipeName;
        _log = log ?? WitnessHostLog.None;
    }

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<string>? MessageReceived;

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => ConnectLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(ConnectTimeout);
                await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Agent not running yet — back off and retry.
                if (attempt <= AttemptsLoggedEach || attempt % AttemptsLoggedEvery == 0)
                {
                    // A connect that timed out is cancelled, which says nothing:
                    // no agent is listening on the pipe.
                    var why = ex is OperationCanceledException
                        ? $"no answer within {ConnectTimeout.TotalSeconds:0}s (no agent listening on it, or none free)"
                        : WitnessHostLog.Describe(ex);
                    _log.Warn($"Connecting to the agent's pipe {_pipeName} failed (attempt {attempt}): {why}; retrying every {RetryDelay.TotalSeconds:0}s.");
                }
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
            _log.Info($"Connected to the agent's pipe {_pipeName} (attempt {attempt}).");
            attempt = 0;
            Connected?.Invoke();
            using (var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var reading = ReadLinesAsync(pipe, linkCts.Token);
                var keeping = KeepAliveAsync(linkCts.Token);
                try
                {
                    // Either side ending means the agent is gone: a keepalive write
                    // threw (the pipe broke), or the agent closed its end (EOF).
                    var ended = await Task.WhenAny(reading, keeping).ConfigureAwait(false);
                    if (!ct.IsCancellationRequested)
                        _log.Warn($"The agent's pipe dropped: {DropReason(ended, ended == reading)}.");
                }
                finally
                {
                    linkCts.Cancel();
                    try { await Task.WhenAll(reading, keeping).ConfigureAwait(false); } catch { }
                    _pipe = null;
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (!ct.IsCancellationRequested)
            {
                Disconnected?.Invoke();
                await DelayQuietly(RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private static string DropReason(Task ended, bool reading)
    {
        if (ended.Exception?.GetBaseException() is { } ex)
            return (reading ? "reading from it failed: " : "writing to it failed: ") + WitnessHostLog.Describe(ex);
        return reading ? "the agent closed its end" : "the keepalive stopped";
    }

    private async Task KeepAliveAsync(CancellationToken ct)
    {
        await WriteLineAsync("{\"type\":\"hello\"}", ct).ConfigureAwait(false);
        // What the extension sent while the pipe was still coming up: a service
        // worker woken by a navigation reports blocking it before the host has
        // reached the agent.
        while (_pending.TryDequeue(out var pending))
            await WriteLineAsync(pending, ct).ConfigureAwait(false);
        // Keepalives let us notice the agent dying: the write throws when the
        // pipe breaks, which ends this task and the connection with it.
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(KeepaliveInterval, ct).ConfigureAwait(false);
            await WriteLineAsync("{\"type\":\"ping\"}", ct).ConfigureAwait(false);
        }
    }

    private async Task ReadLinesAsync(Stream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return; // the agent closed its end
            if (line.Length > 0)
                MessageReceived?.Invoke(line);
        }
    }

    public Task SendAsync(string line, CancellationToken ct = default)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected)
        {
            // Not connected (yet): keep it for the next connection, the newest
            // MaxPending only. The agent drops one about a session that is over.
            _pending.Enqueue(line);
            while (_pending.Count > MaxPending && _pending.TryDequeue(out _)) { }
            return Task.CompletedTask;
        }
        return WriteLineAsync(line, ct, bestEffort: true);
    }

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
            // Drop the forwarded message; the keepalive loop owns drop detection.
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
