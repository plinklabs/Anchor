using FocusAgent.Core.Dtos;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Focus;

/// <summary>
/// Glue between the session lifecycle and the foreground-enforcement stack.
/// Starts the watcher on <see cref="SessionCoordinator.SessionJoined"/>,
/// stops it on <see cref="SessionCoordinator.SessionLeft"/>, classifies each
/// change against the allowlist, drives the enforcer, and reports.
/// </summary>
public sealed class FocusSessionController : IAsyncDisposable
{
    private readonly SessionCoordinator _sessions;
    private readonly IForegroundWatcher _watcher;
    private readonly IFocusEnforcer _enforcer;
    private readonly IFocusEventReporter _reporter;
    private readonly IFocusOverlay _overlay;
    private readonly SessionSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<FocusSessionController> _log;
    private readonly object _gate = new();

    private Guid? _activeSessionId;
    private AllowlistMatcher? _matcher;
    private string? _lastReportedProcessName;
    private DateTimeOffset _lastReportedAt;

    public FocusSessionController(
        SessionCoordinator sessions,
        IForegroundWatcher watcher,
        IFocusEnforcer enforcer,
        IFocusEventReporter reporter,
        IFocusOverlay overlay,
        IOptions<SessionSettings> settings,
        TimeProvider? clock = null,
        ILogger<FocusSessionController>? log = null)
    {
        _sessions = sessions;
        _watcher = watcher;
        _enforcer = enforcer;
        _reporter = reporter;
        _overlay = overlay;
        _settings = settings.Value;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<FocusSessionController>.Instance;

        _sessions.SessionJoined += OnSessionJoined;
        _sessions.SessionLeft += OnSessionLeft;
        _sessions.SessionAllowlistUpdated += OnAllowlistUpdated;
        _watcher.Changed += OnForegroundChanged;
    }

    public Guid? ActiveSessionId
    {
        get { lock (_gate) return _activeSessionId; }
    }

    /// <summary>
    /// The currently-enforced allowed-app rule values (process names / paths /
    /// publishers), or <c>null</c> when no session is active. Dev-only: lets the
    /// status endpoint surface the live matcher state so headless verify scripts
    /// can observe a mid-session allowlist rebuild (#93).
    /// </summary>
    public IReadOnlyList<string>? GetActiveAllowedApps()
    {
        lock (_gate)
        {
            if (_activeSessionId is null || _matcher is null)
                return null;
            return _matcher.UserRules.Select(r => r.Value).ToArray();
        }
    }

