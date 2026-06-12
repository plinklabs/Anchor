using FocusAgent.Core.Dtos;
using FocusAgent.Core.Focus;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FocusAgent.Core.Tests;

public class FocusSessionControllerTests
{
    [Fact]
    public async Task Watcher_starts_on_join_and_stops_on_leave()
    {
        var (controller, fixtures) = BuildController();
        var payload = NewPayload();

        await fixtures.Hub.RaiseSessionStarted(payload);

        Assert.True(fixtures.Watcher.IsRunning);
        Assert.Equal(payload.SessionId, controller.ActiveSessionId);

        fixtures.Hub.RaiseSessionEnded(payload.SessionId);

        Assert.False(fixtures.Watcher.IsRunning);
        Assert.Null(controller.ActiveSessionId);
    }

    [Fact]
    public async Task Watcher_does_not_start_when_student_declines()
    {
        var fixtures = new Fixtures { UiDecision = JoinDecision.Declined };
        var (_, _) = BuildController(fixtures);
        var payload = NewPayload();

        await fixtures.Hub.RaiseSessionStarted(payload);

        Assert.False(fixtures.Watcher.IsRunning);
    }

    [Fact]
    public async Task Allowed_foreground_change_is_remembered_and_reported_as_unblocked()
    {
        var fixtures = new Fixtures();
        var (_, _) = BuildController(fixtures);
        var payload = NewPayload(apps: new[]
        {
            new AllowedAppDto("ProcessName", "winword"),
        });
        await fixtures.Hub.RaiseSessionStarted(payload);

        var change = ForegroundFor("winword", hwnd: 0x100);
        fixtures.Watcher.Raise(change);

        Assert.Empty(fixtures.Enforcer.Blocked);
        Assert.Equal((nint)0x100, Assert.Single(fixtures.Enforcer.Remembered));
        var reported = Assert.Single(fixtures.Reporter.Reports);
        Assert.Equal(payload.SessionId, reported.SessionId);
        Assert.False(reported.Blocked);
        Assert.Equal("winword", reported.Change.App.ProcessName);
    }

    [Fact]
    public async Task Disallowed_foreground_change_is_blocked_and_reported_as_blocked()
    {
        var fixtures = new Fixtures();
        var (_, _) = BuildController(fixtures);
        await fixtures.Hub.RaiseSessionStarted(NewPayload());

        fixtures.Watcher.Raise(ForegroundFor("notepad", hwnd: 0x200));

        var blocked = Assert.Single(fixtures.Enforcer.Blocked);
        Assert.Equal((nint)0x200, blocked);
        var reported = Assert.Single(fixtures.Reporter.Reports);
        Assert.True(reported.Blocked);
    }

    [Fact]
    public async Task System_shell_surface_is_neither_enforced_nor_reported()
    {
        // #140: foregrounding a Windows shell surface (taskbar Search, Start, the
        // touch keyboard, …) must not minimize it, steal foreground, surface the
        // overlay, or report it as activity — doing so blanks Search/Start and can
        // cascade into an explorer taskbar restart. Without the fix SearchHost is
        // off-list, so it would be blocked, reported, and the overlay shown.
        var fixtures = new Fixtures();
        var (_, _) = BuildController(fixtures);
        await fixtures.Hub.RaiseSessionStarted(NewPayload(apps: new[]
        {
            new AllowedAppDto("ProcessName", "winword"),
        }));

        // Student is on an allowed app, then clicks taskbar Search.
        fixtures.Watcher.Raise(ForegroundFor("winword", hwnd: 0x100));
        fixtures.Clock.Advance(TimeSpan.FromMilliseconds(50));
        fixtures.Watcher.Raise(ForegroundFor("SearchHost", hwnd: 0x900));

        // The surface itself is never minimized, overlaid, or reported.
        Assert.Empty(fixtures.Enforcer.Blocked);
        Assert.Empty(fixtures.Overlay.Shown);
        Assert.DoesNotContain(fixtures.Reporter.Reports, r => r.Change.App.ProcessName == "SearchHost");
        // The allowed app stays the remembered fallback — the surface didn't
        // overwrite _lastAllowed.
        Assert.Equal(new[] { (nint)0x100 }, fixtures.Enforcer.Remembered);
    }

