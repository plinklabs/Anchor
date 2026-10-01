using FocusAgent.Core.Dtos;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using FocusAgent.Core.Tamper;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FocusAgent.Core.Tests;

public class InPrivateWitnessMonitorTests
{
    private static readonly BrowserWindow InPrivate =
        new(new nint(0x1001), "msedge", "New tab - [InPrivate] - Microsoft Edge");
    private static readonly BrowserWindow Ordinary =
        new(new nint(0x2002), "msedge", "Anchor - Personal - Microsoft Edge");

    [Fact]
    public async Task Reports_inprivate_opened_for_an_open_inprivate_window_in_a_joined_session()
    {
        var (coordinator, hub, ui, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { Ordinary, InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out var sessionId));
        await DrainAsync(clock);

        var call = Assert.Single(reporter.Calls);
        Assert.Equal(sessionId, call.SessionId);
        Assert.Equal(TamperKinds.InPrivateOpened, call.Kind);
        Assert.Equal("inprivate_opened", call.Kind);
        _ = (hub, ui);
    }

    [Fact]
    public async Task Same_window_is_reported_once_across_repeated_polls()
    {
        var (coordinator, _, _, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);               // initial scan at session start
        await AdvanceAndDrainAsync(clock);     // a few more poll ticks
        await AdvanceAndDrainAsync(clock);

        Assert.Single(reporter.Calls);
        Assert.Equal(1, monitor.DetectionCount);
    }

    [Fact]
    public async Task A_second_distinct_inprivate_window_is_reported_separately()
    {
        var (coordinator, _, _, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);

        // Student opens a second InPrivate window.
        scanner.Windows = new[] { InPrivate, InPrivate with { Handle = new nint(0x3003) } };
        await AdvanceAndDrainAsync(clock);

        Assert.Equal(2, reporter.Calls.Count);
        Assert.All(reporter.Calls, c => Assert.Equal(TamperKinds.InPrivateOpened, c.Kind));
    }

    [Fact]
    public async Task A_closed_then_reopened_window_reports_again()
    {
        // The same handle can recur after a close (Windows recycles them). Pruning
        // closed handles means a genuine reopen is a fresh detection, not swallowed.
        var (coordinator, _, _, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);

        scanner.Windows = Array.Empty<BrowserWindow>(); // closed
        await AdvanceAndDrainAsync(clock);
        scanner.Windows = new[] { InPrivate };          // reopened, same handle
        await AdvanceAndDrainAsync(clock);

        Assert.Equal(2, reporter.Calls.Count);
    }

    [Fact]
    public async Task An_ordinary_edge_window_is_never_reported()
    {
        var (coordinator, _, _, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { Ordinary };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);
        await AdvanceAndDrainAsync(clock);

        Assert.Empty(reporter.Calls);
    }

    [Fact]
    public async Task Nothing_is_reported_before_a_session_is_joined()
    {
        var (coordinator, _, _, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await AdvanceAndDrainAsync(clock);
        await AdvanceAndDrainAsync(clock);

        Assert.Empty(reporter.Calls);
    }

    [Fact]
    public async Task The_decline_path_never_starts_scanning()
    {
        var (coordinator, _, ui, scanner, reporter, clock) = NewHarness();
        ui.Decision = JoinDecision.Declined;
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);
        await AdvanceAndDrainAsync(clock);

        Assert.Empty(reporter.Calls);
    }

    [Fact]
    public async Task Scanning_stops_after_the_session_ends()
    {
        var (coordinator, _, _, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out var sessionId));
        await DrainAsync(clock);
        Assert.Single(reporter.Calls);

        coordinator.HandleSessionEnded(sessionId);
        await DrainAsync(clock);

        // A new InPrivate window after the session ends must not be reported.
        scanner.Windows = new[] { InPrivate with { Handle = new nint(0x4004) } };
        await AdvanceAndDrainAsync(clock);

