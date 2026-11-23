using FocusAgent.Core.Auth;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Tamper;

/// <summary>
/// During a joined session, only the Edge profile signed in with the student's
/// school account may be used (#525). Serving every profile right kept leaving
/// gaps (#517, #518, #519), and Guest and InPrivate windows are further ways
/// around the extension; permanent Edge policies would also restrict the laptop
/// outside lessons, and don't reach laptops outside Intune. So the rule holds only
/// while a session runs.
///
/// Every <see cref="SessionSettings.SchoolProfileScanSeconds"/> this looks up the
/// school profile (<see cref="EdgeSchoolProfileProbe"/>) and Edge's open windows:
/// <list type="bullet">
/// <item>With a school profile (<see cref="SchoolProfileStatus.Found"/>), a window
/// of any other profile, a Guest window and an InPrivate window is outside it, and
/// so is the window of a site installed as an app in another profile (#537):
/// <see cref="WindowsOutside"/> hands the ones on screen to the focus controller,
/// which pauses each of them with the extension pause's overlay and machinery
/// (<see cref="Focus.FocusSessionController"/>), and pauses one that comes
/// forward again (<see cref="IsOutsideSchoolProfile"/>). The school profile's
/// windows keep working, the windows of the sites installed as apps in it
/// too.</item>
/// <item>With none (<see cref="SchoolProfileStatus.Missing"/>), every Edge window
/// is outside it, so <see cref="ExtensionGuard"/> pauses Edge as a whole. With no
/// profile signed in with the school account
/// (<see cref="ExtensionPauseReason.NoSchoolProfile"/>), its message names the
/// account to sign in with and its button opens Edge for its repair window. With
/// one that is but lacks the extension
/// (<see cref="ExtensionPauseReason.SchoolProfileWithoutExtension"/>, #536), it says
/// Anchor still has to be added to that profile (<see cref="ProfileWithoutExtension"/>),
/// and its button opens the store listing in it. The pause lifts by itself once a
/// profile with that account has the extension.</item>
/// </list>
/// The teacher is told only when the student doesn't resolve it within
/// <see cref="SessionSettings.SchoolProfileReportSeconds"/>: a window outside the
/// school profile still open (<see cref="TamperKinds.OutsideSchoolProfile"/>),
/// still no profile signed in with the school account
/// (<see cref="TamperKinds.SchoolProfileMissing"/>), or still no extension in the
/// one that is (<see cref="TamperKinds.SchoolProfileWithoutExtension"/>). Signing
/// in and adding the extension are two steps, and each gets the report time of
/// its own. An InPrivate window is left out of the first: it has its own report,
/// <see cref="TamperKinds.InPrivateOpened"/>, which waits as long
/// (<see cref="InPrivateWitnessMonitor"/>, #148, #535).
///
/// The rule guards the devices the extension pause guards. It leaves a device
/// alone where the extension never connected since the agent started and isn't
/// installed in any Edge profile either: there Edge isn't filtered in any
/// profile, and pausing one wouldn't change that. Where it is installed, though
/// its link never came up (#556), the rule is in force all the same (#519): a
/// profile that never had the extension, a Guest window and an InPrivate window
/// would otherwise browse unfiltered next to the profile that has it, and nobody
/// would hear of it. The rule is off as well while the school profile can't be
/// told (<see cref="SchoolProfileStatus.Unknown"/>). When the session ends,
/// nothing is paused any more.
/// </summary>
public sealed class SchoolProfileGuard : IDisposable
{
    private readonly SessionCoordinator _sessions;
    private readonly ExtensionWitnessMonitor _witness;
    private readonly EdgeSchoolProfileProbe _probe;
    private readonly IBrowserWindowReader _windows;
    private readonly SignedInAccount _account;
    private readonly ITamperReporter _reporter;
    private readonly ExtensionWakeUp? _wakeUp;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _reportAfter;
    private readonly string _browser;
    private readonly ILogger<SchoolProfileGuard> _log;
    private readonly object _gate = new();

