using FocusAgent.Core.Watchdog;

namespace FocusAgent.Core.Tests.Watchdog;

public class QuitFlagGateTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(50);

    [Fact]
    public void Absent_flag_does_not_suppress()
    {
        Assert.False(QuitFlagGate.ShouldSuppressRelaunch(flagLastWriteUtc: null, Now, Window));
    }

    [Fact]
    public void Just_written_flag_suppresses()
    {
        Assert.True(QuitFlagGate.ShouldSuppressRelaunch(Now, Now, Window));
    }

    [Fact]
    public void Flag_inside_window_suppresses()
    {
        Assert.True(QuitFlagGate.ShouldSuppressRelaunch(Now.AddSeconds(-5), Now, Window));
    }

    [Fact]
    public void Flag_at_exact_window_edge_suppresses()
    {
        Assert.True(QuitFlagGate.ShouldSuppressRelaunch(Now - Window, Now, Window));
    }

    [Fact]
    public void Stale_flag_does_not_suppress()
    {
        Assert.False(QuitFlagGate.ShouldSuppressRelaunch(Now.AddMinutes(-1), Now, Window));
    }

    [Fact]
    public void Future_dated_flag_suppresses_to_tolerate_clock_skew()
    {
        Assert.True(QuitFlagGate.ShouldSuppressRelaunch(Now.AddMinutes(5), Now, Window));
    }
}
