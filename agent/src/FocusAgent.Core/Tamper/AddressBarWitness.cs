using System.Text.Json;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Extension;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Tamper;

/// <summary>
/// TEST PHASE ONLY (#515): a second witness of what Edge shows during a session
/// (#526). The extension is the only thing that sees inside Edge, and the agent
/// checks that it is alive, not that it filters; so a gap in it was a silent one
/// (the classroom test of 17 November 2026 found a student browsing unfiltered
/// with nothing reported). This reads the address bar of the foreground Edge
/// window and holds its host against the session's allowlist. An off-list page
/// the extension let through is reported to the teacher once per host and
/// session, as <see cref="Kind"/> with the host only. It reports and nothing
/// more: no pause, no block. After the pilot it goes, or stays without the site
/// (#515).
///
/// <list type="bullet">
/// <item>Only during a joined session: the timer runs from the join to the end,
/// and nothing is read outside it.</item>
/// <item>Only an ordinary window of the school profile
/// (<see cref="SchoolProfileGuard.IsSchoolProfileWindow"/>, #525): windows of
/// other profiles, Guest and InPrivate windows are paused, so there is nothing to
/// read there.</item>
/// <item>The allowlist is the one the extension was handed
/// (<see cref="ExtensionSessionRelay.Domains"/>, approved requests included),
/// matched as the extension matches it (<see cref="SiteAllowlist"/>). A host on
/// it is compared in memory and dropped: nothing is kept, logged or sent for
/// allowed sites.</item>
/// <item>Not reported: the block page, Edge's own pages, the new-tab page and a
/// local file (no http(s) page, <see cref="AddressBarHost"/>), what the student is
/// typing (the address bar has the keyboard), and a navigation the extension is
/// about to redirect: an off-list host counts only once the address bar has
/// shown it in the same window for <see cref="SessionSettings.AddressBarGraceSeconds"/>.</item>
/// </list>
///
/// UI Automation on Edge's address bar can break with an Edge update; a session
/// in which it can't be read says so once in the agent log.
/// </summary>
public sealed class AddressBarWitness : IDisposable
{
    /// <summary>
    /// TEST PHASE ONLY (#515). The event kind of the report; parses
    /// (case-insensitive) to <c>Anchor.Domain.Events.EventKind.UnfilteredSite</c>
    /// on the backend's <c>ReportEvent</c>. Its payload is
    /// <c>{"host":"…"}</c> and nothing more.
    /// </summary>
    public const string Kind = "UnfilteredSite";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SessionCoordinator _sessions;
    private readonly IAddressBarReader _reader;
    private readonly Func<nint, bool> _isSchoolProfileWindow;
    private readonly Func<IReadOnlyList<AllowedDomainDto>> _allowedDomains;
    private readonly ISessionHubConnection _hub;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _grace;
    private readonly string _browser;
    private readonly ILogger<AddressBarWitness> _log;
    private readonly object _gate = new();

    // Held for a whole scan, so two timer ticks never read at once.
    private readonly object _scanGate = new();

    private Guid? _sessionId;
    private ITimer? _timer;
    // The off-list host the address bar shows, in which window and since when,
    // while it is within its grace period. Never an allowed host.
    private (nint Window, string Host, DateTimeOffset Since)? _pending;
    // The off-list hosts reported in this session, each once.
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private bool _unreadableLogged;
    private int _reads;

    /// <param name="isSchoolProfileWindow">
    /// Whether a window is an ordinary window of the school profile:
    /// <see cref="SchoolProfileGuard.IsSchoolProfileWindow"/>.
    /// </param>
    /// <param name="allowedDomains">
    /// The domains the extension was last handed: <see cref="ExtensionSessionRelay.Domains"/>.
    /// </param>
    public AddressBarWitness(
        SessionCoordinator sessions,
        IAddressBarReader reader,
        Func<nint, bool> isSchoolProfileWindow,
        Func<IReadOnlyList<AllowedDomainDto>> allowedDomains,
        ISessionHubConnection hub,
        IOptions<SessionSettings> settings,
        IOptions<WitnessSettings> witnessSettings,
        TimeProvider? clock = null,
        ILogger<AddressBarWitness>? log = null)
    {
        _sessions = sessions;
        _reader = reader;
        _isSchoolProfileWindow = isSchoolProfileWindow;
        _allowedDomains = allowedDomains;
        _hub = hub;
        _clock = clock ?? TimeProvider.System;
        _interval = TimeSpan.FromSeconds(Math.Max(1, settings.Value.AddressBarScanSeconds));
        _grace = TimeSpan.FromSeconds(Math.Max(0, settings.Value.AddressBarGraceSeconds));
        _browser = NormalizeProcessName(witnessSettings.Value.BrowserProcessName);
        _log = log ?? NullLogger<AddressBarWitness>.Instance;

        _sessions.SessionJoined += OnSessionJoined;
        _sessions.SessionLeft += OnSessionLeft;
    }

