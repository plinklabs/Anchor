using System.Runtime.Versioning;
using FocusAgent.Native;

namespace FocusAgent.Native.Tests;

[SupportedOSPlatform("windows")]
public class FocusEnforcerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_app_window_without_an_owner_is_minimized_itself(bool windowShown)
    {
        Assert.Equal((false, true), FocusEnforcer.PlanMinimize(
            hasVisibleOwner: false, ownerMinimized: false, windowShown: windowShown));
    }

    [Fact]
    public void A_dialog_goes_down_with_the_app_window_that_owns_it()
    {
        // #428: the file picker PickerHost shows for an Edge upload is owned by the
        // Edge window. Minimizing the owner hides the picker with it, and brings it
        // back when the app comes back; the picker itself is left as it is.
        Assert.Equal((true, false), FocusEnforcer.PlanMinimize(
            hasVisibleOwner: true, ownerMinimized: false, windowShown: true));
    }

    [Fact]
    public void A_dialog_on_screen_while_its_owner_is_already_minimized_is_minimized_itself()
    {
        // Minimizing an owner that is already down hides nothing, so a dialog that
        // came up on its own (a paused Edge's picker, say) must go down by itself.
        Assert.Equal((false, true), FocusEnforcer.PlanMinimize(
            hasVisibleOwner: true, ownerMinimized: true, windowShown: true));
    }

    [Fact]
    public void A_dialog_already_out_of_sight_with_its_minimized_owner_is_left_alone()
    {
        // Minimizing a hidden dialog would put it back on screen as a title bar.
        Assert.Equal((false, false), FocusEnforcer.PlanMinimize(
            hasVisibleOwner: true, ownerMinimized: true, windowShown: false));
    }

    // --- #437: where focus goes after a block ---------------------------------

    [Theory]
    [InlineData("Shell_TrayWnd")]                 // the taskbar
    [InlineData("Shell_SecondaryTrayWnd")]        // the taskbar on another monitor
    [InlineData("XamlExplorerHostIslandWindow")]  // the taskbar's thumbnail flyout
    [InlineData("Progman")]                       // the desktop
    [InlineData("WorkerW")]                       // the desktop
    [InlineData("shell_traywnd")]                 // window classes ignore case
    public void Explorers_shell_windows_are_never_the_focus_fallback(string className)
    {
        // Explorer is allowed in every session, so a click on the taskbar made it
        // the window to hand focus back to: the student landed on the taskbar, and
        // re-showing the closing thumbnail flyout crashed explorer.
        Assert.False(FocusEnforcer.IsFallbackCandidate(className, visible: true));
    }

    [Theory]
    [InlineData("CabinetWClass")]       // a File Explorer window
    [InlineData("Notepad")]
    [InlineData("Chrome_WidgetWin_1")]  // Edge, and Electron apps such as Teams
    [InlineData("XLMAIN")]              // Excel
    [InlineData("#32770")]              // a dialog, such as the file picker of an allowed app (#428)
    [InlineData(null)]                  // class unknown
    public void An_app_window_on_screen_is_the_focus_fallback(string? className)
    {
        Assert.True(FocusEnforcer.IsFallbackCandidate(className, visible: true));
    }

    [Fact]
    public void A_hidden_window_is_never_the_focus_fallback()
    {
        Assert.False(FocusEnforcer.IsFallbackCandidate("Notepad", visible: false));
    }

    [Fact]
    public void A_minimized_fallback_is_restored()
    {
        // Back on screen before the blocked window goes down, so Windows can hand it
        // the foreground (#438).
        Assert.Equal(FocusEnforcer.FallbackPlan.Restore, FocusEnforcer.PlanFallback(
            sameAppAsBlocked: false, visible: true, minimized: true));
    }

    [Fact]
    public void A_fallback_on_screen_is_left_as_it_is()
    {
        // No ShowWindow on a window that is already up: its state is left as it is.
        Assert.Equal(FocusEnforcer.FallbackPlan.OnScreen, FocusEnforcer.PlanFallback(
            sameAppAsBlocked: false, visible: true, minimized: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_fallback_that_went_hidden_is_never_shown_again(bool minimized)
    {
        // Showing it would put back a window its app hid: the taskbar's thumbnail
        // flyout, shown again while it closed, crashed explorer (#437).
        Assert.Equal(FocusEnforcer.FallbackPlan.None, FocusEnforcer.PlanFallback(
            sameAppAsBlocked: false, visible: false, minimized: minimized));
    }

    [Fact]
    public void Focus_never_goes_back_to_the_app_just_blocked()
    {
        Assert.Equal(FocusEnforcer.FallbackPlan.None, FocusEnforcer.PlanFallback(
            sameAppAsBlocked: true, visible: true, minimized: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_blocked_window_on_screen_hands_the_overlay_the_foreground_by_minimizing_itself(bool hasForeground)
    {
        // It hands the foreground on as it goes down (#441).
        Assert.Equal(FocusEnforcer.HandOverPlan.Minimize, FocusEnforcer.PlanHandOver(
            minimized: false, hasForeground: hasForeground, isAppWindow: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_minimized_window_without_the_foreground_is_left_as_it_is(bool isAppWindow)
    {
        // It has nothing to hand on, and putting the overlay below it, at the bottom
        // of the Z-order, would lose the overlay the foreground.
        Assert.Equal(FocusEnforcer.HandOverPlan.AlreadyDown, FocusEnforcer.PlanHandOver(
            minimized: true, hasForeground: false, isAppWindow: isAppWindow));
    }

    [Fact]
    public void A_minimized_window_that_has_the_foreground_is_restored_to_hand_it_over()
    {
        // Minimizing it again hands nothing on, so it would keep the keyboard: the
        // first step of a click on its taskbar button activates it while minimized.
        Assert.Equal(FocusEnforcer.HandOverPlan.RestoreFirst, FocusEnforcer.PlanHandOver(
            minimized: true, hasForeground: true, isAppWindow: true));
    }

    [Fact]
    public void A_dialog_is_never_restored_to_hand_it_over()
    {
        // A dialog goes down with the window that owns it (#428).
        Assert.Equal(FocusEnforcer.HandOverPlan.Minimize, FocusEnforcer.PlanHandOver(
            minimized: true, hasForeground: true, isAppWindow: false));
    }

    [Fact]
    public void A_paused_window_in_the_foreground_is_the_window_the_overlay_takes_over_from()
    {
        // #450: the paused browser window that has the keyboard goes to the overlay.
        Assert.True(FocusEnforcer.IsWindowOrItsDialog(window: 0x20, windowsAppWindow: 0x20, appWindow: 0x20));
    }

    [Fact]
    public void A_dialog_of_a_paused_window_in_the_foreground_is_the_window_the_overlay_takes_over_from()
    {
        // The file picker of a paused Edge window has the keyboard: it goes to the
        // overlay, and down with the Edge window that owns it (#428).
        Assert.True(FocusEnforcer.IsWindowOrItsDialog(window: 0x700, windowsAppWindow: 0x20, appWindow: 0x20));
    }

    [Fact]
    public void Another_apps_window_in_the_foreground_leaves_a_paused_window_to_be_minimized()
    {
        Assert.False(FocusEnforcer.IsWindowOrItsDialog(window: 0x10, windowsAppWindow: 0x10, appWindow: 0x20));
    }
}