    private void OnSessionJoined(object? sender, SessionStartedPayload payload)
    {
        try
        {
            var rules = AllowedAppRuleMapper.FromPayload(payload.Apps);
            var matcher = new AllowlistMatcher(rules, ownProcessName: CurrentProcessName());
            lock (_gate)
            {
                _activeSessionId = payload.SessionId;
                _matcher = matcher;
                _lastReportedProcessName = null;
                _lastReportedAt = DateTimeOffset.MinValue;
            }
            _enforcer.Reset();
            _watcher.Start();
            _log.LogInformation(
                "Focus enforcement started for session {SessionId} with {AppCount} app rules / {DomainCount} domains from payload",
                payload.SessionId,
                payload.Apps.Count,
                payload.Domains.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to start focus enforcement for session {SessionId}", payload.SessionId);
        }
    }

    private void OnAllowlistUpdated(object? sender, SessionBundlesUpdatedPayload payload)
    {
        try
        {
            var rules = AllowedAppRuleMapper.FromPayload(payload.Apps);
            lock (_gate)
            {
                if (_activeSessionId != payload.SessionId)
                    return;
                _matcher = new AllowlistMatcher(rules, ownProcessName: CurrentProcessName());
                // Drop the coalesce guard so the foreground app is re-evaluated
                // against the new rules on its next event rather than being
                // suppressed as a duplicate of the last report.
                _lastReportedProcessName = null;
                _lastReportedAt = DateTimeOffset.MinValue;
            }
            _log.LogInformation(
                "Allowlist rebuilt for session {SessionId} with {AppCount} app rules / {DomainCount} domains",
                payload.SessionId,
                payload.Apps.Count,
                payload.Domains.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to rebuild allowlist for session {SessionId}", payload.SessionId);
        }
    }

    private void OnSessionLeft(object? sender, Guid sessionId)
    {
        try
        {
            lock (_gate)
            {
                if (_activeSessionId != sessionId)
                    return;
                _activeSessionId = null;
                _matcher = null;
            }
            _watcher.Stop();
            _enforcer.Reset();
            try { _overlay.Close(); }
            catch (Exception ex) { _log.LogWarning(ex, "Overlay.Close threw on session-left"); }
            _log.LogInformation("Focus enforcement stopped for session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to stop focus enforcement for session {SessionId}", sessionId);
        }
    }

    internal void OnForegroundChanged(ForegroundChange change)
    {
        Guid? sessionId;
        AllowlistMatcher? matcher;
        lock (_gate)
        {
            sessionId = _activeSessionId;
            matcher = _matcher;
        }

        if (sessionId is not Guid id || matcher is null)
            return;

        var allowed = matcher.IsAllowed(change.App);

        // Enforcement runs on EVERY foreground event, including repeats of the
        // same app inside the coalesce window. Design §5.2 requires re-checking
        // and re-minimizing an off-list window each time it returns to the
        // foreground — e.g. a student repeatedly clicking the taskbar entry of a
        // just-minimized blocked app (#92). Minimizing is idempotent, so
        // re-running it for genuine OS double-fires is harmless.
        try
        {
            if (allowed)
            {
                _enforcer.RememberAllowed(change.WindowHandle);
                try { _overlay.Hide(); }
                catch (Exception ex) { _log.LogWarning(ex, "Overlay.Hide threw"); }
            }
            else
            {
                _log.LogWarning(
                    "Blocking off-list foreground app {ProcessName} (pid={Pid}) in session {SessionId}",
                    change.App.ProcessName, change.ProcessId, id);
                var restored = _enforcer.Block(change.WindowHandle);
                if (!restored)
                {
                    _log.LogInformation(
                        "No allowed-window fallback after blocking {ProcessName}; surfacing overlay",
                        change.App.ProcessName);
                    try { _overlay.Show(matcher.UserRules, change.App.ProcessName); }
                    catch (Exception ex) { _log.LogWarning(ex, "Overlay.Show threw"); }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Enforcer threw for {ProcessName} (pid={Pid}); reporting anyway",
                change.App.ProcessName, change.ProcessId);
        }

        // Coalesce only the backend *report* — not enforcement above. Genuine OS
        // double-fires of the same app within the window (SetWinEventHook firing
        // twice for one window, rapid alt-tabs bouncing back) are reporting
        // noise; the enforcement already ran for each.
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (string.Equals(_lastReportedProcessName, change.App.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                (now - _lastReportedAt) < _settings.DuplicateCoalesceWindow)
            {
                return;
            }
            _lastReportedProcessName = change.App.ProcessName;
            _lastReportedAt = now;
        }

        _ = ReportAsync(id, change, blocked: !allowed);
    }

    private async Task ReportAsync(Guid sessionId, ForegroundChange change, bool blocked)
    {
        try
        {
            await _reporter.ReportForegroundChangeAsync(sessionId, change, blocked).ConfigureAwait(false);
            _log.LogInformation(
                "Foreground change reported: {ProcessName} blocked={Blocked} session={SessionId}",
                change.App.ProcessName, blocked, sessionId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to report foreground change for {ProcessName} in session {SessionId}",
                change.App.ProcessName, sessionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sessions.SessionJoined -= OnSessionJoined;
        _sessions.SessionLeft -= OnSessionLeft;
        _sessions.SessionAllowlistUpdated -= OnAllowlistUpdated;
        _watcher.Changed -= OnForegroundChanged;
        try { _watcher.Stop(); } catch { /* best-effort */ }
        _watcher.Dispose();
        await Task.CompletedTask;
    }

    private static string? CurrentProcessName()
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            return proc.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}
