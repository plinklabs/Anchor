using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using FocusAgent.Native;
using FocusAgent.Native.Win32;
using Microsoft.Extensions.Logging;

namespace FocusAgent.Native.Tests;

/// <summary>
/// <see cref="FocusEnforcer.Block"/> and <see cref="FocusEnforcer.Minimize"/> on
/// real windows (#438, #440, #451). The agent never waits for an app, whether the
/// window minimizes itself, the agent minimizes it or the agent restores the
/// fallback; where the foreground goes then needs
/// two processes and is covered by the e2e FocusFallbackTests.
/// </summary>
[SupportedOSPlatform("windows")]
public class FocusEnforcerWindowTests
{
    [Fact]
    public async Task A_blocked_window_goes_down_without_a_warning()
    {
        var log = new ListLogger<FocusEnforcer>();
        using var allowed = TestWindow.Open("Anchor test allowed app");
        using var offList = TestWindow.Open("Anchor test off-list app");
        var enforcer = new FocusEnforcer(log);
        enforcer.RememberAllowed(allowed.Handle);

        Assert.True(enforcer.Block(offList.Handle));

        Assert.True(
            await WaitUntilAsync(() => NativeMethods.IsIconic(offList.Handle), TimeSpan.FromSeconds(2)),
            "The blocked window was not minimized.");
        Assert.False(NativeMethods.IsIconic(allowed.Handle), "The allowed window was minimized.");
        await Task.Delay(FocusEnforcer.OwnThreadMinimizeTimeout + TimeSpan.FromMilliseconds(300));
        Assert.DoesNotContain(log.Messages, m => m.Contains("did not minimize"));
    }

    [Fact]
    public async Task Blocking_an_app_that_is_not_responding_never_holds_up_the_agent()
    {
        var log = new ListLogger<FocusEnforcer>();
        using var allowed = TestWindow.Open("Anchor test allowed app");
        using var offList = TestWindow.Open("Anchor test hung off-list app");
        var enforcer = new FocusEnforcer(log);
        enforcer.RememberAllowed(allowed.Handle);
        offList.Freeze();

        var clock = Stopwatch.StartNew();
        Assert.True(enforcer.Block(offList.Handle));
        clock.Stop();

        // A ShowWindow from the agent waits until the app handles messages again, and
        // holds up every foreground change behind it meanwhile.
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(1),
            $"Block took {clock.ElapsedMilliseconds} ms while the off-list app was not responding.");
        Assert.True(
            await WaitUntilAsync(() => log.Messages.Any(m => m.Contains("did not minimize")), TimeSpan.FromSeconds(3)),
            "The agent did not log that the blocked app is not responding.");
        Assert.False(NativeMethods.IsIconic(offList.Handle), "A window whose thread is frozen went down.");

