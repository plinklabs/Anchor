using System.Diagnostics;
using FocusAgent.Core.Settings;
using FocusAgent.Core.Updates;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FocusAgent.Core.Tests;

/// <summary>
/// Unit coverage for the #224 auto-update cadence + gating policy. These drive the
/// <see cref="AgentUpdateService"/> against a fake <see cref="IAgentUpdateManager"/>
/// (no real Velopack / GitHub) to lock the behaviour the issue asks for: a startup
/// check, a re-check on the interval, install/enabled gating, single-flighting, and
/// best-effort tolerance of a throwing check. A pass ends at the download (#449): it
/// reports what the next start will apply and nothing more. The real Velopack check
/// path is proven end-to-end by the IntegrationTests UpdateCheckTests spec, and the
/// download-then-apply-at-next-start path by UpdateApplyTests. The #456 additions
/// are covered here too: the wake/unlock check against a clock that moves through
/// sleep while the timers don't, the ready-to-apply signal, and the hold-back that
/// keeps a failed apply from turning into a restart loop.
///
/// <para>
/// Every pass runs on the thread pool and a tick that lands before the previous
/// pass has completed is dropped (#407), so these tests never sleep for a slice of
/// wall-clock time (#435): they await the pass itself (<see cref="PassAsync"/>), or
/// wait for an explicit condition with a generous timeout (<see cref="WaitUntilAsync"/>).
/// </para>
/// </summary>
public class AgentUpdateServiceTests
{
    [Fact]
    public async Task Checks_on_startup_when_installed_and_enabled()
    {
        var manager = new FakeUpdateManager { IsInstalled = true };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);

        svc.Start();
        await PassAsync(svc);

