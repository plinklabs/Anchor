using FocusAgent.Core.Dtos;
using FocusAgent.Core.Extension;
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
    /// this device since the agent started and isn't installed in any Edge profile
    /// either, so its absence says nothing about the student. (Installed but never
    /// connected is guarded, #556: the link is waited for, the extension woken, and
    /// Edge paused if it still doesn't come.)
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

/// <summary>Why <see cref="ExtensionGuard"/> has Edge paused, or is opening it to repair.</summary>
public enum ExtensionPauseReason
{
    /// <summary>
    /// The extension is switched off or removed: its link stayed down, or an open
    /// profile has it off (#427, #518).
    /// </summary>
    ExtensionOff,

    /// <summary>
    /// The extension is on, but says it may not work on all sites: its "Site
    /// access" was turned down from "On all sites" (#524).
    /// </summary>
    NoSiteAccess,

    /// <summary>
    /// No Edge profile is signed in with the student's school account, and during a
    /// session only that profile may be used (#525). The pause names the account to
    /// sign in with.
    /// </summary>
    NoSchoolProfile,

    /// <summary>
    /// An Edge profile is signed in with the student's school account, but it
    /// doesn't have the extension (#536): it would browse unfiltered (#519). The
    /// pause says Anchor still has to be added to that profile, and its repair
    /// action opens the store listing in it, the guided install's (#211).
    /// </summary>
    SchoolProfileWithoutExtension,

    /// <summary>
    /// One window is paused, not Edge: it is outside the school profile (another
    /// profile, Guest or InPrivate, #525). Never the guard's own reason:
    /// <see cref="SchoolProfileGuard"/> finds such windows and the focus controller
    /// pauses each with the overlay this reason picks, whose button closes the
    /// window and takes the student to the school profile.
    /// </summary>
    OutsideSchoolProfile,

    /// <summary>
    /// The extension is installed in an open Edge profile, but its link hasn't come
    /// up since the agent started, not even after the agent registered the host
    /// again and opened Edge in that profile to wake it (#556). The student
    /// switched nothing off, so the pause and the teacher's report
    /// (<see cref="TamperKinds.ExtensionNotConnected"/>) say the extension isn't
    /// connected, not that it is off; the repair action opens that profile again.
    /// </summary>
    NotConnected,
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
/// The extension can also be on and still say it may not work on all sites: its
/// "Site access" was turned down on its page in Edge, before the session or during
/// it (#524). <see cref="ExtensionSelfCheckWatch"/> hears that from the extension
/// itself; Edge is paused at once for <see cref="ExtensionPauseReason.NoSiteAccess"/>,
/// the repair action has the extension open its own page, and the next check that
/// passes lifts the pause. That watch tells the teacher, and only when the student
/// doesn't put it back in time.
///
/// During a session only the Edge profile signed in with the school account may
/// be used (#525). While <see cref="SchoolProfileGuard"/> finds none, Edge is paused
/// as a whole for <see cref="ExtensionPauseReason.NoSchoolProfile"/>: the pause
/// names the account, the repair action opens Edge so the student can sign in, and
/// the pause lifts once a profile with that account has the extension. When a
/// profile is signed in with it but lacks the extension, the pause is for
/// <see cref="ExtensionPauseReason.SchoolProfileWithoutExtension"/> instead (#536):
/// it says Anchor still has to be added there, and the repair action opens the
/// store listing in that profile. That guard tells the teacher, and only when it
/// isn't resolved in time. (The windows outside a school profile that does exist
/// are paused one by one by the focus controller.)
///
/// With several Edge profiles, the extension can be switched off in one while
/// another's is still connected. The links can't show that, so
/// <see cref="OpenProfileExtensionWatch"/> reads it from the profiles themselves;
/// while it finds such a profile, Edge is paused and the teacher told just the
/// same. A profile that is merely closed has no window left and does neither.
///
/// The extension can also be installed and never connect (#556): its link was
/// down from the agent's start, so the session was never handed down to it and
/// Edge filtered nothing, with no report. A device where it isn't installed in any
/// profile is still left alone (<see cref="ExtensionGuardState.Off"/>), but where
/// it is (<see cref="ExtensionWakeUp"/>), the join starts the grace period as for a
/// drop. The host was registered again at the join already
/// (<see cref="WitnessLinkSelfCheckOnJoin"/>); when the grace period runs out with
/// Edge open in a profile that has the extension, the guard first wakes the
/// extension, opening Edge in that profile at a page the session allows, and
/// gives it another grace period. Only if it still doesn't connect is Edge paused
/// for <see cref="ExtensionPauseReason.NotConnected"/>, and the teacher told
/// (<see cref="TamperKinds.ExtensionNotConnected"/>). With Edge open only in
/// profiles that never had the extension, nothing is paused (#519).
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
    private readonly ExtensionSelfCheckWatch? _selfCheck;
    private readonly SchoolProfileGuard? _schoolProfile;
    private readonly IBrowserProfileLauncher? _browserLauncher;
    private readonly ExtensionWakeUp? _wakeUp;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExtensionGuard> _log;
    private readonly TimeSpan _grace;
    private readonly string _browser;
    private readonly object _gate = new();

