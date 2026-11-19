using System.Diagnostics;
using System.Runtime.Versioning;
using FocusAgent.Native;

namespace FocusAgent.Native.Tests;

/// <summary>
/// #536: the command line that opens Edge in a profile, at an address when one is
/// given: the store listing that adds the extension to a school profile without
/// it. A profile folder and an address each stay one argument, whatever they hold.
/// </summary>
[SupportedOSPlatform("windows")]
public class BrowserProfileLauncherTests
{
    private const string StoreListing = "https://microsoftedge.microsoft.com/addons/detail/dnkimhodjfogjibnbbfdjdapgmmiojio";

    [Fact]
    public void Edge_is_opened_in_the_profile_alone()
    {
        Assert.Equal("--profile-directory=\"Profile 1\"", BrowserProfileLauncher.ArgumentsFor("Profile 1", null));
    }

    [Fact]
    public void Edge_is_opened_in_the_profile_at_the_store_listing()
    {
        Assert.Equal(
            $"--profile-directory=\"Default\" \"{StoreListing}\"",
            BrowserProfileLauncher.ArgumentsFor("Default", StoreListing));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData("Profile 1\" --incognito \"", null)]
    [InlineData("Profile 1", "")]
    [InlineData("Profile 1", "not an address")]
    [InlineData("Profile 1", "edge://extensions")]
    [InlineData("Profile 1", "file:///C:/Windows/notepad.exe")]
    [InlineData("Profile 1", "https://example.com/a b")]
    [InlineData("Profile 1", "https://example.com/\" --incognito \"")]
    public void Nothing_that_is_no_profile_folder_or_web_address_is_opened(string profile, string? url)
    {
        Assert.Null(BrowserProfileLauncher.ArgumentsFor(profile, url));
    }

    [Fact]
    public void A_stand_in_browser_is_never_started_but_what_it_was_asked_to_open_is_kept()
    {
        // The e2e stands its own process in for Edge: nothing is started on the
        // developer's desktop, and /status still shows what the button opened.
        using var current = Process.GetCurrentProcess();
        var launcher = new BrowserProfileLauncher(current.ProcessName);
        Assert.Null(launcher.LastOpened);

        launcher.OpenProfile("Profile 2", StoreListing);
        Assert.Equal(new BrowserProfileOpened("Profile 2", StoreListing), launcher.LastOpened);

        launcher.OpenProfile("Profile 1");
        Assert.Equal(new BrowserProfileOpened("Profile 1", null), launcher.LastOpened);

        // What can't be opened isn't kept either.
        launcher.OpenProfile("Profile 3", "edge://extensions");
        Assert.Equal(new BrowserProfileOpened("Profile 1", null), launcher.LastOpened);
    }
}