    // Held for a whole scan, so two timer ticks never scan at once and Changed is
    // raised in order.
    private readonly object _scanGate = new();

    private Guid? _sessionId;
    private ITimer? _timer;
    // #519: the extension is installed in an Edge profile though its link wasn't
    // up at the join, as the extension pause reads it then (#556). With a link up
    // since, the device is guarded anyway.
    private bool _installedAtJoin;
    private SchoolProfileReading _reading = SchoolProfileReading.Unknown;
    // The name Edge shows for the school profile, from the title of one of its
    // browser windows (#537); kept while none is open.
    private string? _schoolProfileName;
    private IReadOnlyList<nint> _outside = Array.Empty<nint>();
    // Since when a window outside the school profile (InPrivate aside) has been
    // open, and since when there has been no school profile; null while not.
    private DateTimeOffset? _outsideSince;
    private DateTimeOffset? _missingSince;
    // Whether what has been missing since _missingSince is the extension in the
    // profile signed in with the school account (#536), not the sign-in itself.
    private bool _missingExtension;
    // The teacher has been told of the current one; cleared once it is resolved.
    private bool _reportedOutside;
    private bool _reportedMissing;

    /// <param name="wakeUp">
    /// Tells whether the extension is installed in an Edge profile, as the extension
    /// pause reads it at the join (#556): where it is, the rule is in force though
    /// the link never came up (#519). Null: only a device where the link came up is
    /// guarded.
    /// </param>
    public SchoolProfileGuard(
        SessionCoordinator sessions,
        ExtensionWitnessMonitor witness,
        EdgeSchoolProfileProbe probe,
        IBrowserWindowReader windows,
        SignedInAccount account,
        ITamperReporter reporter,
        IOptions<SessionSettings> settings,
        IOptions<WitnessSettings> witnessSettings,
        TimeProvider? clock = null,
        ILogger<SchoolProfileGuard>? log = null,
        ExtensionWakeUp? wakeUp = null)
    {
        _sessions = sessions;
        _witness = witness;
        _probe = probe;
        _windows = windows;
        _account = account;
        _reporter = reporter;
        _wakeUp = wakeUp;
        _clock = clock ?? TimeProvider.System;
        _interval = TimeSpan.FromSeconds(Math.Max(1, settings.Value.SchoolProfileScanSeconds));
        _reportAfter = TimeSpan.FromSeconds(Math.Max(1, settings.Value.SchoolProfileReportSeconds));
        _browser = NormalizeProcessName(witnessSettings.Value.BrowserProcessName);
        _log = log ?? NullLogger<SchoolProfileGuard>.Instance;

        _sessions.SessionJoined += OnSessionJoined;
        _sessions.SessionLeft += OnSessionLeft;
    }

    /// <summary>Where the school profile stands; <see cref="SchoolProfileStatus.Unknown"/> outside a session.</summary>
    public SchoolProfileStatus Status
    {
        get { lock (_gate) return _reading.Status; }
    }

    /// <summary>
    /// The school profile's folder (<c>Profile 1</c>) while it is
    /// <see cref="SchoolProfileStatus.Found"/>, else null.
    /// </summary>
    public string? Profile
    {
        get
        {
            lock (_gate)
                return _reading.Status == SchoolProfileStatus.Found ? _reading.Profile : null;
        }
    }

    /// <summary>True while, in a session, no Edge profile signed in with the school account has the extension.</summary>
    public bool IsMissing => Status == SchoolProfileStatus.Missing;

    /// <summary>
    /// Where the school profile stands, as the last scan found it, read in one go:
    /// its status, and the profile signed in with the school account while it is
    /// <see cref="SchoolProfileStatus.Found"/> or lacks the extension (#536).
    /// <see cref="SchoolProfileReading.Unknown"/> outside a session.
    /// </summary>
    public SchoolProfileReading Reading
    {
        get { lock (_gate) return _reading; }
    }

