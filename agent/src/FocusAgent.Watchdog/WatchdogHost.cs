using FocusAgent.Core.Watchdog;
using Serilog;

namespace FocusAgent.Watchdog;

/// <summary>
/// The supervisor loop. Wakes every <see cref="WatchdogPolicy.PollInterval"/>,
/// asks the probe whether the App is alive, and relaunches it if not — unless
/// (a) the user just clicked Quit (a fresh <c>quit.flag</c> is present) or
/// (b) the crash budget is exhausted and we're inside a cooldown.
/// </summary>
internal sealed class WatchdogHost
{
    private readonly IAppPresenceProbe _probe;
    private readonly IAppLauncher _launcher;
    private readonly WatchdogPolicy _policy;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset?> _quitFlagLastWriteUtc;
    private readonly CrashCooldownPolicy _cooldown;

    private bool _wasAlivePreviousTick;
    private bool _loggedQuitFlagThisCycle;
    private bool _loggedCooldownThisCycle;

    public WatchdogHost(
        IAppPresenceProbe probe,
        IAppLauncher launcher,
        WatchdogPolicy policy,
        TimeProvider clock,
        ILogger log,
        Func<DateTimeOffset?> quitFlagLastWriteUtc)
    {
        _probe = probe;
        _launcher = launcher;
        _policy = policy;
        _clock = clock;
        _log = log;
        _quitFlagLastWriteUtc = quitFlagLastWriteUtc;
        _cooldown = new CrashCooldownPolicy(policy);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _log.Information(
            "Watchdog supervising FocusAgent.App (poll every {PollSeconds}s, crash window {WindowSeconds}s/{Limit}, cooldown {CooldownMinutes}m)",
            _policy.PollInterval.TotalSeconds,
            _policy.CrashWindow.TotalSeconds,
            _policy.CrashWindowLimit,
            _policy.CrashCooldown.TotalMinutes);

        _wasAlivePreviousTick = _probe.IsAppAlive();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Watchdog tick failed; will retry on next interval");
            }

            try
            {
                await Task.Delay(_policy.PollInterval, _clock, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.Information("Watchdog stopping (cancellation requested)");
    }

    /// <summary>
    /// One iteration of the loop. Public for the <c>--one-shot</c> verify mode.
    /// </summary>
    public TickOutcome Tick()
    {
        var now = _clock.GetUtcNow();
        var alive = _probe.IsAppAlive();

        if (alive)
        {
            if (!_wasAlivePreviousTick)
            {
                _log.Information("FocusAgent.App is back up");
            }
            _wasAlivePreviousTick = true;
            _loggedQuitFlagThisCycle = false;
            _loggedCooldownThisCycle = false;
            return TickOutcome.AppAlive;
        }

        // App is dead. Three reasons we don't relaunch right now:
        //   1. The user just clicked Quit (fresh quit.flag).
        //   2. We're inside a crash-loop cooldown.
        //   3. (next tick) We have no resolvable App exe.
        var flagMtime = _quitFlagLastWriteUtc();
        if (QuitFlagGate.ShouldSuppressRelaunch(flagMtime, now, _policy.QuitFlagFreshness))
        {
            if (!_loggedQuitFlagThisCycle)
            {
                _log.Information(
                    "quit flag honored, not relaunching (flag written {AgeSeconds:F1}s ago)",
                    flagMtime is { } w ? (now - w).TotalSeconds : 0d);
                _loggedQuitFlagThisCycle = true;
            }
            _wasAlivePreviousTick = false;
            return TickOutcome.SuppressedByQuitFlag;
        }

        if (_cooldown.IsInCooldown(now))
        {
            if (!_loggedCooldownThisCycle)
            {
                _log.Warning(
                    "Crash-loop cooldown active until {Until:O} ({TotalCrashes} crashes seen total); not relaunching",
                    _cooldown.CooldownUntil,
                    _cooldown.TotalCrashes);
                _loggedCooldownThisCycle = true;
            }
            _wasAlivePreviousTick = false;
            return TickOutcome.SuppressedByCooldown;
        }

        if (_wasAlivePreviousTick)
        {
            // First tick where we notice the App is gone — record it as a
            // crash. Subsequent ticks where it's still down (e.g. while a
            // relaunch is in flight) don't double-count.
            var evaluation = _cooldown.RecordCrash(now);
            _log.Warning(
                "FocusAgent.App is not running — crash {InWindow}/{Limit} in last {WindowSeconds}s",
                evaluation.CrashesInWindow,
                _policy.CrashWindowLimit,
                _policy.CrashWindow.TotalSeconds);

            if (evaluation.InCooldown)
            {
                _log.Warning(
                    "Crash budget exhausted ({Limit} crashes within {WindowSeconds}s); pausing relaunch until {Until:O}",
                    _policy.CrashWindowLimit,
                    _policy.CrashWindow.TotalSeconds,
                    evaluation.CooldownUntil);
                _wasAlivePreviousTick = false;
                _loggedCooldownThisCycle = true;
                return TickOutcome.SuppressedByCooldown;
            }
        }

        var launched = _launcher.TryLaunchApp();
        _wasAlivePreviousTick = false;
        _loggedQuitFlagThisCycle = false;
        _loggedCooldownThisCycle = false;
        return launched ? TickOutcome.Relaunched : TickOutcome.LaunchFailed;
    }
}

public enum TickOutcome
{
    AppAlive,
    Relaunched,
    LaunchFailed,
    SuppressedByQuitFlag,
    SuppressedByCooldown,
}