    [Fact]
    public async Task Edge_is_unblocked_when_payload_carries_it_as_baseline()
    {
        // Baseline lives on the backend post-#70 — the agent's matcher no
        // longer has a built-in Edge entry. The payload's Apps list carries
        // it instead. This test pins that the wiring still treats Edge as
        // allowed once it's present on the wire.
        var fixtures = new Fixtures();
        var (_, _) = BuildController(fixtures);
        await fixtures.Hub.RaiseSessionStarted(NewPayload(apps: new[]
        {
            new AllowedAppDto("ProcessName", "msedge"),
        }));

        fixtures.Watcher.Raise(ForegroundFor("msedge", hwnd: 0x300));

        Assert.Empty(fixtures.Enforcer.Blocked);
        var reported = Assert.Single(fixtures.Reporter.Reports);
        Assert.False(reported.Blocked);
    }

    [Fact]
    public async Task Edge_is_blocked_when_payload_does_not_carry_it()
    {
        // Inverse of the above: if the backend ever omits the baseline
        // (misconfiguration or empty selection), the agent doesn't
        // silently re-add it. The matcher honours exactly what the
        // payload says.
        var fixtures = new Fixtures();
        var (_, _) = BuildController(fixtures);
        await fixtures.Hub.RaiseSessionStarted(NewPayload());

        fixtures.Watcher.Raise(ForegroundFor("msedge", hwnd: 0x300));

        Assert.Single(fixtures.Enforcer.Blocked);
        var reported = Assert.Single(fixtures.Reporter.Reports);
        Assert.True(reported.Blocked);
    }

    [Fact]
    public async Task Duplicate_foreground_change_is_coalesced_within_window()
    {
        var fixtures = new Fixtures();
        var (_, _) = BuildController(fixtures);
        await fixtures.Hub.RaiseSessionStarted(NewPayload(apps: new[]
        {
            new AllowedAppDto("ProcessName", "winword"),
        }));

        fixtures.Watcher.Raise(ForegroundFor("winword", hwnd: 0x100));
        fixtures.Clock.Advance(TimeSpan.FromMilliseconds(100));
        fixtures.Watcher.Raise(ForegroundFor("winword", hwnd: 0x100));
        fixtures.Clock.Advance(TimeSpan.FromMilliseconds(100));
        fixtures.Watcher.Raise(ForegroundFor("winword", hwnd: 0x100));

        Assert.Single(fixtures.Reporter.Reports);
    }

    [Fact]
    public async Task Blocked_app_is_reminimized_on_every_reactivation_within_window()
    {
        // #92: a student who restores a just-minimized off-list window (e.g. by
        // clicking its taskbar entry) within the coalesce window must see it
        // re-minimized every time. Per design §5.2 enforcement runs on every
        // foreground event; only the backend report is coalesced.
        var fixtures = new Fixtures();
        var (_, _) = BuildController(fixtures);
        await fixtures.Hub.RaiseSessionStarted(NewPayload(apps: new[]
        {
            new AllowedAppDto("ProcessName", "winword"),
        }));

        fixtures.Watcher.Raise(ForegroundFor("notepad", hwnd: 0x200));
        fixtures.Clock.Advance(TimeSpan.FromMilliseconds(100));
        fixtures.Watcher.Raise(ForegroundFor("notepad", hwnd: 0x200));
        fixtures.Clock.Advance(TimeSpan.FromMilliseconds(100));
        fixtures.Watcher.Raise(ForegroundFor("notepad", hwnd: 0x200));

        // Re-minimized on every event (design §5.2)...
        Assert.Equal(new[] { (nint)0x200, (nint)0x200, (nint)0x200 }, fixtures.Enforcer.Blocked);
        // ...while the backend report stays coalesced to one within the window.
        Assert.Single(fixtures.Reporter.Reports);
    }

