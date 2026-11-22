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
    // The report time (Session:SchoolProfileReportSeconds) and scan interval the
    // harness runs with, as in production.
    private static readonly TimeSpan Report = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private static readonly BrowserWindow InPrivate =
        new(new nint(0x1001), "msedge", "New tab - [InPrivate] - Microsoft Edge");
    private static readonly BrowserWindow Ordinary =
        new(new nint(0x2002), "msedge", "Anchor - Personal - Microsoft Edge");

    [Fact]
    public async Task Reports_inprivate_opened_for_an_inprivate_window_still_open_after_the_report_time()
    {
        var (coordinator, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { Ordinary, InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out var sessionId));
        await DrainAsync(clock);
        Assert.Equal(1, monitor.OpenCount);

        await AdvanceAsync(clock, Report + Interval);

        var call = Assert.Single(reporter.Calls);
        Assert.Equal(sessionId, call.SessionId);
        Assert.Equal(TamperKinds.InPrivateOpened, call.Kind);
        Assert.Equal("inprivate_opened", call.Kind);
        Assert.Equal(1, monitor.DetectionCount);
    }

    [Fact]
    public async Task An_inprivate_window_is_not_reported_the_moment_it_opens_nor_before_the_report_time()
    {
        // #535: since #525 the window is paused at once and its overlay offers to
        // close it; the teacher hears only of one the student leaves open.
        var (coordinator, scanner, reporter, clock) = NewHarness();
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);
        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);

        scanner.Windows = new[] { InPrivate };
        await AdvanceAsync(clock, Interval);
        Assert.Equal(1, monitor.OpenCount);
        Assert.Empty(reporter.Calls);

        await AdvanceAsync(clock, Report - Interval * 2);
        Assert.Empty(reporter.Calls);
        Assert.Equal(0, monitor.DetectionCount);

        await AdvanceAsync(clock, Interval * 2);
        Assert.Single(reporter.Calls);
    }

    [Fact]
    public async Task An_inprivate_window_closed_within_the_report_time_is_never_reported()
    {
        var (coordinator, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);
        await AdvanceAsync(clock, Report - Interval * 2);

        scanner.Windows = new[] { Ordinary }; // the student closed it in time
        await AdvanceAsync(clock, Interval);
        Assert.Equal(0, monitor.OpenCount);
        await AdvanceAsync(clock, Report * 2);

        Assert.Empty(reporter.Calls);
        Assert.Equal(0, monitor.DetectionCount);
    }

    [Fact]
    public async Task Same_window_is_reported_once_across_repeated_polls()
    {
        var (coordinator, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);               // initial scan at session start
        await AdvanceAsync(clock, Report * 3); // many more poll ticks

        Assert.Single(reporter.Calls);
        Assert.Equal(1, monitor.DetectionCount);
    }

    [Fact]
    public async Task A_second_distinct_inprivate_window_is_reported_separately_after_its_own_report_time()
    {
        var (coordinator, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);
        await AdvanceAsync(clock, Report + Interval);
        Assert.Single(reporter.Calls);

        // Student opens a second InPrivate window and leaves it open too.
        scanner.Windows = new[] { InPrivate, InPrivate with { Handle = new nint(0x3003) } };
        await AdvanceAsync(clock, Report - Interval);
        Assert.Single(reporter.Calls);
        await AdvanceAsync(clock, Interval * 2);

        Assert.Equal(2, reporter.Calls.Count);
        Assert.All(reporter.Calls, c => Assert.Equal(TamperKinds.InPrivateOpened, c.Kind));
    }

    [Fact]
    public async Task A_closed_then_reopened_window_waits_the_report_time_anew_and_reports_again()
    {
        // The same handle can recur after a close (Windows recycles them).
        // Forgetting closed handles means a genuine reopen is a fresh window: its
        // wait starts over, and once left open it is reported again.
        var (coordinator, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);
        await AdvanceAsync(clock, Report + Interval);
        Assert.Single(reporter.Calls);

        scanner.Windows = Array.Empty<BrowserWindow>(); // closed
        await AdvanceAsync(clock, Interval);
        scanner.Windows = new[] { InPrivate };          // reopened, same handle
        await AdvanceAsync(clock, Report - Interval);
        Assert.Single(reporter.Calls);
        await AdvanceAsync(clock, Interval * 2);

        Assert.Equal(2, reporter.Calls.Count);
    }

    [Fact]
    public async Task A_window_closed_in_time_and_reopened_waits_from_the_reopen()
    {
        var (coordinator, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);
        await AdvanceAsync(clock, Report / 2);

        scanner.Windows = Array.Empty<BrowserWindow>(); // closed in time
        await AdvanceAsync(clock, Interval);
        scanner.Windows = new[] { InPrivate };          // reopened
        // Past a report time since it first opened, but not since it reopened.
        await AdvanceAsync(clock, Report / 2 + Interval);
        Assert.Empty(reporter.Calls);

        await AdvanceAsync(clock, Report / 2);
        Assert.Single(reporter.Calls);
    }

    [Fact]
    public async Task An_ordinary_edge_window_is_never_reported()
    {
        var (coordinator, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { Ordinary };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);
        await AdvanceAsync(clock, Report * 2);

        Assert.Empty(reporter.Calls);
        Assert.Equal(0, monitor.OpenCount);
    }

    [Fact]
    public async Task Only_windows_of_the_configured_browser_count()
    {
        // Witness:BrowserProcessName names Edge (an e2e points it at a stand-in of
        // its own); another process's window with InPrivate in its title is no
        // InPrivate window.
        var (coordinator, scanner, reporter, clock) = NewHarness();
        var standIn = InPrivate with { Handle = new nint(0x6006), ProcessName = "stand-in" };
        scanner.Windows = new[] { InPrivate, standIn };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock, browser: "stand-in.exe");

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);
        Assert.Equal(1, monitor.OpenCount);
        await AdvanceAsync(clock, Report + Interval);

        Assert.Single(reporter.Calls);
    }

    [Fact]
    public async Task Nothing_is_reported_before_a_session_is_joined()
    {
        var (coordinator, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await AdvanceAsync(clock, Report * 2);

        Assert.Empty(reporter.Calls);
        Assert.Equal(0, monitor.OpenCount);
    }

    [Fact]
    public async Task The_decline_path_never_starts_scanning()
    {
        var (coordinator, scanner, reporter, clock) = NewHarness(JoinDecision.Declined);
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);
        await AdvanceAsync(clock, Report * 2);

        Assert.Empty(reporter.Calls);
    }

    [Fact]
    public async Task Scanning_stops_after_the_session_ends()
    {
        var (coordinator, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out var sessionId));
        await DrainAsync(clock);
        await AdvanceAsync(clock, Report + Interval);
        Assert.Single(reporter.Calls);

        coordinator.HandleSessionEnded(sessionId);
        await DrainAsync(clock);
        Assert.Equal(0, monitor.OpenCount);

        // A new InPrivate window after the session ends must not be reported.
        scanner.Windows = new[] { InPrivate with { Handle = new nint(0x4004) } };
        await AdvanceAsync(clock, Report * 2);

        Assert.Single(reporter.Calls);
    }

    [Fact]
    public async Task A_window_still_waiting_when_the_session_ends_is_never_reported()
    {
        var (coordinator, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out var sessionId));
        await DrainAsync(clock);
        await AdvanceAsync(clock, Report / 2);

        coordinator.HandleSessionEnded(sessionId);
        await DrainAsync(clock);
        await AdvanceAsync(clock, Report * 2);

        Assert.Empty(reporter.Calls);
    }

    [Fact]
    public async Task A_reporter_failure_does_not_stop_the_poll_loop()
    {
        var (coordinator, scanner, reporter, clock) = NewHarness();
        reporter.Throw = true;
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);
        await AdvanceAsync(clock, Report + Interval);
        Assert.Single(reporter.Calls);

        // A new window left open is still attempted despite the first throw.
        scanner.Windows = new[] { InPrivate, InPrivate with { Handle = new nint(0x5005) } };
        var ex = await Record.ExceptionAsync(() => AdvanceAsync(clock, Report + Interval));

        Assert.Null(ex);
        Assert.Equal(2, reporter.Calls.Count);
    }

    [Fact]
    public async Task A_scanner_failure_does_not_stop_the_poll_loop()
    {
        var (coordinator, scanner, reporter, clock) = NewHarness();
        scanner.Throw = true;
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);

        scanner.Throw = false;
        scanner.Windows = new[] { InPrivate };
        await AdvanceAsync(clock, Report + Interval * 2);

        Assert.Single(reporter.Calls);
    }

    [Fact]
    public async Task A_scanner_failure_does_not_restart_the_wait_of_an_open_window()
    {
        // A scan that throws sees nothing: it must not count as the window closing.
        var (coordinator, scanner, reporter, clock) = NewHarness();
        scanner.Windows = new[] { InPrivate };
        await using var monitor = NewMonitor(coordinator, scanner, reporter, clock);

        await coordinator.HandleSessionStartedAsync(Payload(out _));
        await DrainAsync(clock);
        await AdvanceAsync(clock, Report / 2);

        scanner.Throw = true;
        await AdvanceAsync(clock, Interval);
        scanner.Throw = false;
        await AdvanceAsync(clock, Report / 2 + Interval);

        Assert.Single(reporter.Calls);
    }

    // --- harness ---------------------------------------------------------

    private static (SessionCoordinator, FakeScanner, RecordingReporter, FakeTimeProvider)
        NewHarness(JoinDecision decision = JoinDecision.Confirmed)
    {
        var hub = new FakeHub();
        var ui = new ConfirmingUi { Decision = decision };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var settings = Options.Create(new RealtimeSettings { JoinConfirmationDuration = TimeSpan.FromSeconds(5) });
        var coordinator = new SessionCoordinator(hub, ui, settings, clock);
        return (coordinator, new FakeScanner(), new RecordingReporter(), clock);
    }

    private static InPrivateWitnessMonitor NewMonitor(
        SessionCoordinator coordinator, FakeScanner scanner, RecordingReporter reporter, FakeTimeProvider clock,
        string browser = "msedge")
        => new(
            coordinator,
            scanner,
            reporter,
            Options.Create(new SessionSettings
            {
                InPrivateScanIntervalSeconds = (int)Interval.TotalSeconds,
                SchoolProfileReportSeconds = (int)Report.TotalSeconds,
            }),
            Options.Create(new WitnessSettings { BrowserProcessName = browser }),
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

    // Advances one scan interval at a time, letting each tick's report land.
    private static async Task AdvanceAsync(FakeTimeProvider clock, TimeSpan by)
    {
        for (var left = by; left > TimeSpan.Zero; left -= Interval)
        {
            clock.Advance(left < Interval ? left : Interval);
            await Task.Delay(10);
        }
        await Task.Delay(30);
    }

    private sealed class FakeScanner : IBrowserWindowScanner
    {
        private volatile BrowserWindow[] _windows = Array.Empty<BrowserWindow>();
        private volatile bool _throw;
        public BrowserWindow[] Windows { get => _windows; set => _windows = value; }
        public bool Throw { get => _throw; set => _throw = value; }

        public IReadOnlyList<BrowserWindow> GetOpenBrowserWindows()
        {
            if (_throw) throw new InvalidOperationException("simulated enumeration failure");
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
        public event EventHandler<AllowlistAmendedPayload>? AllowlistAmended;
        public event EventHandler<UnblockDeniedPayload>? UnblockDenied;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task JoinSessionAsync(Guid sessionId, string? joinCode, CancellationToken ct = default) => Task.CompletedTask;
        public Task LeaveSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclineSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReportEventAsync(Guid sessionId, string kind, string payloadJson, DateTimeOffset? occurredAt = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExtensionHeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> HeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> IsInSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<SessionStartedPayload?> GetStartedSessionAsync(CancellationToken ct = default) => Task.FromResult<SessionStartedPayload?>(null);
        public Task<SessionAllowlistPayload?> GetSessionAllowlistAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult<SessionAllowlistPayload?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