        Assert.Equal(1, manager.CheckCount);
    }

    [Fact]
    public async Task Re_checks_on_the_interval()
    {
        var manager = new FakeUpdateManager { IsInstalled = true };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock, interval: TimeSpan.FromHours(6));

        svc.Start();
        await PassAsync(svc); // startup check

        var afterStartup = manager.CheckCount;
        clock.Advance(TimeSpan.FromHours(6));
        await PassAsync(svc);
        clock.Advance(TimeSpan.FromHours(6));
        await PassAsync(svc);

        Assert.Equal(afterStartup + 2, manager.CheckCount);
    }

    [Fact]
    public async Task Does_not_check_when_not_a_velopack_install()
    {
        var manager = new FakeUpdateManager { IsInstalled = false };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);

        svc.Start();
        await PassAsync(svc);
        clock.Advance(TimeSpan.FromHours(24));
        await PassAsync(svc);

        Assert.Equal(0, manager.CheckCount);
    }

    [Fact]
    public async Task Does_not_check_when_disabled_in_config()
    {
        var manager = new FakeUpdateManager { IsInstalled = true };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock, enabled: false);

        svc.Start();
        await PassAsync(svc);
        clock.Advance(TimeSpan.FromHours(24));
        await PassAsync(svc);

        Assert.Equal(0, manager.CheckCount);
    }

    [Fact]
    public async Task Downloads_and_reports_the_update_the_next_start_applies()
    {
        var update = new AgentUpdateCheckResult(IsUpdateAvailable: true, TargetVersion: "1.2.3", Payload: new object());
        var manager = new FakeUpdateManager { IsInstalled = true, NextResult = update };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);

        svc.Start();
        await PassAsync(svc);

        Assert.Equal(1, manager.DownloadCount);
        Assert.Equal(1, manager.PendingQueryCount);
        Assert.True(svc.UpdatePendingRestart);
        Assert.Equal("1.2.3", svc.PendingVersion);
    }

    [Fact]
    public async Task Reports_the_version_Velopack_will_apply_not_the_one_it_asked_for()
    {
        // #449: the pending version is read back from the packages folder (what the
        // next start's auto-apply will pick), never assumed from the check result.
        var update = new AgentUpdateCheckResult(IsUpdateAvailable: true, TargetVersion: "1.2.3", Payload: new object());
        var manager = new FakeUpdateManager { IsInstalled = true, NextResult = update, PendingAfterDownload = "1.2.4" };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);

        svc.Start();
        await PassAsync(svc);

        Assert.True(svc.UpdatePendingRestart);
        Assert.Equal("1.2.4", svc.PendingVersion);
    }

    [Fact]
    public async Task Does_not_report_a_pending_update_when_the_download_left_nothing_for_the_next_start()
    {
        var update = new AgentUpdateCheckResult(IsUpdateAvailable: true, TargetVersion: "1.2.3", Payload: new object());
        var manager = new FakeUpdateManager { IsInstalled = true, NextResult = update, PendingAfterDownload = null };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);

        svc.Start();
        await PassAsync(svc);

        Assert.Equal(1, manager.DownloadCount);
        Assert.Equal(1, manager.PendingQueryCount);
        Assert.False(svc.UpdatePendingRestart);
        Assert.Null(svc.PendingVersion);
    }

    [Fact]
    public async Task A_throwing_download_reports_no_pending_update()
    {
        var update = new AgentUpdateCheckResult(IsUpdateAvailable: true, TargetVersion: "1.2.3", Payload: new object());
        var manager = new FakeUpdateManager { IsInstalled = true, NextResult = update, ThrowOnDownload = true };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);

        svc.Start();
        await PassAsync(svc);

        Assert.Equal(1, manager.DownloadCount);
        Assert.Equal(0, manager.PendingQueryCount);
        Assert.False(svc.UpdatePendingRestart);
        Assert.Null(svc.PendingVersion);
    }

    [Fact]
    public async Task Does_not_download_when_up_to_date()
    {
        var manager = new FakeUpdateManager { IsInstalled = true, NextResult = AgentUpdateCheckResult.None };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);

        svc.Start();
        await PassAsync(svc);

        Assert.Equal(0, manager.DownloadCount);
        Assert.Equal(0, manager.PendingQueryCount);
        Assert.False(svc.UpdatePendingRestart);
        Assert.Null(svc.PendingVersion);
    }

    [Fact]
    public async Task A_throwing_check_does_not_crash_the_service_and_it_keeps_checking()
    {
        var manager = new FakeUpdateManager { IsInstalled = true, ThrowOnCheck = true };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock, interval: TimeSpan.FromHours(1));

        svc.Start();
        await PassAsync(svc); // startup check throws but is swallowed

        Assert.Equal(1, manager.CheckCount);

        // The loop survives a thrown check and tries again next interval. Awaiting
        // the startup pass to completion first matters: a tick that lands while it
        // is still unwinding the throw is dropped as in flight, by design.
        clock.Advance(TimeSpan.FromHours(1));
        await PassAsync(svc);

        Assert.Equal(2, manager.CheckCount);
        Assert.False(svc.UpdatePendingRestart);
    }

    [Fact]
    public async Task A_second_check_does_not_overlap_a_slow_one_in_flight()
    {
        var gate = new TaskCompletionSource();
        var manager = new FakeUpdateManager { IsInstalled = true, CheckGate = gate.Task };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock, interval: TimeSpan.FromHours(1));

        svc.Start();         // startup check begins and blocks on the gate
        var startup = svc.InFlight;
        await WaitUntilAsync(() => manager.CheckCount == 1, "the startup check to begin");

        // An interval fires while the first check is still running — it must be
        // dropped, not queued (single-flight): no new pass, no second check.
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Same(startup, svc.InFlight);
        Assert.False(startup.IsCompleted);
        Assert.Equal(1, manager.CheckCount);

        gate.SetResult();    // let the first check complete
        await startup.WaitAsync(WaitTimeout);

        // A later interval now runs normally.
        clock.Advance(TimeSpan.FromHours(1));
        await PassAsync(svc);
        Assert.Equal(2, manager.CheckCount);
    }

    [Fact]
    public async Task Sub_minute_interval_is_clamped_up_so_it_cannot_busy_loop()
    {
        var manager = new FakeUpdateManager { IsInstalled = true };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock, interval: TimeSpan.Zero);

        svc.Start();
        await PassAsync(svc); // startup check only

        var afterStartup = manager.CheckCount;

        // Advance less than the clamp floor: no extra check should fire. (Had one
        // fired, it would be the pass in flight now, and awaiting it would count it.)
        clock.Advance(TimeSpan.FromSeconds(30));
        await PassAsync(svc);
        Assert.Equal(afterStartup, manager.CheckCount);

        // Cross the floor: now the interval tick fires.
        clock.Advance(AgentUpdateService.MinimumInterval);
        await PassAsync(svc);
        Assert.Equal(afterStartup + 1, manager.CheckCount);
    }

    // --- #456: the wake/unlock check (CheckIfDueAsync) -------------------------

    [Fact]
    public async Task A_wake_after_a_night_asleep_checks_although_the_interval_timer_never_fired()
    {
        // The issue's case: two hours open, then the lid closed overnight. The
        // timer counts awake time only, so it saw two of its six hours.
        var manager = new FakeUpdateManager { IsInstalled = true };
        var clock = new SleepingClock();
        await using var svc = NewService(manager, clock, interval: TimeSpan.FromHours(6));

        svc.Start();
        await PassAsync(svc);
        Assert.Equal(clock.GetUtcNow(), svc.LastCheckStartedAt);

        clock.Advance(TimeSpan.FromHours(2));
        clock.Sleep(TimeSpan.FromHours(14));
        await PassAsync(svc);
        Assert.Equal(1, manager.CheckCount);

        await svc.CheckIfDueAsync("resumed from sleep").WaitAsync(WaitTimeout);

        Assert.Equal(2, manager.CheckCount);
        Assert.Equal(clock.GetUtcNow(), svc.LastCheckStartedAt);
    }

    [Fact]
    public async Task A_wake_before_the_interval_has_passed_by_the_clock_does_not_check()
    {
        var manager = new FakeUpdateManager { IsInstalled = true };
        var clock = new SleepingClock();
        await using var svc = NewService(manager, clock, interval: TimeSpan.FromHours(6));

        svc.Start();
        await PassAsync(svc);
        clock.Advance(TimeSpan.FromHours(1));
        clock.Sleep(TimeSpan.FromHours(4));

        await svc.CheckIfDueAsync("was unlocked").WaitAsync(WaitTimeout);
        await PassAsync(svc);

        Assert.Equal(1, manager.CheckCount);
    }

    [Fact]
    public async Task A_wake_check_restarts_the_interval_so_the_timer_does_not_check_again_right_after()
    {
        var manager = new FakeUpdateManager { IsInstalled = true };
        var clock = new SleepingClock();
        await using var svc = NewService(manager, clock, interval: TimeSpan.FromHours(6));

        svc.Start();
        await PassAsync(svc);                       // awake 0 h
        clock.Advance(TimeSpan.FromHours(5));
        clock.Sleep(TimeSpan.FromHours(10));
        await svc.CheckIfDueAsync("resumed from sleep").WaitAsync(WaitTimeout);
        Assert.Equal(2, manager.CheckCount);        // awake 5 h

        // The old schedule would check at awake 6 h, an hour after the wake check.
        clock.Advance(TimeSpan.FromHours(1));
        await PassAsync(svc);
        Assert.Equal(2, manager.CheckCount);

        // The new one checks six awake hours after the wake check.
        clock.Advance(TimeSpan.FromHours(5));
        await PassAsync(svc);
        Assert.Equal(3, manager.CheckCount);
    }

    [Fact]
    public async Task A_wake_does_not_check_when_not_a_velopack_install()
    {
        var manager = new FakeUpdateManager { IsInstalled = false };
        var clock = new SleepingClock();
        await using var svc = NewService(manager, clock);

        svc.Start();
        clock.Sleep(TimeSpan.FromDays(3));
        await svc.CheckIfDueAsync("resumed from sleep").WaitAsync(WaitTimeout);
        await PassAsync(svc);

        Assert.Equal(0, manager.CheckCount);
        Assert.Null(svc.LastCheckStartedAt);
    }

    // --- #456: ready to apply, and no restart loops ----------------------------

    [Fact]
    public async Task A_pass_that_downloads_an_update_reports_it_ready_to_apply()
    {
        var update = new AgentUpdateCheckResult(IsUpdateAvailable: true, TargetVersion: "1.2.3", Payload: new object());
        var manager = new FakeUpdateManager { IsInstalled = true, NextResult = update };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);
        var ready = 0;
        svc.UpdateReady += (_, _) => Interlocked.Increment(ref ready);

        svc.Start();
        await PassAsync(svc);

        Assert.Equal(1, ready);
        Assert.Equal("1.2.3", svc.ReadyToApplyVersion);
    }

    [Fact]
    public async Task An_up_to_date_pass_reports_nothing_ready()
    {
        var manager = new FakeUpdateManager { IsInstalled = true, NextResult = AgentUpdateCheckResult.None };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);
        var ready = 0;
        svc.UpdateReady += (_, _) => Interlocked.Increment(ref ready);

        svc.Start();
        await PassAsync(svc);

        Assert.Equal(0, ready);
        Assert.Null(svc.ReadyToApplyVersion);
    }

    [Fact]
    public async Task A_version_whose_hand_over_failed_is_held_back_until_the_next_pass_finds_it_again()
    {
        var update = new AgentUpdateCheckResult(IsUpdateAvailable: true, TargetVersion: "1.2.3", Payload: new object());
        var manager = new FakeUpdateManager { IsInstalled = true, NextResult = update };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock, interval: TimeSpan.FromHours(6));
        var ready = 0;
        svc.UpdateReady += (_, _) => Interlocked.Increment(ref ready);

        svc.Start();
        await PassAsync(svc);
        svc.ReportApplyFailed("1.2.3");

        // Still downloaded and pending for the next start, but not to restart into.
        Assert.Null(svc.ReadyToApplyVersion);
        Assert.Equal("1.2.3", svc.PendingVersion);

        clock.Advance(TimeSpan.FromHours(6));
        await PassAsync(svc);

        Assert.Equal(2, ready);
        Assert.Equal("1.2.3", svc.ReadyToApplyVersion);
    }

    [Fact]
    public async Task Restarted_by_the_updater_with_the_update_still_waiting_holds_it_back_through_the_startup_pass()
    {
        // The updater's apply failed and it started the old version again: the
        // package is still there, newer than what runs. Restarting into it at once
        // would fail the same way, again and again.
        var update = new AgentUpdateCheckResult(IsUpdateAvailable: true, TargetVersion: "1.2.3", Payload: new object());
        var manager = new FakeUpdateManager { IsInstalled = true, NextResult = update, InitiallyPending = "1.2.3" };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock, interval: TimeSpan.FromHours(6), restartedByUpdater: true);
        var ready = 0;
        svc.UpdateReady += (_, _) => Interlocked.Increment(ref ready);

        svc.Start();
        await PassAsync(svc);

        Assert.Equal(0, ready);
        Assert.Null(svc.ReadyToApplyVersion);
        Assert.Equal("1.2.3", svc.PendingVersion);

        // The next check tries it again, once.
        clock.Advance(TimeSpan.FromHours(6));
        await PassAsync(svc);

        Assert.Equal(1, ready);
        Assert.Equal("1.2.3", svc.ReadyToApplyVersion);
    }

    [Fact]
    public async Task Restarted_by_the_updater_does_not_hold_back_a_newer_release_than_the_one_that_failed()
    {
        var update = new AgentUpdateCheckResult(IsUpdateAvailable: true, TargetVersion: "1.2.4", Payload: new object());
        var manager = new FakeUpdateManager { IsInstalled = true, NextResult = update, InitiallyPending = "1.2.3" };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock, restartedByUpdater: true);

        svc.Start();
        await PassAsync(svc);

        Assert.Equal("1.2.4", svc.ReadyToApplyVersion);
    }

    [Fact]
    public async Task Restarted_by_the_updater_after_a_working_apply_holds_nothing_back()
    {
        // After a working apply nothing newer than the running version waits.
        var update = new AgentUpdateCheckResult(IsUpdateAvailable: true, TargetVersion: "1.2.4", Payload: new object());
        var manager = new FakeUpdateManager { IsInstalled = true, NextResult = update };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock, restartedByUpdater: true);
        var ready = 0;
        svc.UpdateReady += (_, _) => Interlocked.Increment(ref ready);

        svc.Start();
        await PassAsync(svc);

        Assert.Equal(1, ready);
        Assert.Equal("1.2.4", svc.ReadyToApplyVersion);
    }

    // --- #407: the fatal-path check (CheckNowAsync) ---------------------------

    [Fact]
    public async Task CheckNow_joins_the_startup_check_still_in_flight_instead_of_starting_a_second()
    {
        // The crash case: startup kicked off its check, then startup crashed while
        // that check was still running. The fatal path must wait for THAT pass.
        var gate = new TaskCompletionSource();
        var update = new AgentUpdateCheckResult(IsUpdateAvailable: true, TargetVersion: "1.2.3", Payload: new object());
        var manager = new FakeUpdateManager { IsInstalled = true, CheckGate = gate.Task, NextResult = update };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);

        svc.Start();
        var startup = svc.InFlight;
        await WaitUntilAsync(() => manager.CheckCount == 1, "the startup check to begin");

        var fatal = svc.CheckNowAsync();
        Assert.Same(startup, fatal);
        Assert.False(fatal.IsCompleted, "the fatal-path task completed while the startup check was still running");

        gate.SetResult();
        await fatal.WaitAsync(WaitTimeout);

        Assert.Equal(1, manager.CheckCount);
        Assert.Equal(1, manager.DownloadCount);
        Assert.True(svc.UpdatePendingRestart);
    }

    [Fact]
    public async Task CheckNow_runs_a_fresh_pass_without_Start_and_completes_only_once_the_update_is_downloaded()
    {
        // The host-never-came-up case: nothing was started, so the fatal path runs
        // its own pass — and the task it hands back must cover the whole download,
        // since the crash path keeps the process alive exactly that long and the
        // next launch can only apply a package that finished downloading.
        var download = new TaskCompletionSource();
        var update = new AgentUpdateCheckResult(IsUpdateAvailable: true, TargetVersion: "1.2.3", Payload: new object());
        var manager = new FakeUpdateManager { IsInstalled = true, NextResult = update, DownloadGate = download.Task };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);

        var fatal = svc.CheckNowAsync();
        await WaitUntilAsync(() => manager.DownloadCount == 1, "the pass to reach the download");
        Assert.Equal(1, manager.CheckCount);
        Assert.False(fatal.IsCompleted, "the fatal-path task completed before the download finished");
        Assert.Equal(0, manager.PendingQueryCount);
        Assert.Null(svc.PendingVersion);

        download.SetResult();
        await fatal.WaitAsync(WaitTimeout);

        Assert.Equal(1, manager.PendingQueryCount);
        Assert.Equal("1.2.3", svc.PendingVersion);
    }

    [Fact]
    public async Task CheckNow_runs_a_new_pass_once_the_previous_one_has_finished()
    {
        // A background fatal hours after startup: the startup check is long done,
        // so "one more check" means a real new one.
        var manager = new FakeUpdateManager { IsInstalled = true };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);

        await svc.CheckNowAsync().WaitAsync(WaitTimeout);
        await svc.CheckNowAsync().WaitAsync(WaitTimeout);

        Assert.Equal(2, manager.CheckCount);
    }

    [Fact]
    public async Task CheckNow_completes_at_once_without_checking_when_not_a_velopack_install()
    {
        // Dev runs and the e2e are not installs: a crash there must not wait on, or
        // talk to, any feed.
        var manager = new FakeUpdateManager { IsInstalled = false };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);

        await svc.CheckNowAsync().WaitAsync(WaitTimeout);

        Assert.Equal(0, manager.CheckCount);
    }

    [Fact]
    public async Task CheckNow_task_does_not_fault_when_the_check_throws()
    {
        // The crash path waits on this task with no try/catch of its own.
        var manager = new FakeUpdateManager { IsInstalled = true, ThrowOnCheck = true };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);

        var fatal = svc.CheckNowAsync();
        await fatal.WaitAsync(WaitTimeout);

        Assert.True(fatal.IsCompletedSuccessfully);
        Assert.Equal(1, manager.CheckCount);
    }

    private static AgentUpdateService NewService(
        FakeUpdateManager manager,
        TimeProvider clock,
        bool enabled = true,
        TimeSpan? interval = null,
        bool restartedByUpdater = false)
    {
        var settings = new UpdateSettings
        {
            Enabled = enabled,
            CheckInterval = interval ?? TimeSpan.FromHours(6),
        };
        return new AgentUpdateService(manager, Options.Create(settings), clock)
        {
            RestartedByUpdater = restartedByUpdater,
        };
    }

    /// <summary>
    /// A laptop's clock across sleep (#456): <see cref="GetUtcNow"/> jumps by the time
    /// asleep (<see cref="Sleep"/>), while the timers, like .NET's on Windows, only
    /// count time awake (<see cref="Advance"/>).
    /// </summary>
    private sealed class SleepingClock : TimeProvider
    {
        private readonly FakeTimeProvider _awake = new(DateTimeOffset.UnixEpoch);
        private TimeSpan _slept;

        public void Advance(TimeSpan awake) => _awake.Advance(awake);

        public void Sleep(TimeSpan asleep) => _slept += asleep;

        public override DateTimeOffset GetUtcNow() => _awake.GetUtcNow() + _slept;

        public override long GetTimestamp() => _awake.GetTimestamp();

        public override long TimestampFrequency => _awake.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _awake.CreateTimer(callback, state, dueTime, period);
    }

    // Generous on purpose: a passing test never waits this long, and a loaded CI
    // runner must not turn a slow pass into a failure.
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Wait for the check pass in flight (or the last one) to complete. FakeTimeProvider
    /// fires a due timer synchronously inside <c>Start()</c> / <c>Advance()</c>, so once
    /// either returns, the pass that tick started — if any — is the one in flight.
    /// </summary>
    private static Task PassAsync(AgentUpdateService svc) => svc.InFlight.WaitAsync(WaitTimeout);

    /// <summary>Wait for a condition the thread-pool pass makes true (e.g. a gated
    /// check has begun); fails the test after <see cref="WaitTimeout"/>.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > WaitTimeout)
                Assert.Fail($"timed out after {WaitTimeout.TotalSeconds:0} s waiting for {what}");
            await Task.Delay(1);
        }
    }

    internal sealed class FakeUpdateManager : IAgentUpdateManager
    {
        private const string Downloaded = "<the downloaded version>";

        public bool IsInstalled { get; set; }
        public bool ThrowOnCheck { get; set; }
        public bool ThrowOnDownload { get; set; }
        public AgentUpdateCheckResult NextResult { get; set; } = AgentUpdateCheckResult.None;
        public Task? CheckGate { get; set; }
        public Task? DownloadGate { get; set; }

        /// <summary>What Velopack reports as pending once a download finished: by
        /// default the version just downloaded, like a real packages folder.</summary>
        public string? PendingAfterDownload { get; set; } = Downloaded;

        /// <summary>A package already in the packages folder at start (#456): one a
        /// failed apply left behind, say.</summary>
        public string? InitiallyPending { init => _pending = value; }

        private int _checkCount;
        private int _downloadCount;
        private int _pendingQueryCount;
        private string? _pending;

        public int CheckCount => Volatile.Read(ref _checkCount);
        public int DownloadCount => Volatile.Read(ref _downloadCount);
        public int PendingQueryCount => Volatile.Read(ref _pendingQueryCount);

        public async Task<AgentUpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _checkCount);
            if (CheckGate is { } gate) await gate.ConfigureAwait(false);
            if (ThrowOnCheck) throw new InvalidOperationException("feed unreachable (test)");
            return NextResult;
        }

        public async Task DownloadUpdateAsync(AgentUpdateCheckResult update, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _downloadCount);
            if (DownloadGate is { } gate) await gate.ConfigureAwait(false);
            if (ThrowOnDownload) throw new IOException("download failed (test)");
            Volatile.Write(ref _pending, PendingAfterDownload == Downloaded ? update.TargetVersion : PendingAfterDownload);
        }

        public string? GetPendingRestartVersion()
        {
            Interlocked.Increment(ref _pendingQueryCount);
            return Volatile.Read(ref _pending);
        }

        // The service never hands over itself; IdleUpdateRestarter's IAgentRestarter does.
        public void ApplyOnExitAndRestart(IReadOnlyList<string> restartArgs) =>
            throw new InvalidOperationException("AgentUpdateService must never start the updater (#449).");
    }
}
