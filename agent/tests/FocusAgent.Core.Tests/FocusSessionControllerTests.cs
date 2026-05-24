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
        var fixtures = new Fixtures
        {
            AllowedAppRules = { new() { MatchKind = AllowedAppMatchKind.ProcessName, Value = "winword" } },
        };
        var (_, _) = BuildController(fixtures);
        var payload = NewPayload();
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
    public async Task Edge_is_always_unblocked_even_without_explicit_rule()
    {
        var fixtures = new Fixtures();
        var (_, _) = BuildController(fixtures);
        await fixtures.Hub.RaiseSessionStarted(NewPayload());

        fixtures.Watcher.Raise(ForegroundFor("msedge", hwnd: 0x300));

        Assert.Empty(fixtures.Enforcer.Blocked);
        var reported = Assert.Single(fixtures.Reporter.Reports);
        Assert.False(reported.Blocked);
    }

    [Fact]
    public async Task Duplicate_foreground_change_is_coalesced_within_window()
    {
        var fixtures = new Fixtures
        {
            AllowedAppRules = { new() { MatchKind = AllowedAppMatchKind.ProcessName, Value = "winword" } },
        };
        var (_, _) = BuildController(fixtures);
        await fixtures.Hub.RaiseSessionStarted(NewPayload());

        fixtures.Watcher.Raise(ForegroundFor("winword", hwnd: 0x100));
        fixtures.Clock.Advance(TimeSpan.FromMilliseconds(100));
        fixtures.Watcher.Raise(ForegroundFor("winword", hwnd: 0x100));
        fixtures.Clock.Advance(TimeSpan.FromMilliseconds(100));
        fixtures.Watcher.Raise(ForegroundFor("winword", hwnd: 0x100));

        Assert.Single(fixtures.Reporter.Reports);
    }

    [Fact]
    public async Task Distinct_apps_are_each_reported()
    {
        var fixtures = new Fixtures
        {
            AllowedAppRules = { new() { MatchKind = AllowedAppMatchKind.ProcessName, Value = "winword" } },
        };
        var (_, _) = BuildController(fixtures);
        await fixtures.Hub.RaiseSessionStarted(NewPayload());

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

    private static SessionStartedPayload NewPayload(Guid? id = null) => new(
        SessionId: id ?? Guid.NewGuid(),
        ClassId: Guid.NewGuid(),
        Mode: "strict",
        StartedAt: DateTimeOffset.UnixEpoch,
        JoinCode: "123456");

    private static ForegroundChange ForegroundFor(string process, nint hwnd, string? exePath = null, string? publisher = null, int pid = 4242) =>
        new(new AppInfo(process, exePath, publisher), WindowTitle: process, ProcessId: pid, WindowHandle: hwnd);

    private static (FocusSessionController, Fixtures) BuildController(Fixtures? supplied = null)
    {
        var fixtures = supplied ?? new Fixtures();
        var settings = Options.Create(new SessionSettings
        {
            AllowedApps = fixtures.AllowedAppRules,
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
            fixtures.Reporter,
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
        public RecordingReporter Reporter { get; } = new();
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public List<AllowedAppRule> AllowedAppRules { get; } = new();
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
        public int ResetCount { get; private set; }
        public void RememberAllowed(nint windowHandle) => Remembered.Add(windowHandle);
        public void Block(nint offendingWindowHandle) => Blocked.Add(offendingWindowHandle);
        public void Reset() => ResetCount++;
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
