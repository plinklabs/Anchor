using FocusAgent.Core.Dtos;
using FocusAgent.Core.Focus;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Tamper;

/// <summary>Where <see cref="ExtensionGuard"/> stands.</summary>
public enum ExtensionGuardState
{
    /// <summary>
    /// Nothing to guard: no joined session, or the extension never connected on
    /// this device since the agent started (not installed, or the host isn't
    /// registered), so its absence says nothing about the student.
    /// </summary>
    Off,

    /// <summary>The extension's witness is connected. Edge is filtered.</summary>
    Present,

    /// <summary>
    /// The witness dropped (or was already down when the session was joined) and
    /// the grace period is running. Edge is still allowed: an Edge restart or an
    /// extension update reconnects within it.
    /// </summary>
    Waiting,

    /// <summary>
    /// The grace period ran out while Edge had no window on screen (closed, or
    /// minimized). Edge is allowed; the next time it comes forward the grace
    /// period starts again, so a normal Edge launch gets the time to load the
    /// extension before anything is paused.
    /// </summary>
    Dormant,

    /// <summary>Edge is paused until the extension reconnects.</summary>
    Paused,

    /// <summary>
    /// The student asked to turn the extension back on: Edge is allowed for the
    /// repair window, so they can reach <c>edge://extensions</c>.
    /// </summary>
    Repairing,
}

/// <summary>
/// Pauses Edge while the extension is gone during a joined session (#427), and
/// tells the teacher (#146, #518). The extension can be switched off when it
/// isn't force-installed (the HKLM forcelist fell back to the guided install),
/// and with it off Edge is an unfiltered browser. The
/// <see cref="ExtensionWitnessMonitor"/> sees the native-messaging link drop;
/// this guard decides what the drop means: once the link has stayed down past a
/// short grace period with an Edge window on screen, Edge is
/// <see cref="ExtensionGuardState.Paused"/>, <see cref="FocusSessionController"/>
/// handles it like an off-list app until the link reconnects, and the teacher
/// gets <see cref="TamperKinds.ExtensionDisabled"/> ("switched off or removed").
///
/// The report follows the pause, so the teacher only hears of an extension that
/// is really gone while Edge is open (#518): a restart that reconnects within
/// the grace period and an Edge that was closed report nothing, and one outage
/// is reported once, however often the student opens Edge to repair it.
///
/// With several Edge profiles, the extension can be switched off in one while
/// another's is still connected. The links can't show that, so
/// <see cref="OpenProfileExtensionWatch"/> reads it from the profiles themselves;
/// while it finds such a profile, Edge is paused and the teacher told just the
/// same. A profile that is merely closed has no window left and does neither.
///
/// The link also drops when Edge closes or the extension updates or reloads, and
/// pausing must not get in the way of the extension coming back:
/// <list type="bullet">
/// <item>The grace period covers a reload, an update and an Edge restart: the
/// extension reconnects in seconds and nothing is paused.</item>
/// <item>A grace period that runs out with no Edge window open (Edge was closed)
/// pauses nothing; the grace period starts again when Edge next comes forward,
/// so a later Edge launch has time to load the extension.</item>
/// <item>Pausing only minimizes Edge's windows; the browser and the extension keep
/// running, so the link can reconnect at any time, and the pause lifts the
/// moment it does.</item>
/// <item>A switched-off extension only comes back on through Edge's own
/// <c>edge://extensions</c> page, which a paused Edge can't show and which can't
/// be opened from outside Edge. <see cref="BeginRepair"/> opens Edge for
/// <see cref="RepairWindow"/> so the student can switch it on; when that window
/// runs out without the extension, Edge is paused again.</item>
/// </list>
/// </summary>
public sealed class ExtensionGuard : IDisposable
{
    private readonly SessionCoordinator _sessions;
    private readonly ExtensionWitnessMonitor _witness;
    private readonly IWindowEnumerator _windows;
    private readonly ITamperReporter _reporter;
    private readonly OpenProfileExtensionWatch? _profiles;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExtensionGuard> _log;
    private readonly TimeSpan _grace;
    private readonly string _browser;
    private readonly object _gate = new();

