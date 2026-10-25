using FocusAgent.Core.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Updates;

/// <summary>
/// The app's half of a restart into an update (#456): hand the downloaded package to
/// Velopack's updater with a restart, then end this process on its own, well inside
/// the 60 s the updater waits before it kills the agent (#449).
/// </summary>
public interface IAgentRestarter
{
    /// <summary>
    /// Start the updater on <paramref name="version"/> and shut the agent down so the
    /// updater can apply it and start the agent again. Returns true once the updater
    /// is started and the exit is under way; false, with the agent left running as it
    /// was, when the updater could not be started.
    /// </summary>
    bool TryRestartIntoUpdate(string version);
}

/// <summary>
/// Restarts the agent into a downloaded update as soon as it has no session going on
/// (#456), instead of leaving the update for the next sign-in, which for a laptop
/// that is only ever closed and opened can be weeks away.
///
/// <para>
/// It acts when <see cref="AgentUpdateService"/> reports an update ready to apply,
/// when a session ends, and once a minute while an update waits. Each time it asks
/// the <see cref="SessionCoordinator"/> to stop taking sessions, which only succeeds
/// with no session joined, none being asked about and no join on its way, and is one
/// step with every session change, so a restart during a session or a pending join
/// cannot happen. A session the teacher starts while the agent restarts is picked up
/// by the restarted agent's catch-up (#356). The extra <c>isBusy</c> check covers a
/// join the coordinator doesn't see yet: the join-by-code dialog, which a restart
/// would close under a student typing a code.
/// </para>
///
/// <para>
/// No loops: when the hand-over fails, sessions are taken again, the version is held
/// back until the next check pass (<see cref="AgentUpdateService.ReportApplyFailed"/>),
/// and the agent stays on its current version. When the updater's apply itself fails,
/// the updater starts the old version again, and <see cref="AgentUpdateService"/>
/// holds that version back the same way (<see cref="AgentUpdateService.RestartedByUpdater"/>).
/// </para>
/// </summary>
public sealed class IdleUpdateRestarter : IDisposable
{
    /// <summary>How often a waiting update is tried again while a restart is blocked.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    private readonly AgentUpdateService _updates;
    private readonly SessionCoordinator _sessions;
    private readonly IAgentRestarter _restarter;
    private readonly Func<bool> _isBusy;
    private readonly TimeProvider _clock;
    private readonly ILogger<IdleUpdateRestarter> _log;
    private readonly object _gate = new();

    private ITimer? _retry;
    private bool _started;
    private bool _stopped;
    private bool _restarting;
    private string? _waitingLogged;

    public IdleUpdateRestarter(
        AgentUpdateService updates,
        SessionCoordinator sessions,
        IAgentRestarter restarter,
        Func<bool>? isBusy = null,
        TimeProvider? clock = null,
        ILogger<IdleUpdateRestarter>? log = null)
    {
        _updates = updates;
        _sessions = sessions;
        _restarter = restarter;
        _isBusy = isBusy ?? (() => false);
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<IdleUpdateRestarter>.Instance;
    }

    /// <summary>
    /// Start watching. Tries once right away too, for an update a pass left ready
    /// before this was started. Idempotent; no-op after <see cref="Stop"/>.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _stopped) return;
            _started = true;
        }
        _updates.UpdateReady += OnUpdateReady;
        _sessions.SessionLeft += OnSessionLeft;
        _ = Task.Run(() => TryRestart("an update was already waiting"));
    }

    /// <summary>
    /// Never restart from now on. The fatal paths (#407) call this: a crashing agent
    /// keeps its own way of leaving the fix for the next start.
    /// </summary>
    public void Stop()
    {
        ITimer? retry;
        lock (_gate)
        {
            _stopped = true;
            retry = _retry;
            _retry = null;
        }
        retry?.Dispose();
        _updates.UpdateReady -= OnUpdateReady;
        _sessions.SessionLeft -= OnSessionLeft;
    }

    public void Dispose() => Stop();

    // Off the raising thread: SessionLeft's other subscribers (enforcement, the
    // heartbeat) finish their part of the session end first.
    private void OnUpdateReady(object? sender, EventArgs e) =>
        _ = Task.Run(() => TryRestart("an update was downloaded"));

    private void OnSessionLeft(object? sender, Guid sessionId) =>
        _ = Task.Run(() => TryRestart("a session ended"));

    /// <summary>
    /// Restart into the ready update now if the agent is idle; otherwise keep it
    /// waiting and try again later. Returns true when the restart was handed over.
    /// Internal so tests can drive one decision deterministically.
    /// </summary>
    internal bool TryRestart(string trigger)
    {
        string version;
        lock (_gate)
        {
            if (_stopped || _restarting) return false;

            if (_updates.ReadyToApplyVersion is not { } ready)
            {
                DisarmRetry();
                return false;
            }
            version = ready;

            if (_isBusy() || !_sessions.TryCloseForRestart())
            {
                ArmRetry();
                if (_waitingLogged != version)
                {
                    _waitingLogged = version;
                    _log.LogInformation(
                        "Update {Version} is downloaded; the agent restarts into it once no session is going on.",
                        version);
                }
                return false;
            }

            _restarting = true;
            DisarmRetry();
        }

        _log.LogInformation(
            "The agent is idle and update {Version} is downloaded ({Trigger}); restarting into it.", version, trigger);

        bool handedOver;
        try
        {
            handedOver = _restarter.TryRestartIntoUpdate(version);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Restarting into update {Version} failed.", version);
            handedOver = false;
        }
        if (handedOver) return true;

        _sessions.ReopenAfterAbortedRestart();
        _updates.ReportApplyFailed(version);
        lock (_gate) _restarting = false;
        _log.LogWarning(
            "Could not hand update {Version} to the updater; staying on the current version until the next update check.",
            version);
        return false;
    }

    // Under _gate.
    private void ArmRetry() =>
        _retry ??= _clock.CreateTimer(_ => TryRestart("retry"), state: null, RetryInterval, RetryInterval);

    // Under _gate.
    private void DisarmRetry()
    {
        _retry?.Dispose();
        _retry = null;
    }
}