    /// <summary>
    /// The profile signed in with the school account (its folder) while it doesn't
    /// have the extension (#536): Anchor still has to be added to it. Null while no
    /// profile is signed in with the school account, while the one that is has the
    /// extension, and outside a session.
    /// </summary>
    public string? ProfileWithoutExtension
    {
        get
        {
            lock (_gate)
                return _reading.Status == SchoolProfileStatus.Missing ? _reading.Profile : null;
        }
    }

    /// <summary>The school account Edge must be signed in with, for the pause to name.</summary>
    public string? AccountName => _account.Current?.Username;

    /// <summary>The windows outside the school profile the last scan found, minimized ones included.</summary>
    public IReadOnlyList<nint> OutsideWindows
    {
        get { lock (_gate) return _outside; }
    }

    /// <summary>
    /// Raised when <see cref="IsMissing"/> changes during a session, with its new
    /// value, and while it stays true when what is missing does (#536): the student
    /// signed in to a profile that lacks the extension (<see cref="ProfileWithoutExtension"/>
    /// is that profile now), or signed out of it. On a timer thread. Not raised for
    /// the reset when the session ends.
    /// </summary>
    public event EventHandler<bool>? MissingChanged;

    /// <summary>
    /// Raised by a scan that found windows outside the school profile on screen
    /// (not minimized), with those windows, on a timer thread: the focus
    /// controller pauses them. Raised on every such scan, so a window the student
    /// restored without a foreground change goes down again too. Not raised where
    /// the extension never connected and isn't installed in any profile (the rule
    /// leaves such a device alone).
    /// </summary>
    public event EventHandler<IReadOnlyList<nint>>? WindowsOutside;

    /// <summary>
    /// Which profile <paramref name="windowHandle"/>, a browser or installed site's
    /// app window, belongs to, read now against the school profile the last scan
    /// found. <see cref="EdgeWindowProfile.Unknown"/> while no school profile is
    /// found (outside a session, too) and for a window the rule can't place.
    /// </summary>
    public EdgeWindowProfile Classify(nint windowHandle)
    {
        string? school, schoolName;
        lock (_gate)
        {
            if (_reading.Status != SchoolProfileStatus.Found) return EdgeWindowProfile.Unknown;
            school = _reading.Profile;
            schoolName = _schoolProfileName;
        }
        var window = _windows.Describe(windowHandle);
        return window is null
            ? EdgeWindowProfile.Unknown
            : _probe.Classify(school, window.AppId, window.Title, schoolName);
    }

    /// <summary>
    /// True when <paramref name="windowHandle"/> is an ordinary (not InPrivate)
    /// browser window of the school profile, the one place Edge may be used during
    /// a session. False for the window of a site installed as an app there, which
    /// has no address bar (#537), outside a session and whenever that can't be
    /// told. What reads Edge from outside, such as the address-bar check (#526),
    /// looks only at these windows: the others are paused.
    /// </summary>
    public bool IsSchoolProfileWindow(nint windowHandle) =>
        Classify(windowHandle) == EdgeWindowProfile.SchoolProfile;

    /// <summary>
    /// True when <paramref name="windowHandle"/> is a window of another profile
    /// (a site installed as an app there included, #537), a Guest window or an
    /// InPrivate window while there is a school profile: one the rule pauses.
    /// Whether the rule is in force (the extension pause's state) is the caller's
    /// to say.
    /// </summary>
    public bool IsOutsideSchoolProfile(nint windowHandle) =>
        EdgeSchoolProfileProbe.IsOutside(Classify(windowHandle));

    /// <summary>
    /// A window of the school profile to bring the student to, on screen ones
    /// first; 0 when none is open.
    /// </summary>
    public nint FindSchoolProfileWindow()
    {
        string? school;
        lock (_gate)
        {
            if (_reading.Status != SchoolProfileStatus.Found) return IntPtr.Zero;
            school = _reading.Profile;
        }
        var windows = _windows.GetBrowserWindows(_browser)
            .Where(w => _probe.Classify(school, w.AppId, w.Title) == EdgeWindowProfile.SchoolProfile)
            .OrderBy(w => w.Minimized)
            .ToList();
        return windows.Count == 0 ? IntPtr.Zero : windows[0].Handle;
    }