    [Fact]
    public async Task Distinct_apps_are_each_reported()
    {
        var fixtures = new Fixtures();
        var (_, _) = BuildController(fixtures);
        await fixtures.Hub.RaiseSessionStarted(NewPayload(apps: new[]
        {
            new AllowedAppDto("ProcessName", "winword"),
        }));

        fixtures.Watcher.Raise(ForegroundFor("winword", hwnd: 0x100));
        fixtures.Clock.Advance(TimeSpan.FromMilliseconds(50));
        fixtures.Watcher.Raise(ForegroundFor("notepad", hwnd: 0x200));

        Assert.Equal(2, fixtures.Reporter.Reports.Count);
    }

    [Fact]
    public async Task Foreground_changes_outside_session_are_ignored()
    {
        var fixtures = new Fixtures();
        var (_, _) = BuildController(fixtures);

        fixtures.Watcher.Raise(ForegroundFor("notepad", hwnd: 0x100));

        Assert.Empty(fixtures.Enforcer.Blocked);
        Assert.Empty(fixtures.Reporter.Reports);
    }

    [Fact]
    public async Task Overlay_is_shown_when_block_has_no_fallback()
    {
        var fixtures = new Fixtures();
        fixtures.Enforcer.BlockRestoresFallback = false;
        var (_, _) = BuildController(fixtures);
        await fixtures.Hub.RaiseSessionStarted(NewPayload(apps: new[]
        {
            new AllowedAppDto("ProcessName", "winword"),
        }));

        fixtures.Watcher.Raise(ForegroundFor("notepad", hwnd: 0x200));

        var shown = Assert.Single(fixtures.Overlay.Shown);
        Assert.Equal("notepad", shown.BlockedAppName);
        // Whatever the payload carries — baseline-merged on the backend or
        // teacher-picked — flows through to the overlay's allowed-apps list.
        Assert.Equal(new[] { "winword" }, shown.Rules.Select(r => r.Value).ToArray());
        Assert.Equal(0, fixtures.Overlay.HideCount);
    }

    [Fact]
    public async Task Overlay_is_not_shown_when_block_restores_fallback()
    {
        var fixtures = new Fixtures();
        fixtures.Enforcer.BlockRestoresFallback = true;
        var (_, _) = BuildController(fixtures);
        await fixtures.Hub.RaiseSessionStarted(NewPayload());

        fixtures.Watcher.Raise(ForegroundFor("notepad", hwnd: 0x200));

        Assert.Empty(fixtures.Overlay.Shown);
    }

    [Fact]
    public async Task Overlay_is_hidden_when_allowed_foreground_change_arrives()
    {
        var fixtures = new Fixtures();
        var (_, _) = BuildController(fixtures);
        await fixtures.Hub.RaiseSessionStarted(NewPayload(apps: new[]
        {
            new AllowedAppDto("ProcessName", "winword"),
        }));

        fixtures.Watcher.Raise(ForegroundFor("winword", hwnd: 0x100));

        Assert.Equal(1, fixtures.Overlay.HideCount);
        Assert.Empty(fixtures.Overlay.Shown);
    }

    [Fact]
    public async Task Overlay_is_closed_on_session_end()
    {
        var fixtures = new Fixtures();
        var (_, _) = BuildController(fixtures);
        var payload = NewPayload();
        await fixtures.Hub.RaiseSessionStarted(payload);

        fixtures.Hub.RaiseSessionEnded(payload.SessionId);

        Assert.Equal(1, fixtures.Overlay.CloseCount);
    }

    [Fact]
    public async Task After_leave_subsequent_foreground_changes_are_ignored()
    {
        var fixtures = new Fixtures();
        var (_, _) = BuildController(fixtures);
        var payload = NewPayload();
        await fixtures.Hub.RaiseSessionStarted(payload);
        fixtures.Hub.RaiseSessionEnded(payload.SessionId);

        fixtures.Watcher.Raise(ForegroundFor("notepad", hwnd: 0x500));

        Assert.Empty(fixtures.Enforcer.Blocked);
        Assert.Empty(fixtures.Reporter.Reports);
    }