        Assert.Single(reporter.Calls);
    }

    [Fact]
    public async Task A_reporter_failure_does_not_stop_the_poll_loop()
    {
        var (coordinator, _, _, scanner, reporter, clock) = NewHarness();
        reporter.Throw = true;
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);

        // A new window on a later tick is still attempted despite the first throw.
        scanner.Windows = new[] { InPrivate, InPrivate with { Handle = new nint(0x5005) } };
        var ex = await Record.ExceptionAsync(() => AdvanceAndDrainAsync(clock));

        Assert.Null(ex);
        Assert.Equal(2, reporter.Calls.Count);
    }

    [Fact]
    public async Task A_scanner_failure_does_not_stop_the_poll_loop()
    {
        var (coordinator, _, _, scanner, reporter, clock) = NewHarness();
        scanner.Throw = true;
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);

        scanner.Throw = false;
        scanner.Windows = new[] { InPrivate };
        await AdvanceAndDrainAsync(clock);

        Assert.Single(reporter.Calls);
    }

    // --- harness ---------------------------------------------------------

    private static (SessionCoordinator, FakeHub, ConfirmingUi, FakeScanner, RecordingReporter, FakeTimeProvider)
        NewHarness()
    {
        var hub = new FakeHub();
        var ui = new ConfirmingUi();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var settings = Options.Create(new RealtimeSettings { JoinConfirmationDuration = TimeSpan.FromSeconds(5) });
        var coordinator = new SessionCoordinator(hub, ui, settings, clock);
        return (coordinator, hub, ui, new FakeScanner(), new RecordingReporter(), clock);
    }

    private static InPrivateWitnessMonitor NewMonitor(
        SessionCoordinator coordinator, FakeScanner scanner, RecordingReporter reporter, FakeTimeProvider clock)
        => new(
            coordinator,
            scanner,
            reporter,
            Options.Create(new SessionSettings { InPrivateScanIntervalSeconds = 5 }),
            clock);

    private static SessionStartedPayload Payload(out Guid sessionId)
    {
        sessionId = Guid.NewGuid();
        return new SessionStartedPayload(
            SessionId: sessionId,
            ClassId: Guid.NewGuid(),
            StartedAt: DateTimeOffset.UnixEpoch,
            JoinCode: "123456",
            Apps: Array.Empty<AllowedAppDto>(),
            Domains: Array.Empty<AllowedDomainDto>());
    }

    // The monitor's timer is due at 0 (immediate first scan). Nudge the fake clock
    // off zero so the due callback runs deterministically, then yield so the
    // queued async callback completes before the assertion reads the reporter.
    private static async Task DrainAsync(FakeTimeProvider clock)
    {
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Task.Delay(30);
    }

    private static async Task AdvanceAndDrainAsync(FakeTimeProvider clock)
    {
        clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(30);
    }

    private sealed class FakeScanner : IBrowserWindowScanner
    {
        private volatile BrowserWindow[] _windows = Array.Empty<BrowserWindow>();
        public BrowserWindow[] Windows { get => _windows; set => _windows = value; }
        public bool Throw { get; set; }

        public IReadOnlyList<BrowserWindow> GetOpenBrowserWindows()
        {
            if (Throw) throw new InvalidOperationException("simulated enumeration failure");
            return _windows;
        }
    }

    private sealed class RecordingReporter : ITamperReporter
    {
        private readonly List<(Guid SessionId, string Kind)> _calls = new();
        public IReadOnlyList<(Guid SessionId, string Kind)> Calls { get { lock (_calls) return _calls.ToArray(); } }
        public bool Throw { get; set; }

        public Task ReportAsync(Guid sessionId, string kind, CancellationToken ct = default)
        {
            lock (_calls) _calls.Add((sessionId, kind));
            if (Throw) throw new InvalidOperationException("simulated hub failure");
            return Task.CompletedTask;
        }
    }

    private sealed class ConfirmingUi : ISessionUiHost
    {
        public JoinDecision Decision { get; set; } = JoinDecision.Confirmed;
        public Task<JoinDecision> ShowJoinConfirmationAsync(JoinConfirmation confirmation, CancellationToken ct = default)
            => Task.FromResult(Decision);
        public void DismissJoinConfirmation() { }
    }

    private sealed class FakeHub : ISessionHubConnection
    {
        public AgentConnectionState State => AgentConnectionState.Connected;
#pragma warning disable CS0067
        public event EventHandler<AgentConnectionState>? StateChanged;
        public event EventHandler<SessionStartedPayload>? SessionStarted;
        public event EventHandler<Guid>? SessionEnded;
        public event EventHandler<SessionBundlesUpdatedPayload>? SessionBundlesUpdated;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task JoinSessionAsync(Guid sessionId, string? joinCode, CancellationToken ct = default) => Task.CompletedTask;
        public Task LeaveSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclineSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReportEventAsync(Guid sessionId, string kind, string payloadJson, DateTimeOffset? occurredAt = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> HeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> IsInSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