    private ExtensionGuardState _state = ExtensionGuardState.Off;
    private Guid? _sessionId;
    private ITimer? _timer;
    // Bumped on every state change so a timer callback that was already queued
    // when the state moved on (the extension reconnected, the session ended)
    // recognises itself as stale and does nothing.
    private long _generation;
    // The teacher has been told of the outage Edge is paused for. Cleared when
    // the extension is back (or the session ends), so the next outage is told.
    private bool _reported;

    /// <param name="reporter">Sends the TamperDetected event when Edge is paused.</param>
    /// <param name="profiles">
    /// Finds an open profile with the extension switched off while another
    /// profile's is connected (#518). Null: only the links are looked at.
    /// </param>
    public ExtensionGuard(
        SessionCoordinator sessions,
        ExtensionWitnessMonitor witness,
        IWindowEnumerator windows,
        ITamperReporter reporter,
        IOptions<SessionSettings> settings,
        IOptions<WitnessSettings> witnessSettings,
        OpenProfileExtensionWatch? profiles = null,
        TimeProvider? clock = null,
        ILogger<ExtensionGuard>? log = null)
    {
        _sessions = sessions;
        _witness = witness;
        _windows = windows;
        _reporter = reporter;
        _profiles = profiles;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<ExtensionGuard>.Instance;
        _grace = TimeSpan.FromSeconds(Math.Max(1, settings.Value.ExtensionGraceSeconds));
        RepairWindow = TimeSpan.FromSeconds(Math.Max(1, settings.Value.ExtensionRepairSeconds));
        _browser = NormalizeProcessName(witnessSettings.Value.BrowserProcessName);

        _sessions.SessionJoined += OnSessionJoined;
        _sessions.SessionLeft += OnSessionLeft;
        _witness.ConnectionChanged += OnWitnessChanged;
        if (_profiles is not null)
            _profiles.Changed += OnProfilesChanged;
    }

    /// <summary>How long <see cref="BeginRepair"/> opens Edge for.</summary>
    public TimeSpan RepairWindow { get; }

    /// <summary>
    /// The browser this guard pauses, as an allowed-app rule — what the overlay's
    /// "turn it back on" action brings forward.
    /// </summary>
    public AllowedAppRule BrowserRule => new() { MatchKind = AllowedAppMatchKind.ProcessName, Value = _browser };

    public ExtensionGuardState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>True while Edge must be handled as an off-list app.</summary>
    public bool IsBrowserPaused => State == ExtensionGuardState.Paused;

    /// <summary>
    /// Raised when Edge becomes paused — after the grace period, when a repair
    /// window runs out, or when an open profile turns out to have the extension
    /// switched off — so the controller can bring down the Edge windows already
    /// on screen (the foreground hook only fires on changes) and tell the student.
    /// Raised on a timer thread or a witness transport thread.
    /// </summary>
    public event EventHandler? BrowserPaused;

