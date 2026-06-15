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
/// best-effort tolerance of a throwing check. The real Velopack check path is
/// proven end-to-end by the IntegrationTests UpdateCheckTests spec.
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
        await DrainAsync();

        Assert.True(manager.CheckCount >= 1, $"expected a startup check, got {manager.CheckCount}");
    }

    [Fact]
    public async Task Re_checks_on_the_interval()
    {
        var manager = new FakeUpdateManager { IsInstalled = true };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock, interval: TimeSpan.FromHours(6));

        svc.Start();
        await DrainAsync(); // startup check

        var afterStartup = manager.CheckCount;
        clock.Advance(TimeSpan.FromHours(6));
        await DrainAsync();
        clock.Advance(TimeSpan.FromHours(6));
        await DrainAsync();

        Assert.True(manager.CheckCount >= afterStartup + 2,
            $"expected 2 more checks after two intervals, started at {afterStartup}, ended at {manager.CheckCount}");
    }

    [Fact]
    public async Task Does_not_check_when_not_a_velopack_install()
    {
        var manager = new FakeUpdateManager { IsInstalled = false };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);

        svc.Start();
        await DrainAsync();
        clock.Advance(TimeSpan.FromHours(24));
        await DrainAsync();

        Assert.Equal(0, manager.CheckCount);
    }

    [Fact]
    public async Task Does_not_check_when_disabled_in_config()
    {
        var manager = new FakeUpdateManager { IsInstalled = true };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock, enabled: false);

        svc.Start();
        await DrainAsync();
        clock.Advance(TimeSpan.FromHours(24));
        await DrainAsync();

        Assert.Equal(0, manager.CheckCount);
    }

    [Fact]
    public async Task Downloads_and_stages_when_an_update_is_available()
    {
        var update = new AgentUpdateCheckResult(IsUpdateAvailable: true, TargetVersion: "1.2.3", Payload: new object());
        var manager = new FakeUpdateManager { IsInstalled = true, NextResult = update };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);

        svc.Start();
        await DrainAsync();

        Assert.Equal(1, manager.DownloadCount);
        Assert.Equal(1, manager.StageCount);
        Assert.True(svc.UpdatePendingRestart);
        Assert.Equal("1.2.3", svc.PendingVersion);
    }

    [Fact]
    public async Task Does_not_download_or_stage_when_up_to_date()
    {
        var manager = new FakeUpdateManager { IsInstalled = true, NextResult = AgentUpdateCheckResult.None };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock);

        svc.Start();
        await DrainAsync();

        Assert.Equal(0, manager.DownloadCount);
        Assert.Equal(0, manager.StageCount);
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
        await DrainAsync(); // startup check throws but is swallowed

        var afterStartup = manager.CheckCount;
        Assert.True(afterStartup >= 1);

        // The loop survives a thrown check and tries again next interval.
        clock.Advance(TimeSpan.FromHours(1));
        await DrainAsync();

        Assert.True(manager.CheckCount > afterStartup);
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
        await DrainAsync();
        Assert.Equal(1, manager.CheckCount);

        // An interval fires while the first check is still running — it must be
        // dropped, not queued (single-flight).
        clock.Advance(TimeSpan.FromHours(1));
        await DrainAsync();
        Assert.Equal(1, manager.CheckCount);

        gate.SetResult();    // let the first check complete
        await DrainAsync();

        // A later interval now runs normally.
        clock.Advance(TimeSpan.FromHours(1));
        await DrainAsync();
        Assert.True(manager.CheckCount >= 2);
    }

    [Fact]
    public async Task Sub_minute_interval_is_clamped_up_so_it_cannot_busy_loop()
    {
        var manager = new FakeUpdateManager { IsInstalled = true };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var svc = NewService(manager, clock, interval: TimeSpan.Zero);

        svc.Start();
        await DrainAsync(); // startup check only

        var afterStartup = manager.CheckCount;

        // Advance less than the clamp floor: no extra check should fire.
        clock.Advance(TimeSpan.FromSeconds(30));
        await DrainAsync();
        Assert.Equal(afterStartup, manager.CheckCount);

        // Cross the floor: now the interval tick fires.
        clock.Advance(AgentUpdateService.MinimumInterval);
        await DrainAsync();
        Assert.True(manager.CheckCount > afterStartup);
    }

    private static AgentUpdateService NewService(
        FakeUpdateManager manager,
        FakeTimeProvider clock,
        bool enabled = true,
        TimeSpan? interval = null)
    {
        var settings = new UpdateSettings
        {
            Enabled = enabled,
            CheckInterval = interval ?? TimeSpan.FromHours(6),
        };
        return new AgentUpdateService(manager, Options.Create(settings), clock);
    }

    private static async Task DrainAsync()
    {
        // Give the timer-fired fire-and-forget check a slice to run before the
        // assertion reads the counters.
        await Task.Delay(40);
    }

    private sealed class FakeUpdateManager : IAgentUpdateManager
    {
        public bool IsInstalled { get; set; }
        public bool ThrowOnCheck { get; set; }
        public AgentUpdateCheckResult NextResult { get; set; } = AgentUpdateCheckResult.None;
        public Task? CheckGate { get; set; }

        private int _checkCount;
        private int _downloadCount;
        private int _stageCount;

        public int CheckCount => Volatile.Read(ref _checkCount);
        public int DownloadCount => Volatile.Read(ref _downloadCount);
        public int StageCount => Volatile.Read(ref _stageCount);

        public async Task<AgentUpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _checkCount);
            if (CheckGate is { } gate) await gate.ConfigureAwait(false);
            if (ThrowOnCheck) throw new InvalidOperationException("feed unreachable (test)");
            return NextResult;
        }

        public Task DownloadUpdateAsync(AgentUpdateCheckResult update, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _downloadCount);
            return Task.CompletedTask;
        }

        public void StageUpdateForNextRestart(AgentUpdateCheckResult update)
        {
            Interlocked.Increment(ref _stageCount);
        }
    }
}
