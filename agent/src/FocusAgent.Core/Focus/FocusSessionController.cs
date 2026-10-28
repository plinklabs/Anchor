using FocusAgent.Core.Dtos;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using FocusAgent.Core.Tamper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Focus;

/// <summary>
/// Glue between the session lifecycle and the foreground-enforcement stack.
/// Starts the watcher on <see cref="SessionCoordinator.SessionJoined"/>,
/// stops it on <see cref="SessionCoordinator.SessionLeft"/>, classifies each
/// change against the allowlist, drives the enforcer, and reports. While the
/// <see cref="ExtensionGuard"/> has Edge paused (its extension is off, #427),
/// Edge is handled like an off-list app on top of the allowlist, and no window of
/// it is fallen back to after a block (#452).
/// </summary>
public sealed class FocusSessionController : IAsyncDisposable
{
    private readonly SessionCoordinator _sessions;
    private readonly IForegroundWatcher _watcher;
    private readonly IFocusEnforcer _enforcer;
    private readonly IWindowEnumerator _windows;
    private readonly IFocusEventReporter _reporter;
    private readonly IFocusOverlay _overlay;
    private readonly SessionSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<FocusSessionController> _log;
    private readonly ExtensionGuard? _guard;
    private readonly object _gate = new();

    private Guid? _activeSessionId;
    private AllowlistMatcher? _matcher;
    private string? _lastReportedProcessName;
    private DateTimeOffset _lastReportedAt;
    private StartupSweepResult? _lastSweep;
    // The app of the window the enforcer falls back to after a block, or null
    // when it has none: a window of the browser is no fallback while the guard
    // has it paused (#452).
    private AppInfo? _fallbackApp;

    public FocusSessionController(
        SessionCoordinator sessions,
        IForegroundWatcher watcher,
        IFocusEnforcer enforcer,
        IWindowEnumerator windows,
        IFocusEventReporter reporter,
        IFocusOverlay overlay,
        IOptions<SessionSettings> settings,
        TimeProvider? clock = null,
        ILogger<FocusSessionController>? log = null,
        ExtensionGuard? guard = null)
    {
        _sessions = sessions;
        _watcher = watcher;
        _enforcer = enforcer;
        _windows = windows;
        _reporter = reporter;
        _overlay = overlay;
        _settings = settings.Value;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<FocusSessionController>.Instance;
        _guard = guard;

        _sessions.SessionJoined += OnSessionJoined;
        _sessions.SessionLeft += OnSessionLeft;
        _sessions.SessionAllowlistUpdated += OnAllowlistUpdated;
        _watcher.Changed += OnForegroundChanged;
        if (_guard is not null)
            _guard.BrowserPaused += OnBrowserPaused;
    }

    /// <summary>
    /// The student's "turn the extension back on" action on the paused-Edge
    /// overlay (#427): opens Edge for the guard's repair window. Returns false
    /// when Edge isn't paused. Also driven by the dev status endpoint so the
    /// headless e2e can take the action without clicking the overlay.
    /// </summary>
    public bool TurnExtensionBackOn() => _guard?.BeginRepair() ?? false;

    public Guid? ActiveSessionId
    {
        get { lock (_gate) return _activeSessionId; }
    }

    /// <summary>
    /// Result of the most recent session-start window sweep (#104), or
    /// <c>null</c> before the first session of this process. Dev-only: lets the
    /// status endpoint surface what the sweep minimized so the headless e2e can
    /// assert it ran instead of screenshotting the desktop.
    /// </summary>
    public StartupSweepResult? GetLastStartupSweep()
    {
        lock (_gate)
            return _lastSweep;
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
                _fallbackApp = null;
            }
            _enforcer.Reset();
            _watcher.Start();
            _log.LogInformation(
                "Focus enforcement started for session {SessionId} with {AppCount} app rules / {DomainCount} domains from payload",
                payload.SessionId,
                payload.Apps.Count,
                payload.Domains.Count);

