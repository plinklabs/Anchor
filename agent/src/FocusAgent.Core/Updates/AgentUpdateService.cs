using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Updates;

/// <summary>
/// Drives the agent's auto-update cadence (#224): one check shortly after startup,
/// then a re-check every <see cref="UpdateSettings.CheckInterval"/>. On a positive
/// check it only downloads the package. It never starts the updater itself (#449:
/// Update.exe gives a running agent 60 s to exit and then kills it). The package is
/// applied by the next agent start (Velopack's auto-apply in Program.Main) or, when
/// the agent has no session going on, by <see cref="IdleUpdateRestarter"/>, which
/// restarts the agent into it (#456); <see cref="UpdateReady"/> tells it when.
///
/// <para>
/// The interval timer counts awake time only: a .NET timer on Windows runs on
/// unbiased interrupt time, which leaves out sleep and hibernation, so a laptop that
/// is open two hours a day would check every three days. The agent therefore also
/// checks on wake and unlock, once the last check is
/// <see cref="UpdateSettings.CheckInterval"/> old by the clock
/// (<see cref="CheckIfDueAsync"/>, #456).
/// </para>
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
    private TimeSpan _interval;
    private bool _started;
    private bool _disposed;

    // #456: passes are numbered as they start, so a version whose apply failed can
    // be held back until a later pass than the one that found it. All under _gate.
    private int _passNumber;
    private DateTimeOffset? _lastCheckStartedAt;
    private string? _heldBackVersion;
    private int _heldBackThroughPass;
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
    /// Whether Velopack's updater started this process (#456): it does so after an
    /// apply, with the new version when the apply worked and with the old one when
    /// it failed. Read from Velopack's <c>VELOPACK_RESTART</c> marker in Program.Main.
    /// Set before <see cref="Start"/>: if an update is still waiting then, applying it
    /// failed, and that version is held back through the startup pass so the agent
    /// doesn't restart into the same failure again and again.
    /// </summary>
    public bool RestartedByUpdater { get; init; }

    /// <summary>
    /// Raised (on the thread pool) when a pass leaves a downloaded update ready to
    /// apply: <see cref="ReadyToApplyVersion"/> is then non-null (#456).
    /// </summary>
    public event EventHandler? UpdateReady;

    /// <summary>
    /// The downloaded version the agent may restart into now (#456): the pending
    /// version, unless applying it failed and no later pass has found it again.
    /// </summary>
    public string? ReadyToApplyVersion
    {
        get
        {
            lock (_gate)
                return PendingVersion is { } pending && pending != _heldBackVersion ? pending : null;
        }
    }

    /// <summary>When the last check pass started, by the clock; null before the first.</summary>
    public DateTimeOffset? LastCheckStartedAt
    {
        get { lock (_gate) return _lastCheckStartedAt; }
    }

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

            if (RestartedByUpdater)
                HoldBackFailedApply();

            _interval = _settings.CheckInterval < MinimumInterval ? MinimumInterval : _settings.CheckInterval;
            // First tick fires ~immediately (startup check); the timer then repeats
            // on the interval. A single timer covers both the startup and interval
            // cadences the issue asks for.
            _timer = _clock.CreateTimer(
                _ => OnTimerFired(),
                state: null,
                dueTime: TimeSpan.Zero,
                period: _interval);

            _log.LogInformation(
                "Auto-update scheduled: startup check now, re-check every {IntervalMinutes} min (feed {Repo}).",
                (int)_interval.TotalMinutes, _settings.GithubRepoUrl);
        }
    }

    // Under _gate, from Start. Velopack's updater restarted this agent and an update
    // is still waiting: the updater starts the old version again when its apply
    // fails, and after a working apply nothing newer than the installed version is
    // left. Hold that version back through the startup pass (the pass about to
    // start), so the next try comes a full check later, not right away.
    private void HoldBackFailedApply()
    {
        string? waiting;
        try
        {
            waiting = _manager.GetPendingRestartVersion();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read which update is waiting after Velopack's updater restarted the agent.");
            return;
        }
        if (waiting is null) return;

        _heldBackVersion = waiting;
        _heldBackThroughPass = _passNumber + 1;
        _log.LogWarning(
            "Velopack's updater restarted the agent on its current version with update {Version} still waiting: " +
            "applying it failed. Not restarting into it again before the next update check.",
            waiting);
    }

    /// <summary>
    /// The wake/unlock check (#456): run a pass now if the last one started at least
    /// <see cref="UpdateSettings.CheckInterval"/> ago by the clock. The interval
    /// timer leaves out time asleep, so after a night with the lid closed it is
    /// nowhere near due while the clock says the last check is many hours old. A
    /// pass that runs here also restarts the interval, so the next periodic check
    /// counts from it. No-op before <see cref="Start"/> (or when it no-oped), and
    /// single-flighted like every pass.
    /// </summary>
    public Task CheckIfDueAsync(string reason, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_started || _disposed) return Task.CompletedTask;

            var now = _clock.GetUtcNow();
            if (_lastCheckStartedAt is { } last && now - last < _interval)
            {
                _log.LogDebug(
                    "Agent {Reason}; the last update check was {Minutes:N0} min ago, not checking again yet.",
                    reason, (now - last).TotalMinutes);
                return Task.CompletedTask;
            }

            _log.LogInformation(
                "Agent {Reason}; the last update check was {Ago} ago, checking now.",
                reason, _lastCheckStartedAt is { } l ? $"{(now - l).TotalHours:N1} h" : "never");
            _timer?.Change(_interval, _interval);
        }
        return RunCheck(joinInFlight: false, cancellationToken);
    }

    /// <summary>
    /// The restart into <paramref name="version"/> could not be handed over (#456):
    /// hold that version back until a pass after the current one finds it again, so
    /// a failing hand-over is retried once per check, not in a loop.
    /// </summary>
    public void ReportApplyFailed(string version)
    {
        lock (_gate)
        {
            _heldBackVersion = version;
            _heldBackThroughPass = _passNumber;
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
            var pass = ++_passNumber;
            _current = Task.Run(() => CheckCoreAsync(pass, cancellationToken), CancellationToken.None);
            return _current;
        }
    }

    private async Task CheckCoreAsync(int pass, CancellationToken cancellationToken)
    {
        try
        {
            // Defensive: respect a flag/install state that changed after Start().
            if (!_settings.Enabled || !_manager.IsInstalled)
                return;

            lock (_gate) _lastCheckStartedAt = _clock.GetUtcNow();

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
            // folder for the next agent start, or the idle restart (#456), to apply.
            // Read back what that will apply rather than assume the download put it
            // where it looks.
            var pending = _manager.GetPendingRestartVersion();
            if (pending is null)
            {
                _log.LogWarning(
                    "Auto-update {Version} downloaded, but no newer package is waiting for the next agent start.",
                    result.TargetVersion);
                return;
            }

            bool ready;
            lock (_gate)
            {
                UpdatePendingRestart = true;
                PendingVersion = pending;
                if (_heldBackVersion is not null && pass > _heldBackThroughPass)
                    _heldBackVersion = null;
                ready = pending != _heldBackVersion;
            }

            if (!ready)
            {
                _log.LogWarning(
                    "Auto-update {Version} downloaded, but applying it failed earlier; it waits for the next check " +
                    "or the next agent start.",
                    pending);
                return;
            }

            _log.LogInformation(
                "Auto-update {Version} downloaded; it applies when the agent restarts or next starts.",
                pending);
            RaiseUpdateReady();
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

    private void RaiseUpdateReady()
    {
        try
        {
            UpdateReady?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A subscriber's failure is not a failed check.
            _log.LogWarning(ex, "An auto-update ready handler failed.");
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
