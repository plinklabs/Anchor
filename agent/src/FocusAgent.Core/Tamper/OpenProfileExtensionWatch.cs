using FocusAgent.Core.Dtos;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Tamper;

/// <summary>
/// Watches, during a joined session, for an Edge profile that is open with the
/// extension switched off or removed (#518). With several profiles the witness
/// link can't say so: another profile's extension is still connected, and a link
/// that drops is as often a profile being closed. So this asks
/// <see cref="IOpenProfileExtensionProbe"/> every
/// <see cref="SessionSettings.ExtensionProfileScanSeconds"/>, which reads it from
/// the profiles' windows and preferences, and <see cref="ExtensionGuard"/> pauses
/// Edge and tells the teacher while <see cref="IsExtensionOff"/>.
///
/// Edge writes a profile's preferences 10 seconds after a change, which gives
/// this the same patience as the grace period of a dropped link: an extension
/// that is restarted, updated or switched off and on again within that time never
/// reaches the file. The file lags the other way too: for those 10 seconds after
/// the extension is switched back on it still says "off". Switching it on brings
/// up a link, so a find within <see cref="PreferencesLag"/> of a link coming up
/// waits for the next scan.
///
/// The first scan of a session only takes stock (it is what "removed" is later
/// measured against), so nothing is found before every subscriber has heard of
/// the session.
/// </summary>
public sealed class OpenProfileExtensionWatch : IDisposable
{
    /// <summary>
    /// How long after a link came up the preferences may still say the extension
    /// is off: Edge's 10 seconds, and some room.
    /// </summary>
    public static readonly TimeSpan PreferencesLag = TimeSpan.FromSeconds(12);

    private readonly SessionCoordinator _sessions;
    private readonly ExtensionWitnessMonitor _witness;
    private readonly IOpenProfileExtensionProbe _probe;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private readonly ILogger<OpenProfileExtensionWatch> _log;
    private readonly object _gate = new();

    private Guid? _sessionId;
    private ITimer? _timer;
    private bool _tookStock;
    private DateTimeOffset _linkCameUp = DateTimeOffset.MinValue;
    private IReadOnlyList<string> _profiles = Array.Empty<string>();

    public OpenProfileExtensionWatch(
        SessionCoordinator sessions,
        ExtensionWitnessMonitor witness,
        IOpenProfileExtensionProbe probe,
        IOptions<SessionSettings> settings,
        TimeProvider? clock = null,
        ILogger<OpenProfileExtensionWatch>? log = null)
    {
        _sessions = sessions;
        _witness = witness;
        _probe = probe;
        _clock = clock ?? TimeProvider.System;
        _interval = TimeSpan.FromSeconds(Math.Max(1, settings.Value.ExtensionProfileScanSeconds));
        _log = log ?? NullLogger<OpenProfileExtensionWatch>.Instance;

        _sessions.SessionJoined += OnSessionJoined;
        _sessions.SessionLeft += OnSessionLeft;
        _witness.ConnectionOpened += OnLinkCameUp;
    }

    /// <summary>
    /// The profiles (their folder names) that are open with the extension switched
    /// off or removed. Empty outside a session.
    /// </summary>
    public IReadOnlyList<string> Profiles
    {
        get { lock (_gate) return _profiles; }
    }

    /// <summary>True while some open Edge profile has the extension switched off or removed.</summary>
    public bool IsExtensionOff => Profiles.Count > 0;

    /// <summary>
    /// Raised when <see cref="IsExtensionOff"/> changes during a session, with its
    /// new value, on a timer thread. Not raised for the reset when the session ends.
    /// </summary>
    public event EventHandler<bool>? Changed;

    private void OnSessionJoined(object? sender, SessionStartedPayload payload)
    {
        var sessionId = payload.SessionId;
        ITimer? previous;
        lock (_gate)
        {
            previous = _timer;
            _sessionId = sessionId;
            _tookStock = false;
            _profiles = Array.Empty<string>();
            _timer = _clock.CreateTimer(_ => OnTimer(sessionId), state: null, dueTime: TimeSpan.Zero, period: _interval);
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
            _profiles = Array.Empty<string>();
        }
        timer?.Dispose();
    }

    private void OnLinkCameUp(object? sender, WitnessConnection connection)
    {
        lock (_gate) _linkCameUp = _clock.GetUtcNow();
    }

    private void OnTimer(Guid sessionId)
    {
        try
        {
            Scan(sessionId);
        }
        catch (Exception ex)
        {
            // Never let a probe failure escape the timer callback.
            _log.LogWarning(ex, "Scanning the open Edge profiles threw");
        }
    }

    private void Scan(Guid sessionId)
    {
        lock (_gate)
        {
            if (_sessionId != sessionId) return;
        }

        // Outside the gate: it reads files and the desktop.
        var found = _probe.FindSwitchedOff();

        bool off;
        lock (_gate)
        {
            if (_sessionId != sessionId) return;
            if (!_tookStock)
            {
                _tookStock = true;
                return;
            }

            var was = _profiles.Count > 0;
            if (found.Count > 0 && !was && _clock.GetUtcNow() - _linkCameUp < PreferencesLag)
            {
                _log.LogDebug(
                    "Profile(s) {Profiles} read as having the extension off, but a link just came up; looking again",
                    string.Join(", ", found));
                return;
            }

            _profiles = found.ToArray();
            off = found.Count > 0;
            if (off == was) return;
        }

        if (off)
            _log.LogWarning(
                "The extension is switched off or removed in open Edge profile(s) {Profiles}", string.Join(", ", found));
        else
            _log.LogInformation("Every open Edge profile has the extension again");
        try
        {
            Changed?.Invoke(this, off);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "An open-profile Changed subscriber threw (off={Off})", off);
        }
    }

    public void Dispose()
    {
        _sessions.SessionJoined -= OnSessionJoined;
        _sessions.SessionLeft -= OnSessionLeft;
        _witness.ConnectionOpened -= OnLinkCameUp;
        ITimer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
            _sessionId = null;
            _profiles = Array.Empty<string>();
        }
        timer?.Dispose();
    }
}