            // #104: the foreground hook only reacts to focus *changes*, so an
            // off-list app already foregrounded when the session began would
            // never be enforced against until the student alt-tabbed. Sweep the
            // currently-open windows now and minimize the off-list ones.
            SweepOpenWindows(matcher);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to start focus enforcement for session {SessionId}", payload.SessionId);
        }
    }

    /// <summary>
    /// Minimizes every off-list top-level window open at session start, applying
    /// the same precedence as the foreground hook: OS shell surfaces are left
    /// alone (#140), allowed apps stay up, everything else is minimized. Records
    /// the outcome for <see cref="GetLastStartupSweep"/>. Best-effort — a failure
    /// to enumerate or minimize one window must not abort the session start.
    /// </summary>
    private void SweepOpenWindows(AllowlistMatcher matcher)
    {
        IReadOnlyList<OpenWindow> open;
        try
        {
            open = _windows.GetOpenWindows();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Session-start sweep failed to enumerate open windows");
            return;
        }

        var minimized = new List<string>();
        foreach (var window in open)
        {
            if (AllowlistMatcher.IsSystemSurface(window.App))
                continue;
            if (matcher.IsAllowed(window.App))
                continue;
            try
            {
                _enforcer.Minimize(window.Handle);
                minimized.Add(window.App.ProcessName);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Session-start sweep failed to minimize off-list window for {ProcessName}",
                    window.App.ProcessName);
            }
        }

        lock (_gate)
            _lastSweep = new StartupSweepResult(open.Count, minimized);

        _log.LogInformation(
            "Session-start sweep examined {Examined} window(s) and minimized {Minimized} off-list window(s)",
            open.Count, minimized.Count);
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
                _fallbackApp = null;
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

    /// <summary>
    /// The guard just paused Edge (#427). The foreground hook only fires on focus
    /// changes, so an Edge window already on screen — typically the one the
    /// student switched the extension off in — would stay usable until they
    /// clicked away. Take every open Edge window down now and tell the student
    /// what happened. The one with the foreground goes to the overlay, which takes
    /// the keyboard over from it (#450): minimized from the agent, it kept what the
    /// student typed, or handed the foreground to whatever window was below it, an
    /// allowed app that then hid the overlay. The others have no keyboard to hand
    /// on and are minimized like the session-start sweep does for off-list apps.
    /// </summary>
    private void OnBrowserPaused(object? sender, EventArgs e)
    {
        Guid? sessionId;
        AllowlistMatcher? matcher;
        lock (_gate)
        {
            sessionId = _activeSessionId;
            matcher = _matcher;
        }
        if (sessionId is null || matcher is null || _guard is null)
            return;

        IReadOnlyList<OpenWindow> open;
        try
        {
            open = _windows.GetOpenWindows();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to enumerate open windows to pause the browser");
            open = Array.Empty<OpenWindow>();
        }

        var paused = 0;
        var handedOver = IntPtr.Zero;
        foreach (var window in open)
        {
            if (!_guard.IsBrowser(window.App))
                continue;
            try
            {
                var foreground = handedOver == IntPtr.Zero ? _enforcer.ForegroundWindowOf(window.Handle) : IntPtr.Zero;
                if (foreground != IntPtr.Zero)
                    handedOver = foreground;
                else
                    _enforcer.Minimize(window.Handle);
                paused++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to minimize a {ProcessName} window to pause it", window.App.ProcessName);
            }
        }

        if (handedOver == IntPtr.Zero)
            _log.LogWarning(
                "Paused the browser in session {SessionId}: minimized {Paused} window(s) until its extension reconnects",
                sessionId, paused);
        else
            _log.LogWarning(
                "Paused the browser in session {SessionId}: taking {Paused} window(s) down until its extension reconnects; the overlay takes the keyboard over from 0x{Foreground:X}",
                sessionId, paused, handedOver);
        ShowBrowserPausedOverlay(_guard, matcher, handedOver, browserWindow: true);
    }

    /// <summary>
    /// Tells the student Edge is paused. <paramref name="pausedWindow"/>, an Edge
    /// window that has the foreground (#450, <paramref name="browserWindow"/>) or an
    /// app blocked while Edge is paused (#452), goes to the overlay, which takes it
    /// down and the keyboard over from it; it is minimized without the overlay when
    /// the overlay cannot come up.
    /// </summary>
    private void ShowBrowserPausedOverlay(
        ExtensionGuard guard, AllowlistMatcher matcher, nint pausedWindow, bool browserWindow)
    {
        // A paused Edge is no way back, so leave it out of the allowed-app rows;
        // the prompt's own action is how the student gets Edge again.
        var others = matcher.UserRules.Where(r => !guard.IsBrowserRule(r)).ToArray();
        var prompt = new BrowserPausedPrompt(guard.BrowserRule, guard.RepairWindow, TurnExtensionBackOn);
        // Edge goes down only while the pause lasts. The overlay gets to this show,
        // and to taking the window down, a moment later, behind the shows asked for
        // before it: by then the extension may be back, or the repair window open,
        // and a minimize would take down the Edge the student just got back (#474).
        // An app blocked while Edge is paused goes down regardless.
        Func<bool>? stillPaused = browserWindow ? () => guard.IsBrowserPaused : null;
        try { _overlay.ShowBrowserPaused(others, prompt, pausedWindow, stillPaused); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Overlay.ShowBrowserPaused threw; minimizing the window without it");
            if (pausedWindow != IntPtr.Zero)
                _enforcer.Minimize(pausedWindow);
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

        // #428: a dialog another process shows for an app's window belongs to that
        // app. Edge's upload button opens the Windows file picker in PickerHost,
        // owned by the Edge window: judged by its own process it was blocked, so
        // no upload could be done. Judge, enforce and report it as the app that
        // owns it instead: an allowed app's picker stays usable, while a picker
        // opened by an off-list app (or by a paused Edge) is handled like that app.
        var app = change.OwningApp;

        // OS shell surfaces (taskbar Search, Start, the touch keyboard, …) are
        // neither allowed nor blocked: minimizing them and stealing foreground
        // blanks Search/Start and can restart the taskbar (#140). Bail before the
        // allowed/blocked branch so we don't enforce, report, or remember one as
        // the fallback window — leaving _lastAllowed pointing at the real app the
        // student returns to when the transient shell surface closes. A dialog a
        // shell surface owns is left alone with it: taking the dialog down would
        // minimize the surface that owns it.
        if (AllowlistMatcher.IsSystemSurface(change.App) || AllowlistMatcher.IsSystemSurface(app))
        {
            _log.LogDebug(
                "Ignoring OS shell surface {ProcessName} (owner {OwnerProcessName}, pid={Pid}) in session {SessionId}; not enforcing or reporting",
                change.App.ProcessName, app.ProcessName, change.ProcessId, id);
            return;
        }

        if (change.Owner is { } owner)
            _log.LogDebug(
                "Judging a {ProcessName} window (pid={Pid}) as the {OwnerProcessName} window that owns it (pid={OwnerPid})",
                change.App.ProcessName, change.ProcessId, owner.App.ProcessName, owner.ProcessId);

        // #427: while the extension is off, Edge is an unfiltered browser, so the
        // guard pauses it on top of whatever the allowlist says — dialogs Edge owns
        // included, so a picker is no way around the pause. Otherwise an Edge
        // coming forward tells a guard that's waiting for Edge to relaunch that the
        // extension's grace period can start now.
        ExtensionGuard? pausedBy = null;
        if (_guard is { } guard && guard.IsBrowser(app))
        {
            if (guard.IsBrowserPaused)
                pausedBy = guard;
            else
                guard.NoteBrowserForeground();
        }
        var browserPaused = pausedBy is not null;

        var allowed = !browserPaused && matcher.IsAllowed(app);

        // Enforcement runs on EVERY foreground event, including repeats of the
        // same app inside the coalesce window. Design §5.2 requires re-checking
        // and re-minimizing an off-list window each time it returns to the
        // foreground — e.g. a student repeatedly clicking the taskbar entry of a
        // just-minimized blocked app (#92). Minimizing is idempotent, so
        // re-running it for genuine OS double-fires is harmless. The enforcer takes
        // a dialog down together with the app window that owns it (#428).
        try
        {
            if (allowed)
            {
                // The enforcer skips explorer's shell windows here (#437): a click
                // on the allowed taskbar leaves the student's own app the fallback.
                // Which app the fallback is matters once the browser is paused (#452).
                if (_enforcer.RememberAllowed(change.WindowHandle))
                {
                    lock (_gate)
                        _fallbackApp = app;
                }
                try { _overlay.Hide(); }
                catch (Exception ex) { _log.LogWarning(ex, "Overlay.Hide threw"); }
            }
            else if (pausedBy is { } pausing)
            {
                // Don't hand focus back to the last allowed app: the overlay must
                // stay up, because it is the only place the student learns why Edge
                // went away and how to switch the extension back on. The overlay takes
                // the window down itself and the keyboard over from it (#450), as it
                // does for a blocked app with nothing to fall back to (#441).
                _log.LogWarning(
                    "Pausing {ProcessName} (pid={Pid}) in session {SessionId}: its extension is off",
                    app.ProcessName, change.ProcessId, id);
                ShowBrowserPausedOverlay(pausing, matcher, change.WindowHandle, browserWindow: true);
            }
            else
            {
                _log.LogWarning(
                    "Blocking off-list foreground app {ProcessName} (pid={Pid}) in session {SessionId}",
                    app.ProcessName, change.ProcessId, id);
                // #452: a window of a paused browser is no place to send the student
                // back to. Restored as the fallback, it came back on screen and took
                // the foreground until its own events paused it again: Edge flashed
                // up, and what the student typed meanwhile went to it.
                var pauseGuard = _guard is { IsBrowserPaused: true } pausingGuard ? pausingGuard : null;
                var fallbackPaused = pauseGuard is not null && FallbackIsBrowserOf(pauseGuard);
                var restored = !fallbackPaused && _enforcer.Block(change.WindowHandle);
                if (!restored && pauseGuard is not null)
                {
                    // While Edge is paused the overlay is the paused-browser one: it
                    // says why Edge is gone and how to get it back, and does not offer
                    // the paused Edge as a way back, as the blocked-app overlay's rows
                    // would. It takes the window down and the foreground over from it
                    // all the same (#441, #450).
                    if (fallbackPaused)
                        _log.LogInformation(
                            "Not falling back to the paused {Browser} after blocking {ProcessName}; surfacing the paused-browser overlay",
                            pauseGuard.BrowserRule.Value, app.ProcessName);
                    else
                        _log.LogInformation(
                            "No allowed-window fallback after blocking {ProcessName} while {Browser} is paused; surfacing the paused-browser overlay",
                            app.ProcessName, pauseGuard.BrowserRule.Value);
                    ShowBrowserPausedOverlay(pauseGuard, matcher, change.WindowHandle, browserWindow: false);
                }
                else if (!restored)
                {
                    // #441: the overlay takes the window down itself and the
                    // foreground over from it, so what the student types goes to
                    // the overlay rather than to the minimized app.
                    _log.LogInformation(
                        "No allowed-window fallback after blocking {ProcessName}; surfacing overlay",
                        app.ProcessName);
                    try { _overlay.Show(matcher.UserRules, app.ProcessName, change.WindowHandle); }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Overlay.Show threw; minimizing {ProcessName} without it", app.ProcessName);
                        _enforcer.Minimize(change.WindowHandle);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Enforcer threw for {ProcessName} (pid={Pid}); reporting anyway",
                app.ProcessName, change.ProcessId);
        }

        // Coalesce only the backend *report* — not enforcement above. Genuine OS
        // double-fires of the same app within the window (SetWinEventHook firing
        // twice for one window, rapid alt-tabs bouncing back) are reporting
        // noise; the enforcement already ran for each. An app's picker coming up
        // right after the app is the same app, so it coalesces too.
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (string.Equals(_lastReportedProcessName, app.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                (now - _lastReportedAt) < _settings.DuplicateCoalesceWindow)
            {
                return;
            }
            _lastReportedProcessName = app.ProcessName;
            _lastReportedAt = now;
        }

        _ = ReportAsync(id, change, blocked: !allowed);
    }

    /// <summary>
    /// Whether the window the enforcer falls back to after a block is a window of
    /// the browser <paramref name="guard"/> guards (#452). It was remembered while
    /// the browser was allowed, before the pause; a window remembered since is
    /// another allowed app's.
    /// </summary>
    private bool FallbackIsBrowserOf(ExtensionGuard guard)
    {
        AppInfo? fallback;
        lock (_gate)
            fallback = _fallbackApp;
        return fallback is not null && guard.IsBrowser(fallback);
    }

    private async Task ReportAsync(Guid sessionId, ForegroundChange change, bool blocked)
    {
        try
        {
            await _reporter.ReportForegroundChangeAsync(sessionId, change, blocked).ConfigureAwait(false);
            _log.LogInformation(
                "Foreground change reported: {ProcessName} blocked={Blocked} session={SessionId}",
                change.OwningApp.ProcessName, blocked, sessionId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to report foreground change for {ProcessName} in session {SessionId}",
                change.OwningApp.ProcessName, sessionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sessions.SessionJoined -= OnSessionJoined;
        _sessions.SessionLeft -= OnSessionLeft;
        _sessions.SessionAllowlistUpdated -= OnAllowlistUpdated;
        _watcher.Changed -= OnForegroundChanged;
        if (_guard is not null)
            _guard.BrowserPaused -= OnBrowserPaused;
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