    [Fact]
    public async Task Bundles_update_for_active_session_rebuilds_matcher()
    {
        var fixtures = new Fixtures();
        var (_, _) = BuildController(fixtures);
        var payload = NewPayload(apps: new[]
        {
            new AllowedAppDto("ProcessName", "winword"),
        });
        await fixtures.Hub.RaiseSessionStarted(payload);

        // winword allowed under the initial bundles.
        fixtures.Watcher.Raise(ForegroundFor("winword", hwnd: 0x100));
        Assert.Empty(fixtures.Enforcer.Blocked);

        // Teacher swaps bundles: now only notepad is allowed.
        fixtures.Hub.RaiseBundlesUpdated(new SessionBundlesUpdatedPayload(
            payload.SessionId,
            new[] { new AllowedAppDto("ProcessName", "notepad") },
            Array.Empty<AllowedDomainDto>()));

        // notepad is now allowed, winword is now blocked.
        fixtures.Watcher.Raise(ForegroundFor("notepad", hwnd: 0x200));
        Assert.Empty(fixtures.Enforcer.Blocked);

        fixtures.Watcher.Raise(ForegroundFor("winword", hwnd: 0x300));
        var blocked = Assert.Single(fixtures.Enforcer.Blocked);
        Assert.Equal((nint)0x300, blocked);
    }

    [Fact]
    public async Task Bundles_update_for_other_session_is_ignored()
    {
        var fixtures = new Fixtures();
        var (_, _) = BuildController(fixtures);
        var payload = NewPayload(apps: new[]
        {
            new AllowedAppDto("ProcessName", "winword"),
        });
        await fixtures.Hub.RaiseSessionStarted(payload);

        // Update for a different session must not touch the active matcher.
        fixtures.Hub.RaiseBundlesUpdated(new SessionBundlesUpdatedPayload(
            Guid.NewGuid(),
            new[] { new AllowedAppDto("ProcessName", "notepad") },
            Array.Empty<AllowedDomainDto>()));

        fixtures.Watcher.Raise(ForegroundFor("winword", hwnd: 0x100));
        Assert.Empty(fixtures.Enforcer.Blocked);
    }

    [Fact]
    public async Task Session_start_sweep_minimizes_only_off_list_open_windows()
    {
        // #104: windows already open when the session starts must be swept —
        // off-list ones minimized, allowed apps left up, and OS shell surfaces
        // (#140) untouched just like the live foreground path.
        var fixtures = new Fixtures();
        fixtures.Windows.Windows.AddRange(new[]
        {
            OpenWindowFor("winword", hwnd: 0x10),     // allowed
            OpenWindowFor("notepad", hwnd: 0x20),     // off-list
            OpenWindowFor("SearchHost", hwnd: 0x30),  // OS shell surface
            OpenWindowFor("calc", hwnd: 0x40),        // off-list
        });
        var (_, _) = BuildController(fixtures);

        await fixtures.Hub.RaiseSessionStarted(NewPayload(apps: new[]
        {
            new AllowedAppDto("ProcessName", "winword"),
        }));

        // Only the off-list windows are minimized, in enumeration order.
        Assert.Equal(new[] { (nint)0x20, (nint)0x40 }, fixtures.Enforcer.Minimized);
        // The sweep never routes through Block — that path is for live
        // foreground changes and would fight focus per window.
        Assert.Empty(fixtures.Enforcer.Blocked);
    }

    [Fact]
    public async Task Session_start_sweep_leaves_baseline_edge_up_and_minimizes_the_rest()
    {
        // AC: a session starting with no bundles minimizes everything except the
        // baseline (Edge), which the backend carries in the payload's Apps list.
        var fixtures = new Fixtures();
        fixtures.Windows.Windows.AddRange(new[]
        {
            OpenWindowFor("msedge", hwnd: 0x10),                    // baseline-allowed
            OpenWindowFor("notepad", hwnd: 0x20),                  // off-list
            OpenWindowFor("StartMenuExperienceHost", hwnd: 0x30),  // OS shell surface
        });
        var (_, _) = BuildController(fixtures);

        await fixtures.Hub.RaiseSessionStarted(NewPayload(apps: new[]
        {
            new AllowedAppDto("ProcessName", "msedge"),
        }));

        Assert.Equal(new[] { (nint)0x20 }, fixtures.Enforcer.Minimized);
    }

