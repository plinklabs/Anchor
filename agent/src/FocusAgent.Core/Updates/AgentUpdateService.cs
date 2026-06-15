using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Updates;

/// <summary>
/// Drives the agent's auto-update cadence (#224): one check shortly after startup,
/// then a re-check every <see cref="UpdateSettings.CheckInterval"/>. On a positive
/// check it downloads the delta and stages it for the <em>next</em> restart — it
/// never relaunches the agent out from under a student, so single-instance gating
/// and an in-session student are left undisturbed.
///
/// <para>
/// Everything here is best-effort: a thrown check/download (network down, feed
/// missing) is logged and swallowed so a flaky update path can never crash or
/// block the agent. The whole loop no-ops when the agent isn't a real Velopack
/// install (<see cref="IAgentUpdateManager.IsInstalled"/>) — a <c>dotnet run</c>
/// / self-test / MSIX build has nothing to update against.
/// </para>
/// </summary>
public sealed class AgentUpdateService : IAsyncDisposable
{
    /// <summary>
    /// Lower bound on the configured interval so a mis-set config (e.g. 0) can't
    /// turn the check into a busy-loop hammering GitHub.
    /// </summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    private readonly IAgentUpdateManager _manager;
    private readonly UpdateSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<AgentUpdateService> _log;
    private readonly object _gate = new();

    private ITimer? _timer;
    private bool _started;
    private bool _disposed;
    // Single-flight guard: a long check must not overlap the next interval tick.
    private int _checkInFlight;

    public AgentUpdateService(
        IAgentUpdateManager manager,
        IOptions<UpdateSettings> settings,
        TimeProvider? clock = null,
        ILogger<AgentUpdateService>? log = null)
    {
        _manager = manager;
        _settings = settings.Value;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<AgentUpdateService>.Instance;
    }

    /// <summary>Set after a check stages an update, so a UI/diagnostic surface
    /// can report "restart to finish updating to vX". Latches true.</summary>
    public bool UpdatePendingRestart { get; private set; }

    /// <summary>The version staged to apply on next restart, or null.</summary>
    public string? PendingVersion { get; private set; }

    /// <summary>
    /// Kick off the cadence: an immediate startup check, then a periodic re-check.
    /// Idempotent and safe to call from app startup. No-op (and logs why) when
    /// disabled in config or when the agent isn't a Velopack install.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;

            if (!_settings.Enabled)
            {
                _log.LogInformation("Auto-update disabled by config (Update:Enabled=false); not scheduling checks.");
                return;
            }
            if (!_manager.IsInstalled)
            {
                _log.LogInformation(
                    "Agent is not a Velopack install (dev run / MSIX / self-test); skipping auto-update checks.");
                return;
            }

            _started = true;

            var interval = _settings.CheckInterval < MinimumInterval ? MinimumInterval : _settings.CheckInterval;
            // First tick fires ~immediately (startup check); the timer then repeats
            // on the interval. A single timer covers both the startup and interval
            // cadences the issue asks for.
            _timer = _clock.CreateTimer(
                _ => OnTimerFired(),
                state: null,
                dueTime: TimeSpan.Zero,
                period: interval);

            _log.LogInformation(
                "Auto-update scheduled: startup check now, re-check every {IntervalMinutes} min (feed {Repo}).",
                (int)interval.TotalMinutes, _settings.GithubRepoUrl);
        }
    }

    private void OnTimerFired() => _ = CheckOnceAsync(CancellationToken.None);

    /// <summary>
    /// Run a single check → download → stage pass. Public so the startup path (and
    /// tests) can drive one cycle deterministically without waiting on the timer.
    /// Single-flighted: a tick that arrives while a prior check is still running is
    /// dropped rather than queued.
    /// </summary>
    public async Task CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _checkInFlight, 1, 0) != 0)
        {
            _log.LogDebug("Auto-update check already in flight; skipping this tick.");
            return;
        }

        try
        {
            // Defensive: respect a flag/install state that changed after Start().
            if (!_settings.Enabled || !_manager.IsInstalled)
                return;

            var result = await _manager.CheckForUpdateAsync(cancellationToken).ConfigureAwait(false);
            if (!result.IsUpdateAvailable)
            {
                _log.LogDebug("Auto-update check: agent is up to date.");
                return;
            }

            _log.LogInformation("Auto-update available: {Version}. Downloading delta…", result.TargetVersion);
            await _manager.DownloadUpdateAsync(result, cancellationToken).ConfigureAwait(false);

            _manager.StageUpdateForNextRestart(result);
            UpdatePendingRestart = true;
            PendingVersion = result.TargetVersion;
            _log.LogInformation(
                "Auto-update {Version} downloaded and staged; it applies on the next agent restart.",
                result.TargetVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown — not an error.
        }
        catch (Exception ex)
        {
            // Best-effort: a failed check/download must never crash or block the
            // agent. The student stays on the current version and we try again next
            // interval.
            _log.LogWarning(ex, "Auto-update check failed; staying on the current version.");
        }
        finally
        {
            Interlocked.Exchange(ref _checkInFlight, 0);
        }
    }

    public ValueTask DisposeAsync()
    {
        ITimer? toDispose;
        lock (_gate)
        {
            _disposed = true;
            toDispose = _timer;
            _timer = null;
        }
        toDispose?.Dispose();
        return ValueTask.CompletedTask;
    }
}
