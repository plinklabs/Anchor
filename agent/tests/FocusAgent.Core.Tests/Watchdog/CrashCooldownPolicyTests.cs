using FocusAgent.Core.Watchdog;

namespace FocusAgent.Core.Tests.Watchdog;

public class CrashCooldownPolicyTests
{
    private static readonly WatchdogPolicy TestPolicy = new(
        PollInterval: TimeSpan.FromSeconds(5),
        CrashWindowLimit: 5,
        CrashWindow: TimeSpan.FromSeconds(60),
        CrashCooldown: TimeSpan.FromMinutes(5),
        QuitFlagFreshness: TimeSpan.FromSeconds(10));

    [Fact]
    public void First_crash_does_not_trigger_cooldown()
    {
        var sut = new CrashCooldownPolicy(TestPolicy);

        var e = sut.RecordCrash(DateTimeOffset.UnixEpoch);

        Assert.False(e.InCooldown);
        Assert.Null(e.CooldownUntil);
        Assert.Equal(1, e.CrashesInWindow);
        Assert.False(sut.IsInCooldown(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Four_crashes_in_window_do_not_trigger_cooldown()
    {
        var sut = new CrashCooldownPolicy(TestPolicy);

        for (var i = 0; i < 4; i++)
        {
            sut.RecordCrash(DateTimeOffset.UnixEpoch.AddSeconds(i * 5));
        }

        Assert.False(sut.IsInCooldown(DateTimeOffset.UnixEpoch.AddSeconds(20)));
        Assert.Equal(4, sut.TotalCrashes);
    }

    [Fact]
    public void Fifth_crash_inside_window_triggers_cooldown()
    {
        var sut = new CrashCooldownPolicy(TestPolicy);
        var start = DateTimeOffset.UnixEpoch;

        CrashEvaluation lastEval = default;
        for (var i = 0; i < 5; i++)
        {
            lastEval = sut.RecordCrash(start.AddSeconds(i * 5));
        }

        Assert.True(lastEval.InCooldown);
        Assert.NotNull(lastEval.CooldownUntil);
        Assert.Equal(start.AddSeconds(4 * 5) + TestPolicy.CrashCooldown, lastEval.CooldownUntil);
        Assert.True(sut.IsInCooldown(start.AddSeconds(4 * 5).AddSeconds(1)));
    }

    [Fact]
    public void Cooldown_clears_once_elapsed()
    {
        var sut = new CrashCooldownPolicy(TestPolicy);
        var start = DateTimeOffset.UnixEpoch;

        for (var i = 0; i < 5; i++) sut.RecordCrash(start.AddSeconds(i));

        Assert.True(sut.IsInCooldown(start.AddMinutes(1)));
        Assert.False(sut.IsInCooldown(start.AddMinutes(6)));
        Assert.Null(sut.CooldownUntil); // side-effect: IsInCooldown clears expired marker
    }

    [Fact]
    public void Crashes_outside_window_are_evicted()
    {
        var sut = new CrashCooldownPolicy(TestPolicy);
        var start = DateTimeOffset.UnixEpoch;

        // Four old crashes far outside the 60s window…
        for (var i = 0; i < 4; i++) sut.RecordCrash(start.AddSeconds(i));

        // …then a crash well after the window. Should NOT trigger cooldown
        // because the old four were evicted before counting.
        var e = sut.RecordCrash(start.AddMinutes(5));

        Assert.False(e.InCooldown);
        Assert.Equal(1, e.CrashesInWindow);
    }

    [Fact]
    public void Cooldown_resets_window_so_next_burst_starts_fresh()
    {
        var sut = new CrashCooldownPolicy(TestPolicy);
        var start = DateTimeOffset.UnixEpoch;

        // Burst that trips cooldown.
        for (var i = 0; i < 5; i++) sut.RecordCrash(start.AddSeconds(i));
        Assert.True(sut.IsInCooldown(start.AddMinutes(1)));

        // After cooldown elapses, four MORE crashes inside a new 60s window
        // should NOT immediately re-trip — the previous burst was cleared.
        var afterCooldown = start.AddMinutes(6);
        Assert.False(sut.IsInCooldown(afterCooldown));
        for (var i = 0; i < 4; i++)
        {
            var ev = sut.RecordCrash(afterCooldown.AddSeconds(i));
            Assert.False(ev.InCooldown);
        }
    }

    [Fact]
    public void TotalCrashes_counts_every_recorded_crash()
    {
        var sut = new CrashCooldownPolicy(TestPolicy);
        var start = DateTimeOffset.UnixEpoch;

        for (var i = 0; i < 12; i++) sut.RecordCrash(start.AddMinutes(i));

        Assert.Equal(12, sut.TotalCrashes);
    }
}
