using FocusAgent.Core.Dtos;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Tamper;

/// <summary>
/// Agent-as-witness InPrivate detection (#148). While the student is in a joined
/// session, polls the open Edge windows every
/// <see cref="SessionSettings.InPrivateScanIntervalSeconds"/> and reports a
/// <see cref="TamperKinds.InPrivateOpened"/> tamper event for each InPrivate
/// window still open <see cref="SessionSettings.SchoolProfileReportSeconds"/>
/// after it first saw it (#535). This is the robust counterpart to the
/// extension's in-browser signal: the extension only witnesses InPrivate once it
/// has been *allowed* in InPrivate (design §5.4), so a student who leaves that
/// toggle off escapes it — the agent, watching from outside the browser, does
/// not. It is also the only one the teacher gets: the extension's own goes no
/// further than the agent (<see cref="FocusAgent.Core.Extension.ExtensionSessionRelay"/>).
///
/// Why the wait: since #525 an InPrivate window is paused within seconds
/// (<see cref="SchoolProfileGuard"/>), with an overlay whose button closes it, and
/// the teacher hears only what the student doesn't resolve, as for a window of
/// another profile. A window closed in time is never reported. The wait holds
/// wherever this runs, also where the school-profile rule is off (no school
/// profile found, or an extension that never connected), so no InPrivate window
/// left open goes unreported.
///
/// Same lifecycle model as <see cref="SessionHeartbeatService"/>: start the poll
/// loop on <see cref="SessionCoordinator.SessionJoined"/>, stop it on
/// <see cref="SessionCoordinator.SessionLeft"/>, so scans only run when there is
/// a joined session whose hub will accept the event.
/// </summary>
public sealed class InPrivateWitnessMonitor : IAsyncDisposable
{
    private readonly SessionCoordinator _coordinator;
    private readonly IBrowserWindowScanner _scanner;
    private readonly ITamperReporter _reporter;
    private readonly TimeProvider _clock;
    private readonly SessionSettings _settings;
    private readonly TimeSpan _reportAfter;
    private readonly string _browser;
    private readonly ILogger<InPrivateWitnessMonitor> _log;
    private readonly object _gate = new();

    private ITimer? _timer;
    private Guid? _sessionId;
    // The InPrivate windows open at the last scan, by handle, with when a scan
    // first saw each. A window that closes is forgotten, so one closed in time is
    // never reported and a reopen (even if Windows recycles the handle) waits the
    // report time anew. Cleared on session start/stop.
    private readonly Dictionary<nint, DateTimeOffset> _openSince = new();
    // Those of them already reported, so a window that stays open across many
    // polls is reported exactly once.
    private readonly HashSet<nint> _reported = new();
    private int _detectionCount;

    public InPrivateWitnessMonitor(
        SessionCoordinator coordinator,
        IBrowserWindowScanner scanner,
        ITamperReporter reporter,
        IOptions<SessionSettings> settings,
        IOptions<WitnessSettings> witnessSettings,
        TimeProvider? clock = null,
        ILogger<InPrivateWitnessMonitor>? log = null)
    {
        _coordinator = coordinator;
        _scanner = scanner;
        _reporter = reporter;
        _clock = clock ?? TimeProvider.System;
        _settings = settings.Value;
        _reportAfter = TimeSpan.FromSeconds(Math.Max(1, _settings.SchoolProfileReportSeconds));
        _browser = NormalizeProcessName(witnessSettings.Value.BrowserProcessName);
        _log = log ?? NullLogger<InPrivateWitnessMonitor>.Instance;

        _coordinator.SessionJoined += OnSessionJoined;
        _coordinator.SessionLeft += OnSessionLeft;
    }

    /// <summary>
    /// Number of InPrivate windows reported since this process started. Dev-only:
    /// surfaced on the status endpoint so the headless e2e can assert the
    /// agent-side detection fired without screenshotting a real InPrivate window.
    /// </summary>
    public int DetectionCount
    {
        get { lock (_gate) return _detectionCount; }
    }

    /// <summary>
    /// Number of InPrivate windows open at the last scan of the current session,
    /// reported or not yet; 0 outside a session. Dev-only, like
    /// <see cref="DetectionCount"/>: the e2e tells from it that a window it closes
    /// in time was seen.
    /// </summary>
    public int OpenCount
    {
        get { lock (_gate) return _openSince.Count; }
    }

    private void OnSessionJoined(object? sender, SessionStartedPayload payload) => StartFor(payload.SessionId);

    private void OnSessionLeft(object? sender, Guid sessionId) => Stop(sessionId);

