using FocusAgent.Core.Focus;
using FocusAgent.Core.Tamper;

namespace FocusAgent.Core.Tests;

/// <summary>
/// Whether the paused-Edge overlay's button brings Edge forward after its action
/// (#597). Not when the action opens a window of one profile itself: with several
/// profiles open, "Edge" can be another profile's window.
/// </summary>
public class BrowserPausedPromptTests
{
    [Theory]
    // The action only opens the repair window: the overlay brings Edge forward.
    [InlineData(ExtensionPauseReason.ExtensionOff, true)]
    [InlineData(ExtensionPauseReason.NoSiteAccess, true)]
    [InlineData(ExtensionPauseReason.NoSchoolProfile, true)]
    // #525: it brings the school profile's window forward itself.
    [InlineData(ExtensionPauseReason.OutsideSchoolProfile, false)]
    // #536: it opens the store listing in the school profile.
    [InlineData(ExtensionPauseReason.SchoolProfileWithoutExtension, false)]
    // #556, #597: it opens the profile the extension was woken in.
    [InlineData(ExtensionPauseReason.NotConnected, false)]
    public void The_button_brings_Edge_forward_only_when_its_action_opens_no_profile_itself(
        ExtensionPauseReason reason, bool bringsBrowserForward)
    {
        var prompt = new BrowserPausedPrompt(
            new AllowedAppRule { MatchKind = AllowedAppMatchKind.ProcessName, Value = "msedge" },
            TimeSpan.FromSeconds(60),
            () => true,
            reason);

        Assert.Equal(bringsBrowserForward, prompt.BringsBrowserForward);
    }

    [Fact]
    public void Every_reason_is_decided()
    {
        // A new reason must be placed in the theory above: does its action open a
        // window of one profile itself?
        Assert.Equal(6, Enum.GetValues<ExtensionPauseReason>().Length);
    }
}
