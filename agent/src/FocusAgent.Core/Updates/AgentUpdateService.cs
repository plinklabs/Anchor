using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Updates;

/// <summary>
/// Drives the agent's auto-update cadence (#224): one check shortly after startup,
/// then a re-check every <see cref="UpdateSettings.CheckInterval"/>. On a positive
/// check it only downloads the package; the <em>next</em> agent start applies it
/// (Velopack's auto-apply in Program.Main). It never starts the updater while the
/// agent runs (#449: Update.exe would give the agent 60 s to exit and then kill it,
/// leaving the student without enforcement until the next login), so single-instance
/// gating and an in-session student are left undisturbed.
///
/// <para>
/// It is also the agent's repair channel for a release that crashes (#407): the app
/// starts it before creating any window, and every fatal path runs (or joins) one
/// more pass through <see cref="CheckNowAsync"/> before the process goes away.
/// </para>
///
/// <para>
/// Everything here is best-effort: a thrown check/download (network down, feed
/// missing) is logged and swallowed so a flaky update path can never crash or
/// block the agent. The whole loop no-ops when the agent isn't a real Velopack
/// install (<see cref="IAgentUpdateManager.IsInstalled"/>) — a <c>dotnet run</c>
/// or self-test launch has nothing to update against.
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
    // The current (or last) check pass, guarded by _gate. Single-flight: a long
    // check must not overlap the next interval tick, and a fatal path (#407) joins
    // the one in flight instead of starting a second.
    private Task _current = Task.CompletedTask;

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

    /// <summary>Set once a check has downloaded an update that the next agent start
    /// will apply (as Velopack itself reports it), so a UI/diagnostic surface can
    /// report "restart to finish updating to vX". Latches true.</summary>
    public bool UpdatePendingRestart { get; private set; }

    /// <summary>The downloaded version the next agent start applies, or null.</summary>
    public string? PendingVersion { get; private set; }

    /// <summary>
    /// Test seam (#435): the check pass in flight, or the last one once it has
    /// finished (<see cref="Task.CompletedTask"/> before any pass). It never faults.
    /// Lets tests await a timer-fired pass instead of sleeping for the thread pool.
    /// </summary>
    internal Task InFlight
    {
        get { lock (_gate) return _current; }
    }

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
                    "Agent is not a Velopack install (dev run / self-test); skipping auto-update checks.");
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
    /// Run a single check → download pass. Public so tests can drive one
    /// cycle deterministically without waiting on the timer. Single-flighted: a
    /// call that arrives while a prior check is still running is dropped rather
    /// than queued, and returns at once.
    /// </summary>
    public Task CheckOnceAsync(CancellationToken cancellationToken = default) =>
        RunCheck(joinInFlight: false, cancellationToken);

    /// <summary>
    /// The fatal-path check (#407): make sure one check → download pass runs now,
    /// and return a task that completes when it has, so the fixed release is on disk
    /// for the next launch to apply before the process goes. If a pass is already in
    /// flight (typically the startup check, still running when startup crashed) the
    /// caller joins it rather than starting a second; otherwise a fresh pass starts.
    /// Works whether or not <see cref="Start"/> ran, and keeps the same gating (a
    /// disabled or non-Velopack agent completes at once). The returned task never
    /// faults, so a crash path can wait on it without a try/catch of its own.
    /// </summary>
    public Task CheckNowAsync(CancellationToken cancellationToken = default) =>
        RunCheck(joinInFlight: true, cancellationToken);

    private Task RunCheck(bool joinInFlight, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_current.IsCompleted)
            {
                if (joinInFlight) return _current;
                _log.LogDebug("Auto-update check already in flight; skipping this tick.");
                return Task.CompletedTask;
            }

            // Always on the thread pool, never on the caller's thread: a fatal path
            // blocks the UI thread on this task, so no part of the pass may need it.
            _current = Task.Run(() => CheckCoreAsync(cancellationToken), CancellationToken.None);
            return _current;
        }
    }

    private async Task CheckCoreAsync(CancellationToken cancellationToken)
    {
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

            // #449: downloading is the whole job. Nothing here may start the updater,
            // which would kill this running agent; the package waits in the packages
            // folder for the next agent start to apply. Read back what that start
            // will apply rather than assume the download put it where it looks.
            var pending = _manager.GetPendingRestartVersion();
            if (pending is null)
            {
                _log.LogWarning(
                    "Auto-update {Version} downloaded, but no newer package is waiting for the next agent start.",
                    result.TargetVersion);
                return;
            }

            UpdatePendingRestart = true;
            PendingVersion = pending;
            _log.LogInformation(
                "Auto-update {Version} downloaded; it applies the next time the agent starts.",
                pending);
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