    /// <summary>True while a session is joined, the only time the address bar is read.</summary>
    public bool Watching
    {
        get { lock (_gate) return _sessionId is not null; }
    }

    /// <summary>How many times an address bar has been read since the agent started.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>The off-list hosts reported in the current session (none outside one).</summary>
    public IReadOnlyList<string> Reported
    {
        get { lock (_gate) return _reported.Order(StringComparer.Ordinal).ToArray(); }
    }

    private void OnSessionJoined(object? sender, SessionStartedPayload payload)
    {
        var sessionId = payload.SessionId;
        ITimer? previous;
        lock (_gate)
        {
            previous = _timer;
            _sessionId = sessionId;
            ResetLocked();
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
        _pending = null;
        _reported.Clear();
        _unreadableLogged = false;
    }

    private void OnTimer(Guid sessionId)
    {
        // A read still running when the next tick comes skips that tick.
        if (!Monitor.TryEnter(_scanGate)) return;
        try
        {
            Scan(sessionId);
        }
        catch (Exception ex)
        {
            // Never let a read escape the timer callback.
            _log.LogWarning(ex, "Reading Edge's address bar threw");
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

        var window = _reader.ForegroundWindowOf(_browser);
        if (window == IntPtr.Zero || !_isSchoolProfileWindow(window))
        {
            Forget(sessionId);
            return;
        }

        var reading = _reader.Read(window);
        Interlocked.Increment(ref _reads);
        if (reading is null)
        {
            Forget(sessionId);
            LogUnreadableOnce(sessionId);
            return;
        }

        // The student may be typing: what the address bar shows is theirs, not a page.
        var host = reading.HasFocus ? null : AddressBarHost.Of(reading.Text);
        if (host is null || SiteAllowlist.Allows(_allowedDomains(), host))
        {
            Forget(sessionId);
            return;
        }

        var now = _clock.GetUtcNow();
        DateTimeOffset since;
        lock (_gate)
        {
            if (_sessionId != sessionId) return;
            if (_reported.Contains(host))
            {
                _pending = null;
                return;
            }
            // The extension redirects an off-list navigation as it starts; until the
            // grace period is over the address bar may still show where it was going.
            if (_pending is not { } pending || pending.Window != window || pending.Host != host)
            {
                _pending = (window, host, now);
                return;
            }
            if (now - pending.Since < _grace) return;
            since = pending.Since;
            _pending = null;
            _reported.Add(host);
        }

        _log.LogWarning(
            "Edge showed off-list host {Host} in the school profile for {Seconds}s without the extension blocking it; " +
            "reporting {Kind} in session {SessionId}",
            host, (int)(now - since).TotalSeconds, Kind, sessionId);
        _ = ReportAsync(sessionId, host, since);
    }

    private void Forget(Guid sessionId)
    {
        lock (_gate)
        {
            if (_sessionId == sessionId) _pending = null;
        }
    }

    private void LogUnreadableOnce(Guid sessionId)
    {
        lock (_gate)
        {
            if (_sessionId != sessionId || _unreadableLogged) return;
            _unreadableLogged = true;
        }
        _log.LogWarning(
            "Could not read the address bar of the school profile's Edge window in session {SessionId}; " +
            "Edge's UI may have changed. Said once per session.",
            sessionId);
    }

    private async Task ReportAsync(Guid sessionId, string host, DateTimeOffset seenAt)
    {
        try
        {
            // The host only (#527): no URL, path or query string leaves the device.
            await _hub.ReportEventAsync(sessionId, Kind, JsonSerializer.Serialize(new { host }, Json), seenAt)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort like the other reports: a second witness that misses one
            // is no reason to retry.
            _log.LogWarning(ex, "Reporting {Kind} failed for session {SessionId}", Kind, sessionId);
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
