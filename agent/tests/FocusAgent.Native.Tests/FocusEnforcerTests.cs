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
}