    private ExtensionGuardState _state = ExtensionGuardState.Off;
    private ExtensionPauseReason _reason = ExtensionPauseReason.ExtensionOff;
    private Guid? _sessionId;
    private ITimer? _timer;
    // Bumped on every state change so a timer callback that was already queued
    // when the state moved on (the extension reconnected, the session ended)
    // recognises itself as stale and does nothing.
    private long _generation;
    // The teacher has been told of the outage Edge is paused for. Cleared when
    // the extension is back (or the session ends), so the next outage is told.
    private bool _reported;
    // #556: the extension, installed but never connected, was woken (in this
    // profile) during the current outage; cleared with _reported.
    private bool _woken;
    private string? _wokenProfile;

    /// <param name="reporter">Sends the TamperDetected event when Edge is paused.</param>
    /// <param name="profiles">
    /// Finds an open profile with the extension switched off while another
    /// profile's is connected (#518). Null: only the links are looked at.
    /// </param>
    /// <param name="selfCheck">
    /// What the extension says about its own settings (#524). Null: its site
    /// access isn't looked at.
    /// </param>
    /// <param name="schoolProfile">
    /// Whether an Edge profile is signed in with the school account (#525). Null:
    /// any profile will do.
    /// </param>
    /// <param name="browserLauncher">
    /// Opens the store listing in the school profile that lacks the extension
    /// (#536). Null: the repair action opens Edge only.
    /// </param>
    /// <param name="wakeUp">
    /// Tells whether the extension is installed in an Edge profile, and wakes it
    /// there (#556). Null: a device where the extension never connected is left
    /// alone, installed or not.
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
        ILogger<ExtensionGuard>? log = null,
        ExtensionSelfCheckWatch? selfCheck = null,
        SchoolProfileGuard? schoolProfile = null,
        IBrowserProfileLauncher? browserLauncher = null,
        ExtensionWakeUp? wakeUp = null)
    {
        _sessions = sessions;
        _witness = witness;
        _windows = windows;
        _reporter = reporter;
        _profiles = profiles;
        _selfCheck = selfCheck;
        _schoolProfile = schoolProfile;
        _browserLauncher = browserLauncher;
        _wakeUp = wakeUp;
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
        if (_selfCheck is not null)
            _selfCheck.Changed += OnSelfCheckChanged;
        if (_schoolProfile is not null)
            _schoolProfile.MissingChanged += OnSchoolProfileChanged;
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
    /// Why Edge is <see cref="ExtensionGuardState.Paused"/> or
    /// <see cref="ExtensionGuardState.Repairing"/>: what the pause tells the
    /// student and what its button does. <see cref="ExtensionPauseReason.ExtensionOff"/>
    /// in every other state.
    /// </summary>
    public ExtensionPauseReason PauseReason
    {
        get { lock (_gate) return _reason; }
    }

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
        ExtensionPauseReason reason;
        string? wokenProfile;
        lock (_gate)
        {
            if (_state != ExtensionGuardState.Paused) return false;
            reason = _reason;
            wokenProfile = _wokenProfile;
            StartTimerLocked(ExtensionGuardState.Repairing, RepairWindow);
        }
        if (reason == ExtensionPauseReason.NoSiteAccess)
        {
            _log.LogInformation(
                "Opening {Browser} for {RepairSeconds}s so the extension's site access can be put back",
                _browser, (int)RepairWindow.TotalSeconds);
            // The setting is on the extension's own page, which only the extension
            // can open (#524). Edge is allowed by now, so the page it opens stays up.
            _selfCheck?.OpenExtensionPage();
        }
        else if (reason == ExtensionPauseReason.NoSchoolProfile)
        {
            // Edge doesn't open its own settings pages from outside (an edge:// URL
            // on its command line opens a new tab instead, Edge 157), so the student
            // signs in from Edge's profile button; the overlay's hint says where.
            _log.LogInformation(
                "Opening {Browser} for {RepairSeconds}s so the student can sign in with the school account",
                _browser, (int)RepairWindow.TotalSeconds);
        }
        else if (reason == ExtensionPauseReason.SchoolProfileWithoutExtension)
        {
            // #536: the guided install's store listing (#211), opened in the profile
            // signed in with the school account: Edge adds an extension to the
            // profile whose window has the listing open. Edge is allowed by now.
            var profile = _schoolProfile?.ProfileWithoutExtension;
            _log.LogInformation(
                "Opening {Browser} for {RepairSeconds}s at the extension's store listing in school profile {Profile}",
                _browser, (int)RepairWindow.TotalSeconds, profile ?? "none");
            if (profile is not null)
                _browserLauncher?.OpenProfile(profile, EdgeExtensionPolicy.StoreListingUrl);
        }
        else if (reason == ExtensionPauseReason.NotConnected)
        {
            // #556: the extension is on but never connected; opening its profile
            // at a page wakes it once more. Edge is allowed by now.
            _log.LogInformation(
                "Opening {Browser} for {RepairSeconds}s in profile {Profile} so the extension can connect",
                _browser, (int)RepairWindow.TotalSeconds, wokenProfile ?? "none");
            if (wokenProfile is not null)
                _wakeUp?.Wake(wokenProfile);
        }
        else
        {
            _log.LogInformation(
                "Opening {Browser} for {RepairSeconds}s so the extension can be switched back on",
                _browser, (int)RepairWindow.TotalSeconds);
        }
        return true;
    }

    private void OnSessionJoined(object? sender, SessionStartedPayload payload)
    {
        ExtensionGuardState state;
        Pause? pause = null;
        // #556: never connected, but installed in a profile: guarded all the same.
        // Read outside the gate: it reads files.
        var installedNeverConnected = !_witness.HasConnected && _wakeUp?.IsInstalled() == true;
        lock (_gate)
        {
            _sessionId = payload.SessionId;
            // Read the witness under our gate so a connect/drop racing the join is
            // applied after it, not lost: OnWitnessChanged takes the same gate.
            if (_witness.IsConnected && ProblemWithLinkUpLocked() is { } problem)
                // The extension's answer to the session beat this handler (#524).
                pause = PauseLocked(problem);
            else if (_witness.IsConnected)
                SetStateLocked(ExtensionGuardState.Present);
            else if (_witness.HasConnected || installedNeverConnected)
                // Switched off before the session started: same grace as a drop.
                // Or installed but never connected (#556): the grace gives Edge's
                // own start, and the host registered again at this join, the time
                // to bring the link up.
                StartTimerLocked(ExtensionGuardState.Waiting, _grace);
            else
                SetStateLocked(ExtensionGuardState.Off);
            state = _state;
        }
        _log.LogInformation("Extension guard for session {SessionId} starts {State}", payload.SessionId, state);
        if (installedNeverConnected && state == ExtensionGuardState.Waiting)
            _log.LogWarning(
                "The extension is installed in an Edge profile but hasn't connected since the agent started; " +
                "waking it in {GraceSeconds}s, then pausing {Browser} unless it connects",
                (int)_grace.TotalSeconds, _browser);
        if (pause is not null)
            Announce(pause, Describe(pause.Reason));
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
            else if (ProblemWithLinkUpLocked() is { } problem)
                // One profile's extension is back, but another open profile still
                // has it switched off (#518), or one says it may not work on all
                // sites (#524).
                pause = PauseLocked(problem);
            else
                SetStateLocked(ExtensionGuardState.Present);
        }

        if (pause is not null)
            Announce(pause, Describe(pause.Reason));
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
            // Off: no extension on this device (or no session), so nothing is guarded.
            if (_sessionId is null || _state == ExtensionGuardState.Off) return;
            if (off)
            {
                // Paused for site access already (#524): being switched off in an
                // open profile is the bigger thing, for the student and the teacher.
                if (_state == ExtensionGuardState.Present
                    || (_state == ExtensionGuardState.Paused && _reason != ExtensionPauseReason.ExtensionOff))
                    pause = PauseLocked(ExtensionPauseReason.ExtensionOff);
            }
            else if (_state != ExtensionGuardState.Present && _witness.IsConnected)
            {
                if (ProblemWithLinkUpLocked() is not { } problem)
                {
                    SetStateLocked(ExtensionGuardState.Present);
                    lifted = true;
                }
                else if (_state == ExtensionGuardState.Paused && _reason != problem)
                    // Every open profile has the extension again, but one says it
                    // may not work on all sites (#524): the pause says so now.
                    pause = PauseLocked(problem);
            }
        }

        if (pause is not null)
            Announce(pause, Describe(pause.Reason));
        else if (lifted)
            _log.LogInformation("Every open profile has the extension again; {Browser} is no longer paused", _browser);
    }

    /// <summary>
    /// A connected extension says it may not work on all sites, or no longer
    /// does (#524). It said so itself, so there is nothing to wait for: Edge is
    /// paused at once, and the pause lifts as soon as a check passes. A link that
    /// is down is already being handled, and a pause for the extension being off
    /// stays one.
    /// </summary>
    private void OnSelfCheckChanged(object? sender, bool lost)
    {
        Pause? pause = null;
        var lifted = false;
        lock (_gate)
        {
            // Off: no extension on this device (or no session), so nothing is guarded.
            if (_sessionId is null || _state == ExtensionGuardState.Off) return;
            if (lost)
            {
                if (_state == ExtensionGuardState.Present)
                    pause = PauseLocked(ExtensionPauseReason.NoSiteAccess);
            }
            else if (_state != ExtensionGuardState.Present && _witness.IsConnected
                     && ProblemWithLinkUpLocked() is null)
            {
                SetStateLocked(ExtensionGuardState.Present);
                lifted = true;
            }
        }

        if (pause is not null)
            Announce(pause, Describe(pause.Reason));
        else if (lifted)
            _log.LogInformation("The extension may work on all sites again; {Browser} is no longer paused", _browser);
    }

    /// <summary>
    /// No Edge profile is signed in with the school account any more, or one is
    /// again (#525), or the one that is lacks the extension (#536). The
    /// school-profile guard has looked it up, so there is nothing to wait for: Edge
    /// is paused at once, the pause says what is missing now, and it lifts as soon
    /// as the school profile is found. It comes before site access: which profile
    /// is the school profile decides which extension's settings matter. A link that
    /// is down is already being handled, a pause for the extension being off stays
    /// one, and a repair window runs its course (when it runs out, Edge is paused
    /// for what is missing then).
    /// </summary>
    private void OnSchoolProfileChanged(object? sender, bool missing)
    {
        Pause? pause = null;
        var lifted = false;
        lock (_gate)
        {
            // Off: no extension on this device (or no session), so nothing is guarded.
            if (_sessionId is null || _state == ExtensionGuardState.Off) return;
            if (missing)
            {
                if (SchoolProfileProblemLocked() is { } reason
                    && (_state == ExtensionGuardState.Present
                        || (_state == ExtensionGuardState.Paused
                            && _reason != reason
                            && _reason is ExtensionPauseReason.NoSiteAccess
                                or ExtensionPauseReason.NoSchoolProfile
                                or ExtensionPauseReason.SchoolProfileWithoutExtension)))
                    pause = PauseLocked(reason);
            }
            else if (_state != ExtensionGuardState.Present && _witness.IsConnected)
            {
                if (ProblemWithLinkUpLocked() is not { } problem)
                {
                    SetStateLocked(ExtensionGuardState.Present);
                    lifted = true;
                }
                else if (_state == ExtensionGuardState.Paused && _reason != problem)
                    pause = PauseLocked(problem);
            }
        }

        if (pause is not null)
            Announce(pause, Describe(pause.Reason));
        else if (lifted)
            _log.LogInformation(
                "An Edge profile is signed in with the school account again; {Browser} is no longer paused", _browser);
    }

    /// <summary>
    /// What keeps Edge from being filtered while an extension's link is up, if
    /// anything. Call under <see cref="_gate"/>.
    /// </summary>
    private ExtensionPauseReason? ProblemWithLinkUpLocked() =>
        _profiles?.IsExtensionOff == true ? ExtensionPauseReason.ExtensionOff
        : SchoolProfileProblemLocked() is { } school ? school
        : _selfCheck?.SiteAccessLost == true ? ExtensionPauseReason.NoSiteAccess
        : null;

    /// <summary>
    /// What is missing for the school profile, if anything: a profile signed in
    /// with the school account (#525), or the extension in the one that is (#536).
    /// Read in one go, so the two can't be mixed up mid-scan.
    /// </summary>
    private ExtensionPauseReason? SchoolProfileProblemLocked() =>
        _schoolProfile?.Reading is { Status: SchoolProfileStatus.Missing } reading
            ? reading.Profile is null
                ? ExtensionPauseReason.NoSchoolProfile
                : ExtensionPauseReason.SchoolProfileWithoutExtension
            : null;

    private static string Describe(ExtensionPauseReason reason) => reason switch
    {
        ExtensionPauseReason.NoSiteAccess => "the extension may not work on all sites",
        ExtensionPauseReason.NoSchoolProfile => "no Edge profile is signed in with the school account",
        ExtensionPauseReason.SchoolProfileWithoutExtension =>
            "the Edge profile signed in with the school account doesn't have the extension",
        ExtensionPauseReason.NotConnected => "the extension is installed but hasn't connected",
        _ => "an open profile has the extension switched off",
    };

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
        // #556: installed but never connected since the agent started. Which open
        // profiles have the extension decides what is done; read outside the gate,
        // it reads files. A link that comes up meanwhile bumps the generation.
        var neverConnected = browserOnScreen && _wakeUp is not null && !_witness.HasConnected;
        var open = neverConnected ? _wakeUp!.FindOpen() : Array.Empty<ProfileWithExtension>();

        Pause? pause = null;
        string? wake = null;
        lock (_gate)
        {
            // Something moved on while we were probing (a reconnect, a leave).
            if (generation != _generation) return;
            if (!browserOnScreen)
                SetStateLocked(ExtensionGuardState.Dormant);
            else if (!neverConnected)
                // The link is down, or a repair window ran out with the problem
                // that paused Edge still there.
                pause = PauseLocked(
                    _witness.IsConnected && ProblemWithLinkUpLocked() is { } problem
                        ? problem
                        : ExtensionPauseReason.ExtensionOff);
            else if (open.Count == 0)
                // Edge is open only in profiles that never had the extension: not
                // "switched off" (#518), and not this guard's to pause (#519).
                SetStateLocked(ExtensionGuardState.Dormant);
            else if (open.All(p => !p.On))
                // Installed, but switched off in every open profile that has it.
                pause = PauseLocked(ExtensionPauseReason.ExtensionOff);
            else if (!_woken)
            {
                // First wake it: a navigation in its profile starts its service
                // worker, which connects at once. Another grace period for that.
                wake = open.First(p => p.On).Profile;
                StartTimerLocked(ExtensionGuardState.Waiting, _grace);
                _woken = true;
                _wokenProfile = wake;
            }
            else
                pause = PauseLocked(open.Any(p => !p.On)
                    ? ExtensionPauseReason.ExtensionOff
                    : ExtensionPauseReason.NotConnected);
        }

        if (wake is not null)
        {
            _log.LogWarning(
                "The extension in Edge profile {Profile} still hasn't connected after {Expired}; waking it, and pausing {Browser} in {GraceSeconds}s unless it connects",
                wake, expired, _browser, (int)_grace.TotalSeconds);
            _wakeUp!.Wake(wake);
            return;
        }

        if (pause is null)
        {
            if (browserOnScreen)
                _log.LogInformation(
                    "The extension never connected, and no open {Browser} profile has it installed; not pausing",
                    _browser);
            else
                // Edge was closed (or minimized): nothing to pause, nothing to tell.
                _log.LogInformation(
                    "Extension still gone after {Expired} but no {Browser} window is open; waiting for it to come forward",
                    expired, _browser);
            return;
        }

        Announce(pause, pause.Reason switch
        {
            ExtensionPauseReason.NoSiteAccess => $"the extension still may not work on all sites after {expired}",
            ExtensionPauseReason.NoSchoolProfile =>
                $"still no Edge profile signed in with the school account after {expired}",
            ExtensionPauseReason.SchoolProfileWithoutExtension =>
                $"the school profile still doesn't have the extension after {expired}",
            ExtensionPauseReason.NotConnected =>
                $"the extension is installed but still hasn't connected after {expired}, though woken",
            _ => $"the extension is still gone after {expired}",
        });
    }

    /// <summary>What entering <see cref="ExtensionGuardState.Paused"/> still has to do outside the gate.</summary>
    /// <param name="ReportFor">The session to tell the teacher in, or null when they already know.</param>
    /// <param name="Reason">Why Edge is paused.</param>
    private sealed record Pause(Guid? ReportFor, ExtensionPauseReason Reason);

    private Pause PauseLocked(ExtensionPauseReason reason)
    {
        // "Switched off or removed" is told at the pause (#518), and so is "isn't
        // connected" (#556); one outage is told once. Site access is told by the
        // self-check watch, and only if the student doesn't put it back (#524); a
        // missing school profile, or its missing extension, by the school-profile
        // guard, likewise (#525, #536).
        var report = (reason is ExtensionPauseReason.ExtensionOff or ExtensionPauseReason.NotConnected)
                     && !_reported;
        SetStateLocked(ExtensionGuardState.Paused);
        _reason = reason;
        if (report)
            _reported = true;
        return new Pause(report ? _sessionId : null, reason);
    }

    /// <summary>What the teacher is told for a pause the guard reports itself.</summary>
    private static string ReportedKind(ExtensionPauseReason reason) =>
        reason == ExtensionPauseReason.NotConnected ? TamperKinds.ExtensionNotConnected : TamperKinds.ExtensionDisabled;

    private void Announce(Pause pause, string why)
    {
        _log.LogWarning("Pausing {Browser} until the extension is back: {Why}", _browser, why);
        if (pause.ReportFor is { } sessionId)
        {
            var kind = ReportedKind(pause.Reason);
            _log.LogWarning(
                "The extension is gone with {Browser} open during session {SessionId}; reporting {Kind}",
                _browser, sessionId, kind);
            _ = ReportAsync(sessionId, kind);
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

    private async Task ReportAsync(Guid sessionId, string kind)
    {
        try
        {
            await _reporter.ReportAsync(sessionId, kind).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort like every other tamper report: a failed send must not
            // undo the pause.
            _log.LogWarning(ex, "Reporting {Kind} failed for session {SessionId}", kind, sessionId);
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
        {
            _reported = false;
            _woken = false;
            _wokenProfile = null;
        }
        // A reason belongs to a pause and to the repair window it opens.
        if (state is not (ExtensionGuardState.Paused or ExtensionGuardState.Repairing))
            _reason = ExtensionPauseReason.ExtensionOff;
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
        if (_selfCheck is not null)
            _selfCheck.Changed -= OnSelfCheckChanged;
        if (_schoolProfile is not null)
            _schoolProfile.MissingChanged -= OnSchoolProfileChanged;
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