    [Fact]
    public async Task Session_start_sweep_result_is_recorded_for_status()
    {
        var fixtures = new Fixtures();
        fixtures.Windows.Windows.AddRange(new[]
        {
            OpenWindowFor("winword", hwnd: 0x10),
            OpenWindowFor("notepad", hwnd: 0x20),
            OpenWindowFor("calc", hwnd: 0x40),
        });
        var (controller, _) = BuildController(fixtures);

        Assert.Null(controller.GetLastStartupSweep());

        await fixtures.Hub.RaiseSessionStarted(NewPayload(apps: new[]
        {
            new AllowedAppDto("ProcessName", "winword"),
        }));

        var sweep = controller.GetLastStartupSweep();
        Assert.NotNull(sweep);
        Assert.Equal(3, sweep!.WindowsExamined);
        Assert.Equal(new[] { "notepad", "calc" }, sweep.MinimizedProcesses);
    }

    [Fact]
    public async Task Session_start_sweep_does_not_run_when_student_declines()
    {
        // No join → no enforcement → no sweep: a declined toast must never
        // minimize the student's windows.
        var fixtures = new Fixtures { UiDecision = JoinDecision.Declined };
        fixtures.Windows.Windows.Add(OpenWindowFor("notepad", hwnd: 0x20));
        var (controller, _) = BuildController(fixtures);

        await fixtures.Hub.RaiseSessionStarted(NewPayload());

        Assert.Empty(fixtures.Enforcer.Minimized);
        Assert.Null(controller.GetLastStartupSweep());
    }

    private static OpenWindow OpenWindowFor(string process, nint hwnd, string? exePath = null, string? publisher = null) =>
        new(hwnd, new AppInfo(process, exePath, publisher));

    private static SessionStartedPayload NewPayload(Guid? id = null, IReadOnlyList<AllowedAppDto>? apps = null) => new(
        SessionId: id ?? Guid.NewGuid(),
        ClassId: Guid.NewGuid(),
        StartedAt: DateTimeOffset.UnixEpoch,
        JoinCode: "123456",
        Apps: apps ?? Array.Empty<AllowedAppDto>(),
        Domains: Array.Empty<AllowedDomainDto>());

    private static ForegroundChange ForegroundFor(string process, nint hwnd, string? exePath = null, string? publisher = null, int pid = 4242) =>
        new(new AppInfo(process, exePath, publisher), WindowTitle: process, ProcessId: pid, WindowHandle: hwnd);

    private static (FocusSessionController, Fixtures) BuildController(Fixtures? supplied = null)
    {
        var fixtures = supplied ?? new Fixtures();
        var settings = Options.Create(new SessionSettings
        {
            DuplicateCoalesceWindow = TimeSpan.FromMilliseconds(500),
        });
        var coordinator = new SessionCoordinator(
            fixtures.Hub,
            new NoopUi { Decision = fixtures.UiDecision },
            Options.Create(new RealtimeSettings { JoinConfirmationDuration = TimeSpan.FromMilliseconds(1) }),
            fixtures.Clock);
        var controller = new FocusSessionController(
            coordinator,
            fixtures.Watcher,
            fixtures.Enforcer,
            fixtures.Windows,
            fixtures.Reporter,
            fixtures.Overlay,
            settings,
            fixtures.Clock);
        fixtures.Coordinator = coordinator;
        return (controller, fixtures);
    }

    private sealed class Fixtures
    {
        public FakeHub Hub { get; } = new();
        public FakeForegroundWatcher Watcher { get; } = new();
        public RecordingEnforcer Enforcer { get; } = new();
        public FakeWindowEnumerator Windows { get; } = new();
        public RecordingReporter Reporter { get; } = new();
        public RecordingOverlay Overlay { get; } = new();
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public JoinDecision UiDecision { get; set; } = JoinDecision.Confirmed;
        public SessionCoordinator? Coordinator { get; set; }
    }