    /// <summary>True when <paramref name="app"/> is the guarded browser.</summary>
    public bool IsBrowser(AppInfo app) =>
        NormalizeProcessName(app.ProcessName).Equals(_browser, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="rule"/> allows the guarded browser, so the
    /// overlay doesn't offer a paused Edge as a way back.
    /// </summary>
    public bool IsBrowserRule(AllowedAppRule rule) => rule.MatchKind switch
    {
        AllowedAppMatchKind.ProcessName =>
            NormalizeProcessName(rule.Value).Equals(_browser, StringComparison.OrdinalIgnoreCase),
        AllowedAppMatchKind.ExecutablePath =>
            Path.GetFileNameWithoutExtension(rule.Value.Trim()).Equals(_browser, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>
    /// Edge came to the foreground while not paused. When the guard is
    /// <see cref="ExtensionGuardState.Dormant"/> (Edge was closed when the grace
    /// period ran out), this is Edge being launched again: give the extension the
    /// grace period to connect before anything is paused.
    /// </summary>
    public void NoteBrowserForeground()
    {
        lock (_gate)
        {
            if (_state != ExtensionGuardState.Dormant) return;
            StartTimerLocked(ExtensionGuardState.Waiting, _grace);
        }
        _log.LogInformation(
            "{Browser} came forward while the extension is gone; waiting {GraceSeconds}s for it to connect",
            _browser, (int)_grace.TotalSeconds);
    }

    /// <summary>
    /// The student asked to turn the extension back on: open Edge for
    /// <see cref="RepairWindow"/>. Returns false (and changes nothing) when Edge
    /// isn't paused.
    /// </summary>
    public bool BeginRepair()
    {
        lock (_gate)
        {
            if (_state != ExtensionGuardState.Paused) return false;
            StartTimerLocked(ExtensionGuardState.Repairing, RepairWindow);
        }
        _log.LogInformation(
            "Opening {Browser} for {RepairSeconds}s so the extension can be switched back on",
            _browser, (int)RepairWindow.TotalSeconds);
        return true;
    }

    private void OnSessionJoined(object? sender, SessionStartedPayload payload)
    {
        ExtensionGuardState state;
        lock (_gate)
        {
            _sessionId = payload.SessionId;
            // Read the witness under our gate so a connect/drop racing the join is
            // applied after it, not lost: OnWitnessChanged takes the same gate.
            if (_witness.IsConnected)
                SetStateLocked(ExtensionGuardState.Present);
            else if (_witness.HasConnected)
                // Switched off before the session started: same grace as a drop.
                StartTimerLocked(ExtensionGuardState.Waiting, _grace);
            else
                SetStateLocked(ExtensionGuardState.Off);
            state = _state;
        }
        _log.LogInformation("Extension guard for session {SessionId} starts {State}", payload.SessionId, state);
    }

    private void OnSessionLeft(object? sender, Guid sessionId)
    {
        lock (_gate)
        {
            if (_sessionId != sessionId) return;
            _sessionId = null;
            SetStateLocked(ExtensionGuardState.Off);
        }
    }

    private void OnWitnessChanged(object? sender, bool connected)
    {
        ExtensionGuardState previous;
        Pause? pause = null;
        lock (_gate)
        {
            if (_sessionId is null) return;
            previous = _state;
            if (!connected)
                StartTimerLocked(ExtensionGuardState.Waiting, _grace);
            else if (_profiles?.IsExtensionOff == true)
                // One profile's extension is back, but another open profile still
                // has it switched off (#518).
                pause = PauseLocked();
            else
                SetStateLocked(ExtensionGuardState.Present);
        }

        if (pause is not null)
            Announce(pause, "an open profile has the extension switched off");
        else if (connected && previous == ExtensionGuardState.Paused)
            _log.LogInformation("Extension reconnected; {Browser} is no longer paused", _browser);
        else if (!connected)
            _log.LogWarning(
                "Extension witness dropped during the session; pausing {Browser} in {GraceSeconds}s unless it reconnects",
                _browser, (int)_grace.TotalSeconds);
    }

    /// <summary>
    /// An open Edge profile has the extension switched off or removed while
    /// another profile's is still connected, or no longer has (#518). The watch
    /// has already waited as long as the grace period (Edge writes it down only
    /// 10 seconds later), so Edge is paused at once. While no extension is
    /// connected at all, the dropped link is already being handled.
    /// </summary>
    private void OnProfilesChanged(object? sender, bool off)
    {
        Pause? pause = null;
        var lifted = false;
        lock (_gate)
        {
            // Off: the extension never connected on this device, so nothing is guarded.
            if (_sessionId is null || _state == ExtensionGuardState.Off) return;
            if (off)
            {
                if (_state == ExtensionGuardState.Present)
                    pause = PauseLocked();
            }
            else if (_state != ExtensionGuardState.Present && _witness.IsConnected)
            {
                SetStateLocked(ExtensionGuardState.Present);
                lifted = true;
            }
        }

        if (pause is not null)
            Announce(pause, "an open profile has the extension switched off");
        else if (lifted)
            _log.LogInformation("Every open profile has the extension again; {Browser} is no longer paused", _browser);
    }

    private void OnTimer(long generation)
    {
        try
        {
            Expire(generation);
        }
        catch (Exception ex)
        {
            // Never let a probe failure escape the timer callback.
            _log.LogWarning(ex, "Extension guard timer threw");
        }
    }

    private void Expire(long generation)
    {
        ExtensionGuardState expired;
        lock (_gate)
        {
            if (generation != _generation) return;
            expired = _state;
        }

        // Probe the desktop outside the gate: it touches Win32.
        var browserOnScreen = _windows.GetOpenWindows().Any(w => IsBrowser(w.App));

        Pause? pause = null;
        lock (_gate)
        {
            // Something moved on while we were probing (a reconnect, a leave).
            if (generation != _generation) return;
            if (browserOnScreen)
                pause = PauseLocked();
            else
                SetStateLocked(ExtensionGuardState.Dormant);
        }

        if (pause is null)
        {
            // Edge was closed (or minimized): nothing to pause, nothing to tell.
            _log.LogInformation(
                "Extension still gone after {Expired} but no {Browser} window is open; waiting for it to come forward",
                expired, _browser);
            return;
        }

        Announce(pause, $"the extension is still gone after {expired}");
    }

    /// <summary>What entering <see cref="ExtensionGuardState.Paused"/> still has to do outside the gate.</summary>
    /// <param name="ReportFor">The session to tell the teacher in, or null when they already know.</param>
    private sealed record Pause(Guid? ReportFor);

    private Pause PauseLocked()
    {
        var report = !_reported;
        SetStateLocked(ExtensionGuardState.Paused);
        _reported = true;
        return new Pause(report ? _sessionId : null);
    }

    private void Announce(Pause pause, string why)
    {
        _log.LogWarning("Pausing {Browser} until the extension is back: {Why}", _browser, why);
        if (pause.ReportFor is { } sessionId)
        {
            _log.LogWarning(
                "The extension is gone with {Browser} open during session {SessionId}; reporting {Kind}",
                _browser, sessionId, TamperKinds.ExtensionDisabled);
            _ = ReportAsync(sessionId);
        }
        try
        {
            BrowserPaused?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A BrowserPaused subscriber threw");
        }
    }

    private async Task ReportAsync(Guid sessionId)
    {
        try
        {
            await _reporter.ReportAsync(sessionId, TamperKinds.ExtensionDisabled).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort like every other tamper report: a failed send must not
            // undo the pause.
            _log.LogWarning(ex, "Reporting {Kind} failed for session {SessionId}",
                TamperKinds.ExtensionDisabled, sessionId);
        }
    }

    private void StartTimerLocked(ExtensionGuardState state, TimeSpan due)
    {
        SetStateLocked(state);
        var generation = _generation;
        _timer = _clock.CreateTimer(
            _ => OnTimer(generation), state: null, dueTime: due, period: Timeout.InfiniteTimeSpan);
    }

    private void SetStateLocked(ExtensionGuardState state)
    {
        // The extension is back, or the session is over: that outage is done.
        if (state is ExtensionGuardState.Present or ExtensionGuardState.Off)
            _reported = false;
        _state = state;
        _generation++;
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        _sessions.SessionJoined -= OnSessionJoined;
        _sessions.SessionLeft -= OnSessionLeft;
        _witness.ConnectionChanged -= OnWitnessChanged;
        if (_profiles is not null)
            _profiles.Changed -= OnProfilesChanged;
        lock (_gate)
        {
            _sessionId = null;
            SetStateLocked(ExtensionGuardState.Off);
        }
    }

    private static string NormalizeProcessName(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];
        return trimmed;
    }
}
