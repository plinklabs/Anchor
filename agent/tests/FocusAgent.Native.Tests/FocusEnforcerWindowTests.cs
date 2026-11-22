using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using FocusAgent.Native;
using FocusAgent.Native.Win32;
using Microsoft.Extensions.Logging;

namespace FocusAgent.Native.Tests;

/// <summary>
/// <see cref="FocusEnforcer.Block"/>, <see cref="FocusEnforcer.Minimize"/> and
/// <see cref="FocusEnforcer.BringForward"/> on real windows (#438, #440, #451,
/// #453). The agent never waits for an app, whether the window minimizes itself,
/// the agent minimizes it, or the agent restores the fallback or an app the
/// student picked on the overlay; where the foreground goes then needs
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

    /// <summary>
    /// #563: another app restores the off-list window from a thread of its own.
    /// Windows carries that out in steps it sends to the window's thread: the first
    /// raises an event the agent blocks the window on, and the window's thread can
    /// carry the block's minimize out before the next step, which puts the window
    /// back on screen while it counts as minimized. The restore's second event
    /// blocks it again, and that minimize changes nothing. The spec holds the restore
    /// in the middle until the first block's minimize has landed there, then blocks
    /// the window again, as the second event does.
    /// </summary>
    [Fact]
    public async Task A_block_whose_minimize_lands_in_the_middle_of_a_restore_from_another_thread_still_takes_the_window_down()
    {
        var log = new ListLogger<FocusEnforcer>();
        using var allowed = TestWindow.Open("Anchor test allowed app");
        using var offList = TestWindow.Open("Anchor test off-list app");
        var enforcer = new FocusEnforcer(log);
        enforcer.RememberAllowed(allowed.Handle);
        var restoredTo = offList.Bounds;
        offList.MinimizeWithoutActivating();

        // The first event of the restore: the agent blocks the window while the
        // restore is under way, and the window's thread carries the block's minimize
        // out between the restore's steps.
        offList.MinimizeInTheMiddleOfTheNextRestore();
        var firstBlock = Task.Run(() =>
            offList.WaitUntilInTheMiddleOfARestore(TimeSpan.FromSeconds(5)) && enforcer.Block(offList.Handle));
        offList.RestoreWithoutActivating();
        Assert.True(await firstBlock, "The restore was not held in the middle for the agent to block the window.");
        Assert.True(offList.MinimizedInTheMiddleOfARestore, "The block's minimize did not land in the middle of the restore.");
        Assert.True(
            offList.CountsAsMinimizedOnScreen,
            "The rest of the restore did not put the window back on screen while it counts as minimized; the spec " +
            "does not reproduce #563.");

        // The second event of the restore blocks the window once more.
        Assert.True(enforcer.Block(offList.Handle));

        Assert.True(
            await WaitUntilAsync(() => offList.HasGoneDown, TimeSpan.FromSeconds(3)),
            "A block whose minimize landed in the middle of a restore from another thread left the window on screen, " +
            "counted as minimized (#563).");
        Assert.False(NativeMethods.IsIconic(allowed.Handle), "The allowed window was minimized.");
        Assert.DoesNotContain(log.Messages, m => m.Contains("did not minimize"));

        // Put down from there, the window goes back to where it was when it is
        // restored, not to where Windows parks a minimized window, out of sight.
        offList.RestoreWithoutActivating();
        Assert.True(offList.IsBackOnScreen, "The window did not come back on screen once restored.");
        Assert.Equal(restoredTo, offList.Bounds);
    }

    /// <summary>
    /// #580: the other order of #563. The block's minimize lands in the middle of a
    /// restore from another thread before the restore starts to move the window
    /// back on screen, as it does when a <c>SW_RESTORE</c> activates the window
    /// first. The window stays down, but Windows took its spot, parked off the
    /// screen, as the place it goes back to. The agent had blocked the window
    /// before, while it was on screen, so it knows where it was.
    /// </summary>
    [Fact]
    public async Task A_block_whose_minimize_lands_before_a_restore_moves_the_window_leaves_it_coming_back_where_it_was()
    {
        var log = new ListLogger<FocusEnforcer>();
        using var allowed = TestWindow.Open("Anchor test allowed app");
        using var offList = TestWindow.Open("Anchor test off-list app");
        var enforcer = new FocusEnforcer(log);
        enforcer.RememberAllowed(allowed.Handle);
        var restoredTo = offList.Bounds;

        // The agent blocks the window while it is on screen...
        Assert.True(enforcer.Block(offList.Handle));
        Assert.True(await WaitUntilAsync(() => offList.HasGoneDown, TimeSpan.FromSeconds(2)), "The blocked window did not go down.");
        await Task.Delay(FocusEnforcer.OwnThreadMinimizeTimeout + TimeSpan.FromMilliseconds(300));

        // ...then blocks it again in the middle of a restore from another thread,
        // before the restore moves it.
        await BlockBeforeARestoreMovesTheWindowAsync(enforcer, offList);

        Assert.True(
            await WaitUntilAsync(() => offList.RestoresOnAMonitor, TimeSpan.FromSeconds(3)),
            "A block whose minimize landed before a restore from another thread moved the window left it coming back " +
            "off every monitor (#580).");
        Assert.True(offList.HasGoneDown, "Putting back where the window goes back to brought it up.");
        Assert.False(NativeMethods.IsIconic(allowed.Handle), "The allowed window was minimized.");

        offList.RestoreWithoutActivating();
        Assert.True(offList.IsBackOnScreen, "The window did not come back on screen once restored.");
        Assert.Equal(restoredTo, offList.Bounds);
    }

    /// <summary>
    /// #580, for a window the agent never saw on screen: minimized before the
    /// agent started, it has no place to go back to that the agent knows of. It
    /// comes back on the primary monitor, where the student sees it.
    /// </summary>
    [Fact]
    public async Task A_block_whose_minimize_lands_before_a_restore_moves_a_window_the_agent_never_saw_brings_it_back_on_a_monitor()
    {
        var log = new ListLogger<FocusEnforcer>();
        using var allowed = TestWindow.Open("Anchor test allowed app");
        using var offList = TestWindow.Open("Anchor test off-list app");
        var enforcer = new FocusEnforcer(log);
        enforcer.RememberAllowed(allowed.Handle);
        offList.MinimizeWithoutActivating();

        await BlockBeforeARestoreMovesTheWindowAsync(enforcer, offList);

        Assert.True(
            await WaitUntilAsync(() => offList.RestoresOnAMonitor, TimeSpan.FromSeconds(3)),
            "A block whose minimize landed before a restore from another thread moved the window left it coming back " +
            "off every monitor (#580).");
        Assert.True(offList.HasGoneDown, "Putting back where the window goes back to brought it up.");

        offList.RestoreWithoutActivating();
        Assert.True(offList.IsBackOnScreen, "The window did not come back on screen once restored.");
        Assert.True(offList.RestoresOnAMonitor, "The window came back off every monitor.");
    }

    /// <summary>
    /// Restores the minimized <paramref name="offList"/> window from another thread
    /// and has <paramref name="enforcer"/> block it in the middle, before the
    /// restore moves it back on screen (#580). Checks the spec reproduces the
    /// issue: the window stays down, and goes back to where Windows parks it.
    /// </summary>
    private static async Task BlockBeforeARestoreMovesTheWindowAsync(FocusEnforcer enforcer, TestWindow offList)
    {
        offList.MinimizeInTheMiddleOfTheNextRestore(beforeTheMove: true);
        var block = Task.Run(() =>
            offList.WaitUntilInTheMiddleOfARestore(TimeSpan.FromSeconds(5)) && enforcer.Block(offList.Handle));
        offList.RestoreWithoutActivating();
        Assert.True(await block, "The restore was not held in the middle for the agent to block the window.");
        Assert.True(offList.MinimizedInTheMiddleOfARestore, "The block's minimize did not land in the middle of the restore.");
        Assert.True(offList.HasGoneDown, "The rest of the restore brought the window up; the spec does not reproduce #580.");
        Assert.False(
            offList.RestoresOnAMonitor,
            "The window goes back to a place on a monitor right after the restore; the spec does not reproduce #580.");
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
    public async Task Bringing_forward_an_app_that_is_not_responding_never_holds_up_the_agent()
    {
        var log = new ListLogger<FocusEnforcer>();
        using var allowed = TestWindow.Open("Anchor test hung allowed app");
        var enforcer = new FocusEnforcer(log);
        allowed.MinimizeWithoutActivating();
        allowed.Freeze();

        // The student picks the app on the overlay, which brings it forward on the
        // agent's UI thread. Restoring it there waited until it responded, and held
        // up every foreground change behind it meanwhile (#453).
        var clock = Stopwatch.StartNew();
        enforcer.BringForward(allowed.Handle);
        clock.Stop();

        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(1),
            $"BringForward took {clock.ElapsedMilliseconds} ms while the app was not responding.");
        Assert.True(
            await WaitUntilAsync(() => log.Messages.Any(m => m.Contains("did not come back")), TimeSpan.FromSeconds(2)),
            "The agent did not log that the app is not responding.");
        Assert.False(allowed.IsBackOnScreen, "A window whose thread is frozen came back on screen.");

        // The restore is still on its way: the app comes back the moment it responds.
        allowed.Thaw();
        Assert.True(
            await WaitUntilAsync(() => allowed.IsBackOnScreen, TimeSpan.FromSeconds(2)),
            "The app did not come back once it responded again.");
    }

    [Fact]
    public void A_minimized_app_is_back_on_screen_when_BringForward_returns()
    {
        using var allowed = TestWindow.Open("Anchor test allowed app");
        var enforcer = new FocusEnforcer();
        allowed.MinimizeWithoutActivating();

        enforcer.BringForward(allowed.Handle);

        // Back before it is given the foreground, as when the restore ran on the
        // caller's thread (#453).
        Assert.True(allowed.IsBackOnScreen, "The minimized app was not back on screen when BringForward returned.");
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
    public async Task The_window_with_the_foreground_minimizes_itself_for_the_sweep()
    {
        // #560: minimized from the agent, the window with the foreground stayed the
        // foreground window, and what the student typed reached the minimized app.
        // Minimizing itself, it hands the foreground on (#438), as a blocked window
        // does. Where the foreground goes then needs two processes and is covered by
        // the e2e ForegroundChangeReportTests.
        var log = new ListLogger<FocusEnforcer>();
        using var offList = TestWindow.Open("Anchor test off-list app with the foreground");
        var enforcer = new FocusEnforcer(log);

        enforcer.MinimizeForeground(offList.Handle);

        Assert.True(
            await WaitUntilAsync(() => offList.HasGoneDown, TimeSpan.FromSeconds(2)),
            "The window with the foreground was not minimized.");
        Assert.True(
            offList.MinimizedItself == true,
            "The window with the foreground was minimized by the agent's thread rather than its own: it stays the " +
            "foreground window and gets what the student types (#560).");
        await Task.Delay(FocusEnforcer.OwnThreadMinimizeTimeout + TimeSpan.FromMilliseconds(300));
        Assert.DoesNotContain(log.Messages, m => m.Contains("did not minimize"));
    }

    [Fact]
    public async Task A_window_minimized_from_the_agent_does_not_minimize_itself()
    {
        // The other side of the line the test above draws: the sweep's other windows,
        // which have no foreground to hand on, are minimized from the agent (#440).
        using var offList = TestWindow.Open("Anchor test off-list app");
        var enforcer = new FocusEnforcer();

        enforcer.Minimize(offList.Handle);

        Assert.True(
            await WaitUntilAsync(() => offList.HasGoneDown, TimeSpan.FromSeconds(2)),
            "The window was not minimized.");
        Assert.True(offList.MinimizedItself == false, "A window minimized from the agent minimized itself.");
    }

    [Fact]
    public async Task Minimizing_the_foreground_window_of_an_app_that_is_not_responding_never_holds_up_the_sweep()
    {
        var log = new ListLogger<FocusEnforcer>();
        using var offList = TestWindow.Open("Anchor test hung off-list app with the foreground");
        var enforcer = new FocusEnforcer(log);
        offList.Freeze();

        var clock = Stopwatch.StartNew();
        enforcer.MinimizeForeground(offList.Handle);
        clock.Stop();

        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(1),
            $"MinimizeForeground took {clock.ElapsedMilliseconds} ms while the off-list app was not responding.");
        Assert.True(
            await WaitUntilAsync(() => log.Messages.Any(m => m.Contains("did not minimize")), TimeSpan.FromSeconds(3)),
            "The agent did not log that the app is not responding.");
        Assert.False(offList.HasGoneDown, "A window whose thread is frozen went down.");

        // The request waits in the app's queue: the window goes down the moment its
        // app responds, and minimizes itself.
        offList.Thaw();
        Assert.True(
            await WaitUntilAsync(() => offList.HasGoneDown, TimeSpan.FromSeconds(2)),
            "The window did not go down once its app responded again.");
        Assert.True(offList.MinimizedItself == true, "The window that responded again did not minimize itself.");
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
    public async Task Handing_a_window_over_takes_the_overlay_out_of_the_topmost_band_and_the_window_down()
    {
        var log = new ListLogger<FocusEnforcer>();
        using var overlay = TestWindow.Open("Anchor test overlay");
        using var offList = TestWindow.Open("Anchor test off-list app");
        // The overlay comes up topmost.
        MakeTopmost(overlay.Handle);
        var enforcer = new FocusEnforcer(log);
        // Held up for a moment, so it is still on screen when the test looks: a
        // minimized window drops to the bottom of the Z-order.
        offList.Freeze();

        var handOver = enforcer.HandOverAsync(offList.Handle, overlay.Handle);

        // Windows hands the foreground of a window that goes down to the first window
        // from the top that is not topmost (#475). Where the overlay lands among the
        // others is up to Windows, which a test process in the background does not
        // always get its way with; the e2e ExtensionOffKeyboardTests sees the result.
        Assert.False(IsTopmost(overlay.Handle), "The overlay was left in the topmost band, where Windows passes it over.");
        offList.Thaw();
        await handOver;
        Assert.True(
            await WaitUntilAsync(() => NativeMethods.IsIconic(offList.Handle), TimeSpan.FromSeconds(2)),
            "The blocked window was not minimized.");
        Assert.False(NativeMethods.IsIconic(overlay.Handle), "The overlay was minimized.");
        Assert.DoesNotContain(log.Messages, m => m.Contains("did not minimize"));
    }

    [Fact]
    public async Task Out_of_the_topmost_band_the_overlay_goes_above_another_apps_window_without_going_back_through_it()
    {
        // #477: a hand-over while another one is under way finds the overlay out of
        // the topmost band, and another app's window can have come above it since.
        // The overlay goes above it in one move, never back through the band, where
        // a window that goes down meanwhile passes it over. From the agent, which
        // does not have the foreground, HWND_TOP leaves the overlay below the windows
        // of the app that has it, so the agent went through the band to get above
        // them; a window of this test cannot be one of those, so the e2e
        // ExtensionOffKeyboardTests puts one there. Here the other app's window is
        // an ordinary one, which the overlay must get above all the same.
        var log = new ListLogger<FocusEnforcer>();
        using var overlay = TestWindow.Open("Anchor test overlay");
        using var offList = TestWindow.Open("Anchor test off-list app");
        using var other = TestWindow.Open("Anchor test other app");
        var enforcer = new FocusEnforcer(log);
        // Held up for a moment, so it is still on screen when the test looks.
        offList.Freeze();
        Assert.True(IsAbove(other.Handle, overlay.Handle), "The other app's window did not come up above the overlay.");

        var handOver = enforcer.HandOverAsync(offList.Handle, overlay.Handle);

        Assert.True(
            overlay.TimesInTheTopmostBand == 0,
            "The hand-over took the overlay through the topmost band, where a window that goes down passes it over.");
        Assert.True(
            IsFirstAppWindowBut(overlay.Handle, offList.Handle),
            "The overlay is still below another app's window, which Windows hands the foreground to when the " +
            "window goes down.");
        offList.Thaw();
        await handOver;
        Assert.True(
            await WaitUntilAsync(() => NativeMethods.IsIconic(offList.Handle), TimeSpan.FromSeconds(2)),
            "The blocked window was not minimized.");
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

    [Fact]
    public async Task A_hand_over_its_caller_no_longer_wants_leaves_the_window_as_it_is()
    {
        // #474: the overlay gets to a hand-over a moment after it is asked for. A
        // paused browser whose extension reconnected meanwhile is the student's again.
        using var overlay = TestWindow.Open("Anchor test overlay");
        using var browser = TestWindow.Open("Anchor test browser no longer paused");
        var enforcer = new FocusEnforcer();

        var tookOver = await enforcer.HandOverAsync(browser.Handle, overlay.Handle, stillToGoDown: () => false);

        Assert.False(tookOver, "The overlay took the foreground over from a window that no longer had to go down.");
        await Task.Delay(FocusEnforcer.OwnThreadMinimizeTimeout);
        Assert.True(browser.IsBackOnScreen, "A hand-over its caller no longer wanted took the window down.");
    }

    [Fact]
    public async Task A_second_hand_over_while_the_first_minimize_is_on_its_way_asks_for_no_second_minimize()
    {
        // #474: the two events of one restore (MINIMIZEEND and FOREGROUND) each ask
        // the overlay for a hand-over, a moment apart. An app slow to get to the
        // minimizes carried the second out after a restore the student sent
        // meanwhile: the browser they brought back once its extension reconnected
        // went down again.
        var log = new ListLogger<FocusEnforcer>();
        using var overlay = TestWindow.Open("Anchor test overlay");
        using var browser = TestWindow.Open("Anchor test slow paused browser");
        MakeTopmost(overlay.Handle);
        var enforcer = new FocusEnforcer(log);
        browser.Freeze();

        var first = enforcer.HandOverAsync(browser.Handle, overlay.Handle);
        var second = enforcer.HandOverAsync(browser.Handle, overlay.Handle);

        Assert.Single(log.Messages, m => m.Contains("to minimize again"));
        // The overlay is out of the topmost band all the same, for when the first one
        // lands.
        Assert.False(IsTopmost(overlay.Handle), "The overlay was left in the topmost band, where Windows passes it over.");
        browser.Thaw();
        await Task.WhenAll(first, second);
        Assert.True(
            await WaitUntilAsync(() => browser.HasGoneDown, TimeSpan.FromSeconds(2)),
            "The window did not go down once its app responded again.");
    }

    [Fact]
    public async Task A_hand_over_after_the_window_went_down_and_came_back_takes_it_down_again()
    {
        // Once the window went down, the minimize asked of it is no longer on its
        // way: the next hand-over asks again (#474).
        var log = new ListLogger<FocusEnforcer>();
        using var overlay = TestWindow.Open("Anchor test overlay");
        using var browser = TestWindow.Open("Anchor test paused browser");
        MakeTopmost(overlay.Handle);
        var enforcer = new FocusEnforcer(log);
        await enforcer.HandOverAsync(browser.Handle, overlay.Handle);
        Assert.True(
            await WaitUntilAsync(() => browser.HasGoneDown, TimeSpan.FromSeconds(2)),
            "The window was not taken down by the first hand-over.");

        NativeMethods.ShowWindow(browser.Handle, NativeMethods.SW_SHOWNOACTIVATE);
        Assert.True(
            await WaitUntilAsync(() => browser.IsBackOnScreen, TimeSpan.FromSeconds(2)),
            "The window could not be brought back.");
        // As the overlay is again once a hand-over is done.
        MakeTopmost(overlay.Handle);
        await enforcer.HandOverAsync(browser.Handle, overlay.Handle);

        Assert.True(
            await WaitUntilAsync(() => browser.HasGoneDown, TimeSpan.FromSeconds(2)),
            "A hand-over after the window came back did not take it down.");
        Assert.DoesNotContain(log.Messages, m => m.Contains("to minimize again"));
    }

    [Fact]
    public async Task A_window_brought_back_while_it_goes_down_is_asked_to_go_down_once_more()
    {
        // #476: Windows marks a window minimized as its app starts on the minimize,
        // and says it started to minimize only once the app is done. The hand-over for
        // a restore that lands in between found the first minimize still on its way and
        // did not ask; once that one was done, the window was on screen and nothing
        // asked again.
        var log = new ListLogger<FocusEnforcer>();
        using var overlay = TestWindow.Open("Anchor test overlay");
        using var offList = TestWindow.Open("Anchor test off-list app brought back as it goes down");
        MakeTopmost(overlay.Handle);
        var enforcer = new FocusEnforcer(log);
        offList.RestoreWhenMinimized();

        var first = enforcer.HandOverAsync(offList.Handle, overlay.Handle);
        Assert.True(offList.WaitUntilHeldWhileMinimizing(TimeSpan.FromSeconds(2)), "The window was not asked to minimize.");
        Assert.True(offList.IsBackOnScreen, "The window was not brought back in the middle of its minimize.");
        // The hand-over for that restore.
        var second = enforcer.HandOverAsync(offList.Handle, overlay.Handle);
        Assert.Single(log.Messages, m => m.Contains("to minimize again"));
        offList.Thaw();
        await Task.WhenAll(first, second);

        Assert.True(
            await WaitUntilAsync(() => offList.HasGoneDown, TimeSpan.FromSeconds(2)),
            "The window brought back while it went down stayed on screen once the minimize on its way was done.");
    }

    [Fact]
    public async Task A_minimize_postponed_for_a_window_that_no_longer_has_to_go_down_is_never_asked()
    {
        // #474: the paused browser the student brings back once its extension
        // reconnected is theirs again. A minimize postponed during the pause (#476) is
        // not asked once the pause lifted.
        var log = new ListLogger<FocusEnforcer>();
        using var overlay = TestWindow.Open("Anchor test overlay");
        using var browser = TestWindow.Open("Anchor test paused browser brought back as it goes down");
        MakeTopmost(overlay.Handle);
        var enforcer = new FocusEnforcer(log);
        var paused = true;
        browser.RestoreWhenMinimized();

        var first = enforcer.HandOverAsync(browser.Handle, overlay.Handle, () => Volatile.Read(ref paused));
        Assert.True(browser.WaitUntilHeldWhileMinimizing(TimeSpan.FromSeconds(2)), "The window was not asked to minimize.");
        var second = enforcer.HandOverAsync(browser.Handle, overlay.Handle, () => Volatile.Read(ref paused));
        Assert.Single(log.Messages, m => m.Contains("to minimize again"));
        Volatile.Write(ref paused, false);
        browser.Thaw();
        await Task.WhenAll(first, second);

        await Task.Delay(FocusEnforcer.OwnThreadMinimizeTimeout);
        Assert.True(browser.IsBackOnScreen, "A minimize postponed during the pause took the browser down after it lifted.");
        Assert.DoesNotContain(log.Messages, m => m.Contains("once more"));
    }

    [Fact]
    public async Task A_minimize_postponed_before_the_session_ended_is_never_asked()
    {
        var log = new ListLogger<FocusEnforcer>();
        using var overlay = TestWindow.Open("Anchor test overlay");
        using var offList = TestWindow.Open("Anchor test off-list app brought back as it goes down");
        MakeTopmost(overlay.Handle);
        var enforcer = new FocusEnforcer(log);
        offList.RestoreWhenMinimized();

        var first = enforcer.HandOverAsync(offList.Handle, overlay.Handle);
        Assert.True(offList.WaitUntilHeldWhileMinimizing(TimeSpan.FromSeconds(2)), "The window was not asked to minimize.");
        var second = enforcer.HandOverAsync(offList.Handle, overlay.Handle);
        Assert.Single(log.Messages, m => m.Contains("to minimize again"));
        // The session ends: the window is the student's again.
        enforcer.Reset();
        offList.Thaw();
        await Task.WhenAll(first, second);

        await Task.Delay(FocusEnforcer.OwnThreadMinimizeTimeout);
        Assert.True(offList.IsBackOnScreen, "A minimize postponed before the session ended took the window down after it.");
        Assert.DoesNotContain(log.Messages, m => m.Contains("once more"));
    }

    [Fact]
    public async Task A_postponed_minimize_is_asked_once_the_one_on_its_way_is_overdue()
    {
        // The minimize on its way counts as such for no longer than a moment, for an
        // app that is not responding (#474); a minimize postponed meanwhile is asked
        // then, should the window be on screen still (#476).
        var log = new ListLogger<FocusEnforcer>();
        using var overlay = TestWindow.Open("Anchor test overlay");
        using var offList = TestWindow.Open("Anchor test hung off-list app");
        MakeTopmost(overlay.Handle);
        var enforcer = new FocusEnforcer(log);
        offList.Freeze();

        var first = enforcer.HandOverAsync(offList.Handle, overlay.Handle);
        var second = enforcer.HandOverAsync(offList.Handle, overlay.Handle);
        Assert.Single(log.Messages, m => m.Contains("to minimize again"));
        Assert.True(
            await WaitUntilAsync(
                () => log.Messages.Any(m => m.Contains("once more")),
                FocusEnforcer.MinimizeOnItsWayLimit + TimeSpan.FromSeconds(1)),
            "The postponed minimize was not asked once the one on its way was overdue.");
        offList.Thaw();
        await Task.WhenAll(first, second);
        Assert.True(
            await WaitUntilAsync(() => offList.HasGoneDown, TimeSpan.FromSeconds(2)),
            "The window did not go down once its app responded again.");
    }

    private static bool IsTopmost(nint hwnd) =>
        (NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) != 0;

    /// <summary>Whether <paramref name="upper"/> is above <paramref name="lower"/> in the Z-order.</summary>
    private static bool IsAbove(nint upper, nint lower)
    {
        for (var window = NativeMethods.GetWindow(upper, NativeMethods.GW_HWNDFIRST);
             window != IntPtr.Zero;
             window = NativeMethods.GetWindow(window, NativeMethods.GW_HWNDNEXT))
        {
            if (window == upper) return true;
            if (window == lower) return false;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="hwnd"/> is the first window from the top that Windows
    /// may hand the foreground of <paramref name="goingDown"/> to: on screen, not
    /// topmost and not a tool window (#475).
    /// </summary>
    private static bool IsFirstAppWindowBut(nint hwnd, nint goingDown)
    {
        for (var window = NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDFIRST);
             window != IntPtr.Zero;
             window = NativeMethods.GetWindow(window, NativeMethods.GW_HWNDNEXT))
        {
            if (window == hwnd) return true;
            if (window == goingDown || !NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window))
                continue;
            if ((NativeMethods.GetWindowLongW(window, NativeMethods.GWL_EXSTYLE) &
                 (NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW)) == 0)
                return false;
        }
        return false;
    }

    /// <summary>Puts <paramref name="hwnd"/> in the topmost band, as the overlay is shown.</summary>
    private static void MakeTopmost(nint hwnd) =>
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

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