        // The request stays queued: the window goes down the moment its app responds.
        offList.Thaw();
        Assert.True(
            await WaitUntilAsync(() => NativeMethods.IsIconic(offList.Handle), TimeSpan.FromSeconds(2)),
            "The blocked window did not go down once its app responded again.");
    }

    [Fact]
    public void RememberAllowed_says_whether_the_window_is_now_the_fallback()
    {
        const int SwHide = 0;
        using var allowed = TestWindow.Open("Anchor test allowed app");
        using var hidden = TestWindow.Open("Anchor test hidden allowed app");
        using var offList = TestWindow.Open("Anchor test off-list app");
        NativeMethods.ShowWindow(hidden.Handle, SwHide);
        var enforcer = new FocusEnforcer();

        // The caller keeps the app of the window it falls back to, so a paused
        // browser's window is never fallen back to (#452): it has to know which
        // windows the enforcer turned down (#437).
        Assert.True(enforcer.RememberAllowed(allowed.Handle));
        Assert.False(enforcer.RememberAllowed(hidden.Handle));
        Assert.False(enforcer.RememberAllowed(IntPtr.Zero));

        // Turned down, they left the allowed window the fallback.
        Assert.True(enforcer.Block(offList.Handle));
    }

    [Fact]
    public void A_minimized_fallback_is_back_on_screen_when_Block_returns()
    {
        var log = new ListLogger<FocusEnforcer>();
        using var allowed = TestWindow.Open("Anchor test allowed app");
        using var offList = TestWindow.Open("Anchor test off-list app");
        var enforcer = new FocusEnforcer(log);
        enforcer.RememberAllowed(allowed.Handle);
        allowed.MinimizeWithoutActivating();

        Assert.True(enforcer.Block(offList.Handle));

        // Back before the blocked window goes down, or Windows has no fallback on
        // screen to hand the foreground to (#438). Restoring it from a thread of its
        // own must not lose that (#451).
        Assert.True(allowed.IsBackOnScreen, "The minimized fallback was not back on screen when Block returned.");
    }

    [Fact]
    public async Task Restoring_a_fallback_app_that_is_not_responding_never_holds_up_the_agent()
    {
        var log = new ListLogger<FocusEnforcer>();
        using var allowed = TestWindow.Open("Anchor test hung allowed app");
        using var offList = TestWindow.Open("Anchor test off-list app");
        using var nextOffList = TestWindow.Open("Anchor test next off-list app");
        var enforcer = new FocusEnforcer(log);
        enforcer.RememberAllowed(allowed.Handle);
        allowed.MinimizeWithoutActivating();
        allowed.Freeze();

        // The student opens an off-list app, then another, while the app the agent
        // falls back to is not responding. Restoring it from the agent's thread
        // waited until it responded, and held up every foreground change behind it
        // meanwhile (#451).
        var clock = Stopwatch.StartNew();
        Assert.True(enforcer.Block(offList.Handle));
        var firstBlock = clock.Elapsed;
        clock.Restart();
        Assert.True(enforcer.Block(nextOffList.Handle));
        var nextBlock = clock.Elapsed;

        Assert.True(
            firstBlock < TimeSpan.FromSeconds(1),
            $"Block took {firstBlock.TotalMilliseconds:N0} ms while the fallback app was not responding.");
        Assert.True(
            nextBlock < TimeSpan.FromSeconds(1),
            $"The next Block took {nextBlock.TotalMilliseconds:N0} ms while the fallback app was not responding.");
        // Both go down all the same.
        Assert.True(
            await WaitUntilAsync(() => offList.HasGoneDown && nextOffList.HasGoneDown, TimeSpan.FromSeconds(2)),
            "The blocked windows did not go down while the fallback app was not responding.");
        Assert.True(
            await WaitUntilAsync(() => log.Messages.Any(m => m.Contains("did not come back")), TimeSpan.FromSeconds(2)),
            "The agent did not log that the fallback app is not responding.");
        Assert.False(allowed.IsBackOnScreen, "A window whose thread is frozen came back on screen.");

        // The restore is still on its way: the fallback comes back the moment its
        // app responds.
        allowed.Thaw();
        Assert.True(
            await WaitUntilAsync(() => allowed.IsBackOnScreen, TimeSpan.FromSeconds(2)),
            "The fallback did not come back once its app responded again.");
    }

    [Fact]
    public async Task Minimizing_an_app_that_is_not_responding_never_holds_up_the_agent()
    {
        // The session-start sweep and the paused browser minimize from the agent (#440).
        var log = new ListLogger<FocusEnforcer>();
        using var offList = TestWindow.Open("Anchor test hung off-list app");
        var enforcer = new FocusEnforcer(log);
        offList.Freeze();

        var clock = Stopwatch.StartNew();
        enforcer.Minimize(offList.Handle);
        clock.Stop();

        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(1),
            $"Minimize took {clock.ElapsedMilliseconds} ms while the off-list app was not responding.");
        Assert.True(
            await WaitUntilAsync(() => log.Messages.Any(m => m.Contains("did not minimize")), TimeSpan.FromSeconds(3)),
            "The agent did not log that the app is not responding.");
        Assert.False(offList.HasGoneDown, "A window whose thread is frozen went down.");

        // The minimize is still on its way: the window goes down the moment its app
        // responds.
        offList.Thaw();
        Assert.True(
            await WaitUntilAsync(() => offList.HasGoneDown, TimeSpan.FromSeconds(2)),
            "The window did not go down once its app responded again.");
    }

    [Fact]
    public async Task A_window_minimized_from_the_agent_goes_down_without_a_warning()
    {
        var log = new ListLogger<FocusEnforcer>();
        using var offList = TestWindow.Open("Anchor test off-list app");
        var enforcer = new FocusEnforcer(log);

        enforcer.Minimize(offList.Handle);

        // Down by the time Minimize returns, as when it ran on the caller's thread:
        // the overlay the caller shows next must come up after the window went down
        // (#440).
        Assert.True(offList.HasGoneDown, "The window was not down yet when Minimize returned.");
        await Task.Delay(FocusEnforcer.OwnThreadMinimizeTimeout + TimeSpan.FromMilliseconds(300));
        Assert.DoesNotContain(log.Messages, m => m.Contains("did not minimize"));
    }

    [Fact]
    public async Task Without_a_fallback_Block_leaves_the_window_to_the_overlay()
    {
        using var offList = TestWindow.Open("Anchor test off-list app");
        var enforcer = new FocusEnforcer();

        Assert.False(enforcer.Block(offList.Handle));

        // Minimized from the agent, it would keep the foreground and the keyboard
        // (#441): the overlay takes it down with HandOverAsync.
        await Task.Delay(300);
        Assert.False(NativeMethods.IsIconic(offList.Handle), "Block minimized the window it leaves to the overlay.");
    }

    [Fact]
    public async Task Handing_a_window_over_puts_the_overlay_right_below_it_and_takes_it_down()
    {
        var log = new ListLogger<FocusEnforcer>();
        using var overlay = TestWindow.Open("Anchor test overlay");
        using var offList = TestWindow.Open("Anchor test off-list app");
        var enforcer = new FocusEnforcer(log);
        // Held up for a moment, so it is still on screen when the test looks: a
        // minimized window drops to the bottom of the Z-order.
        offList.Freeze();

        var handOver = enforcer.HandOverAsync(offList.Handle, overlay.Handle);

        // Windows hands the foreground to the window below the one that goes down.
        Assert.Equal(overlay.Handle, NextVisibleWindowBelow(offList.Handle));
        offList.Thaw();
        await handOver;
        Assert.True(
            await WaitUntilAsync(() => NativeMethods.IsIconic(offList.Handle), TimeSpan.FromSeconds(2)),
            "The blocked window was not minimized.");
        Assert.False(NativeMethods.IsIconic(overlay.Handle), "The overlay was minimized.");
        Assert.DoesNotContain(log.Messages, m => m.Contains("did not minimize"));
    }

    [Fact]
    public async Task Handing_over_from_an_app_that_is_not_responding_never_holds_up_the_agent()
    {
        var log = new ListLogger<FocusEnforcer>();
        using var overlay = TestWindow.Open("Anchor test overlay");
        using var offList = TestWindow.Open("Anchor test hung off-list app");
        var enforcer = new FocusEnforcer(log);
        offList.Freeze();

        var clock = Stopwatch.StartNew();
        var handOver = enforcer.HandOverAsync(offList.Handle, overlay.Handle);
        var returnedAfter = clock.Elapsed;
        var tookOver = await handOver;
        clock.Stop();

        Assert.True(
            returnedAfter < TimeSpan.FromMilliseconds(500),
            $"HandOverAsync held up its caller for {returnedAfter.TotalMilliseconds:N0} ms while the app was not responding.");
        Assert.True(
            clock.Elapsed < FocusEnforcer.OwnThreadMinimizeTimeout + TimeSpan.FromSeconds(1),
            $"The hand-over took {clock.ElapsedMilliseconds} ms while the app was not responding.");
        Assert.False(tookOver, "The overlay took the foreground over from an app that was not responding.");
        Assert.True(
            await WaitUntilAsync(() => log.Messages.Any(m => m.Contains("did not minimize")), TimeSpan.FromSeconds(2)),
            "The agent did not log that the blocked app is not responding.");

        offList.Thaw();
        Assert.True(
            await WaitUntilAsync(() => NativeMethods.IsIconic(offList.Handle), TimeSpan.FromSeconds(2)),
            "The blocked window did not go down once its app responded again.");
    }

    /// <summary>The first visible window below <paramref name="hwnd"/> in the Z-order.</summary>
    private static nint NextVisibleWindowBelow(nint hwnd)
    {
        var next = hwnd;
        while ((next = NativeMethods.GetWindow(next, NativeMethods.GW_HWNDNEXT)) != IntPtr.Zero)
        {
            if (NativeMethods.IsWindowVisible(next))
                return next;
        }
        return IntPtr.Zero;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _messages.Enqueue(formatter(state, exception));
    }
}