    private sealed class FakeHub : ISessionHubConnection
    {
        public AgentConnectionState State => AgentConnectionState.Connected;
#pragma warning disable CS0067
        public event EventHandler<AgentConnectionState>? StateChanged;
#pragma warning restore CS0067
        public event EventHandler<SessionStartedPayload>? SessionStarted;
        public event EventHandler<Guid>? SessionEnded;
        public event EventHandler<SessionBundlesUpdatedPayload>? SessionBundlesUpdated;
        public List<(Guid SessionId, string Kind, string PayloadJson, DateTimeOffset? At)> Reports { get; } = new();

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task JoinSessionAsync(Guid sessionId, string? joinCode, CancellationToken ct = default) => Task.CompletedTask;
        public Task LeaveSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclineSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReportEventAsync(Guid sessionId, string kind, string payloadJson, DateTimeOffset? occurredAt = null, CancellationToken ct = default)
        {
            Reports.Add((sessionId, kind, payloadJson, occurredAt));
            return Task.CompletedTask;
        }
        public Task<bool> HeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task RaiseSessionStarted(SessionStartedPayload payload)
        {
            SessionStarted?.Invoke(this, payload);
            // SessionCoordinator handles SessionStarted via an async void
            // dispatch. Yield once so its work loop runs; the FakeUi resolves
            // synchronously so a single Task.Yield is enough to settle it.
            await Task.Yield();
            await Task.Yield();
        }

        public void RaiseSessionEnded(Guid sessionId) => SessionEnded?.Invoke(this, sessionId);

        public void RaiseBundlesUpdated(SessionBundlesUpdatedPayload payload) =>
            SessionBundlesUpdated?.Invoke(this, payload);
    }

    private sealed class NoopUi : ISessionUiHost
    {
        public JoinDecision Decision { get; set; } = JoinDecision.Confirmed;
        public Task<JoinDecision> ShowJoinConfirmationAsync(JoinConfirmation confirmation, CancellationToken ct = default) =>
            Task.FromResult(Decision);
        public void DismissJoinConfirmation() { }
    }

    private sealed class FakeForegroundWatcher : IForegroundWatcher
    {
        public bool IsRunning { get; private set; }
        public event Action<ForegroundChange>? Changed;
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Dispose() => IsRunning = false;
        public void Raise(ForegroundChange change) => Changed?.Invoke(change);
    }

    private sealed class RecordingEnforcer : IFocusEnforcer
    {
        public List<nint> Remembered { get; } = new();
        public List<nint> Blocked { get; } = new();
        public List<nint> Minimized { get; } = new();
        public int ResetCount { get; private set; }
        /// <summary>
        /// When true, <see cref="Block"/> claims it successfully restored
        /// focus to a previously-allowed window. The default (false) models
        /// "no fallback" — which is what should trigger the overlay.
        /// </summary>
        public bool BlockRestoresFallback { get; set; }
        public void RememberAllowed(nint windowHandle) => Remembered.Add(windowHandle);
        public void Minimize(nint windowHandle) => Minimized.Add(windowHandle);
        public bool Block(nint offendingWindowHandle)
        {
            Blocked.Add(offendingWindowHandle);
            return BlockRestoresFallback;
        }
        public void Reset() => ResetCount++;
    }

    private sealed class FakeWindowEnumerator : IWindowEnumerator
    {
        public List<OpenWindow> Windows { get; } = new();
        public IReadOnlyList<OpenWindow> GetOpenWindows() => Windows;
    }

    private sealed class RecordingOverlay : IFocusOverlay
    {
        public List<(IReadOnlyList<AllowedAppRule> Rules, string? BlockedAppName)> Shown { get; } = new();
        public int HideCount { get; private set; }
        public int CloseCount { get; private set; }
        public void Show(IReadOnlyList<AllowedAppRule> allowedRules, string? blockedAppName)
            => Shown.Add((allowedRules, blockedAppName));
        public void Hide() => HideCount++;
        public void Close() => CloseCount++;
    }

    private sealed class RecordingReporter : IFocusEventReporter
    {
        public List<(Guid SessionId, ForegroundChange Change, bool Blocked)> Reports { get; } = new();
        public Task ReportForegroundChangeAsync(Guid sessionId, ForegroundChange change, bool blocked, CancellationToken ct = default)
        {
            Reports.Add((sessionId, change, blocked));
            return Task.CompletedTask;
        }
    }
}