    private void OnSessionJoined(object? sender, SessionStartedPayload payload)
    {
        var sessionId = payload.SessionId;
        // #519: read as the extension pause reads it at the join (#556), so the two
        // guard the same devices. Outside the gate: it reads files.
        var installed = !_witness.HasConnected && _wakeUp?.IsInstalled() == true;
        ITimer? previous;
        lock (_gate)
        {
            previous = _timer;
            _sessionId = sessionId;
            ResetLocked();
            _installedAtJoin = installed;
            // The first scan comes one interval after the join, not at once: what it
            // finds must reach the extension guard and the focus controller, which
            // hear of the session after this does. A window outside the school
            // profile that is already open is paused within that interval.
            _timer = _clock.CreateTimer(_ => OnTimer(sessionId), state: null, dueTime: _interval, period: _interval);
        }
        previous?.Dispose();
    }

    private void OnSessionLeft(object? sender, Guid sessionId)
    {
        ITimer? timer;
        lock (_gate)
        {
            if (_sessionId != sessionId) return;
            timer = _timer;
            _timer = null;
            _sessionId = null;
            ResetLocked();
        }
        timer?.Dispose();
    }

    private void ResetLocked()
    {
        _reading = SchoolProfileReading.Unknown;
        _schoolProfileName = null;
        _outside = Array.Empty<nint>();
        _outsideSince = null;
        _missingSince = null;
        _missingExtension = false;
        _reportedOutside = false;
        _reportedMissing = false;
        _installedAtJoin = false;
    }

    private void OnTimer(Guid sessionId)
    {
        // A scan still running when the next tick comes skips that tick.
        if (!Monitor.TryEnter(_scanGate)) return;
        try
        {
            Scan(sessionId);
        }
        catch (Exception ex)
        {
            // Never let a probe failure escape the timer callback.
            _log.LogWarning(ex, "Looking for the school profile threw");
        }
        finally
        {
            Monitor.Exit(_scanGate);
        }
    }