    internal void StartFor(Guid sessionId)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _settings.InPrivateScanIntervalSeconds));
        ITimer? toDispose;
        lock (_gate)
        {
            toDispose = _timer;
            _sessionId = sessionId;
            _openSince.Clear();
            _reported.Clear();
            _timer = _clock.CreateTimer(
                _ => OnTimerFired(sessionId),
                state: null,
                dueTime: TimeSpan.Zero, // scan immediately so an InPrivate window already open at join is caught
                period: interval);
        }
        toDispose?.Dispose();
        _log.LogInformation(
            "InPrivateWitnessMonitor started for session {SessionId} (interval={IntervalSeconds}s, report after {ReportSeconds}s)",
            sessionId, (int)interval.TotalSeconds, (int)_reportAfter.TotalSeconds);
    }

    internal void Stop(Guid sessionId)
    {
        ITimer? toDispose;
        lock (_gate)
        {
            if (_sessionId != sessionId) return;
            toDispose = _timer;
            _timer = null;
            _sessionId = null;
            _openSince.Clear();
            _reported.Clear();
        }
        toDispose?.Dispose();
        _log.LogInformation("InPrivateWitnessMonitor stopped for session {SessionId}", sessionId);
    }

    private void OnTimerFired(Guid sessionId)
    {
        // The whole scan is best-effort: a probe or transport failure must never
        // crash the timer callback (an unhandled exception there tears the timer
        // down). Mirrors SessionHeartbeatService's swallow-and-continue posture.
        try
        {
            Scan(sessionId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "InPrivate scan threw for session {SessionId}", sessionId);
        }
    }

    private void Scan(Guid sessionId)
    {
        // Re-check under the gate: a Stop() may have observed between this tick
        // being queued and running. Snapshot the windows outside the lock — the
        // scan touches Win32 and we don't want to hold the gate across it.
        lock (_gate)
        {
            if (_sessionId != sessionId) return;
        }

        var windows = _scanner.GetOpenBrowserWindows();

        var present = windows
            .Where(w => InPrivateDetection.IsInPrivateEdgeWindow(w.ProcessName, w.Title, _browser))
            .Select(w => w.Handle)
            .ToHashSet();
        var now = _clock.GetUtcNow();

        int opened, closedInTime = 0, due = 0;
        lock (_gate)
        {
            if (_sessionId != sessionId) return;
            // Forget windows that have since closed: one closed before its report
            // was due is never reported, and a later reopen waits anew.
            foreach (var handle in _openSince.Keys.Where(h => !present.Contains(h)).ToList())
            {
                _openSince.Remove(handle);
                if (!_reported.Remove(handle)) closedInTime++;
            }
            opened = 0;
            foreach (var handle in present)
                if (_openSince.TryAdd(handle, now)) opened++;
            foreach (var (handle, since) in _openSince)
            {
                if (now - since >= _reportAfter && _reported.Add(handle))
                {
                    due++;
                    _detectionCount++;
                }
            }
        }

        if (opened > 0)
            _log.LogInformation(
                "{Count} InPrivate window(s) opened during session {SessionId}; reporting {Kind} for any still open after {Seconds}s",
                opened, sessionId, TamperKinds.InPrivateOpened, (int)_reportAfter.TotalSeconds);
        if (closedInTime > 0)
            _log.LogInformation(
                "{Count} InPrivate window(s) closed within {Seconds}s in session {SessionId}; not reported",
                closedInTime, (int)_reportAfter.TotalSeconds, sessionId);

        // One report per InPrivate window left open. They carry no per-window
        // payload (the kind is the whole signal), so a count is all we need.
        for (var i = 0; i < due; i++)
            _ = ReportAsync(sessionId);
    }

    private async Task ReportAsync(Guid sessionId)
    {
        _log.LogWarning(
            "InPrivate window still open after {Seconds}s in session {SessionId}; reporting {Kind}.",
            (int)_reportAfter.TotalSeconds, sessionId, TamperKinds.InPrivateOpened);
        try
        {
            await _reporter.ReportAsync(sessionId, TamperKinds.InPrivateOpened).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort like every other tamper report: a failed send must not
            // stop the poll loop. The next poll won't re-report this window (it's
            // already in _reported), matching the other agent-side reports — the
            // teacher sees it if it reached the backend.
            _log.LogWarning(ex, "Reporting {Kind} failed for session {SessionId}",
                TamperKinds.InPrivateOpened, sessionId);
        }
    }

    public ValueTask DisposeAsync()
    {
        _coordinator.SessionJoined -= OnSessionJoined;
        _coordinator.SessionLeft -= OnSessionLeft;
        ITimer? toDispose;
        lock (_gate)
        {
            toDispose = _timer;
            _timer = null;
            _sessionId = null;
            _openSince.Clear();
            _reported.Clear();
        }
        toDispose?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string NormalizeProcessName(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];
        return trimmed;
    }
}