    private void Scan(Guid sessionId)
    {
        lock (_gate)
        {
            if (_sessionId != sessionId) return;
        }

        // Outside the gate: it reads files and the desktop.
        var reading = _probe.Find(_account.Current);
        var windows = reading.Status == SchoolProfileStatus.Found
            ? _windows.GetBrowserWindows(_browser)
            : Array.Empty<BrowserWindowInfo>();
        // The name Edge shows for the school profile places the windows of an app
        // installed in it and in another profile too (#537). It stays known while
        // none of its browser windows is open, for as long as it is the same profile.
        string? schoolName;
        lock (_gate)
        {
            schoolName = string.Equals(reading.Profile, _reading.Profile, StringComparison.OrdinalIgnoreCase)
                ? _schoolProfileName
                : null;
        }
        schoolName = _probe.SchoolProfileName(reading.Status == SchoolProfileStatus.Found ? reading.Profile : null, windows)
                     ?? schoolName;
        var outside = windows
            .Select(w => (Window: w, Profile: _probe.Classify(reading.Profile, w.AppId, w.Title, schoolName)))
            .Where(w => EdgeSchoolProfileProbe.IsOutside(w.Profile))
            .ToList();
        var now = _clock.GetUtcNow();

        bool missingChanged, missing, guarded;
        SchoolProfileReading previous;
        string? reportKind = null;
        lock (_gate)
        {
            if (_sessionId != sessionId) return;
            // The rule leaves a device alone where the extension never connected and
            // isn't installed either, as the extension pause does
            // (ExtensionGuardState.Off). Installed but never connected is guarded
            // (#519): its profile is the one to work in, and a window of a profile
            // that never had the extension browses unfiltered.
            guarded = _witness.HasConnected || _installedAtJoin;
            previous = _reading;
            _reading = reading;
            _schoolProfileName = schoolName;
            _outside = outside.Select(w => w.Window.Handle).ToArray();
            missing = reading.Status == SchoolProfileStatus.Missing;
            var wasMissing = previous.Status == SchoolProfileStatus.Missing;
            // While missing, what is missing can change too (#536): the student
            // signed in to a profile without the extension, or out of it.
            missingChanged = missing != wasMissing
                             || (missing && !string.Equals(reading.Profile, previous.Profile, StringComparison.OrdinalIgnoreCase));
            var missingExtension = missing && reading.Profile is not null;

            // What the teacher is told: only what the student hasn't resolved in time.
            var reportable = outside.Any(w => w.Profile != EdgeWindowProfile.InPrivate);
            if (!guarded || !reportable)
            {
                _outsideSince = null;
                _reportedOutside = false;
            }
            else
            {
                _outsideSince ??= now;
                if (!_reportedOutside && now - _outsideSince >= _reportAfter)
                {
                    _reportedOutside = true;
                    reportKind = TamperKinds.OutsideSchoolProfile;
                }
            }
            if (!guarded || !missing)
            {
                _missingSince = null;
                _reportedMissing = false;
            }
            else
            {
                // Signing in and adding the extension are two steps (#536): the
                // student who signs in to a profile without it gets the report time
                // again to add it, and the teacher hears of the step left undone.
                if (_missingSince is not null && missingExtension != _missingExtension)
                {
                    _missingSince = null;
                    _reportedMissing = false;
                }
                _missingExtension = missingExtension;
                _missingSince ??= now;
                if (!_reportedMissing && now - _missingSince >= _reportAfter)
                {
                    _reportedMissing = true;
                    reportKind = missingExtension
                        ? TamperKinds.SchoolProfileWithoutExtension
                        : TamperKinds.SchoolProfileMissing;
                }
            }
        }

        if (reading.Status != previous.Status
            || !string.Equals(reading.Profile, previous.Profile, StringComparison.OrdinalIgnoreCase))
        {
            if (reading.Status != SchoolProfileStatus.Missing)
                _log.LogInformation("School profile: {Status} ({Profile})", reading.Status, reading.Profile ?? "none");
            else if (reading.Profile is null)
                _log.LogWarning("No Edge profile is signed in with the school account");
            else
                _log.LogWarning(
                    "Edge profile {Profile} is signed in with the school account but doesn't have the extension",
                    reading.Profile);
        }

        if (reportKind is not null)
        {
            _log.LogWarning(
                "Not resolved after {Seconds}s in session {SessionId}; reporting {Kind}",
                (int)_reportAfter.TotalSeconds, sessionId, reportKind);
            _ = ReportAsync(sessionId, reportKind);
        }

        if (missingChanged)
        {
            try
            {
                MissingChanged?.Invoke(this, missing);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "A school-profile MissingChanged subscriber threw (missing={Missing})", missing);
            }
        }

        var onScreen = outside.Where(w => !w.Window.Minimized).Select(w => w.Window.Handle).ToArray();
        if (guarded && onScreen.Length > 0)
        {
            _log.LogInformation(
                "{Count} {Browser} window(s) outside school profile {Profile} on screen ({Kinds}); pausing them",
                onScreen.Length, _browser, reading.Profile,
                string.Join(", ", outside.Where(w => !w.Window.Minimized).Select(w => w.Profile)));
            try
            {
                WindowsOutside?.Invoke(this, onScreen);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "A school-profile WindowsOutside subscriber threw");
            }
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
            // Best-effort like every other tamper report: the pause stays either way.
            _log.LogWarning(ex, "Reporting {Kind} failed for session {SessionId}", kind, sessionId);
        }
    }

    public void Dispose()
    {
        _sessions.SessionJoined -= OnSessionJoined;
        _sessions.SessionLeft -= OnSessionLeft;
        ITimer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
            _sessionId = null;
            ResetLocked();
        }
        timer?.Dispose();
    }

    private static string NormalizeProcessName(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];
        return trimmed;
    }
}
