using FocusAgent.Core.Dtos;
using FocusAgent.Core.Extension;
using FocusAgent.Core.Tamper;

namespace FocusAgent.Core.Tests;

/// <summary>
/// #556: the page the agent opens to wake an extension that is installed but
/// never connected. A navigation wakes its service worker; the page is one the
/// session allows, so it isn't blocked once the session is handed down.
/// </summary>
public sealed class ExtensionWakeUpTests
{
    [Fact]
    public void The_first_allowed_domain_is_opened_over_https()
    {
        var url = ExtensionWakeUp.WakeUrl([new AllowedDomainDto("Exact", "www.office.com"), new AllowedDomainDto("Suffix", "smartschool.be")]);

        Assert.Equal("https://www.office.com/", url);
    }

    [Fact]
    public void A_wildcard_domain_is_opened_at_its_own_host()
    {
        Assert.Equal("https://smartschool.be/", ExtensionWakeUp.WakeUrl([new AllowedDomainDto("Wildcard", " *.SmartSchool.be ")]));
    }

    [Fact]
    public void A_value_that_is_no_host_name_is_skipped()
    {
        var url = ExtensionWakeUp.WakeUrl(
        [
            new AllowedDomainDto("Exact", ""),
            new AllowedDomainDto("Exact", "https://bad.example/x"),
            new AllowedDomainDto("Wildcard", "*."),
            new AllowedDomainDto("Exact", "ok.example"),
        ]);

        Assert.Equal("https://ok.example/", url);
    }

    [Fact]
    public void With_no_allowed_domain_the_extension_s_store_listing_is_opened()
    {
        Assert.Equal(EdgeExtensionPolicy.StoreListingUrl, ExtensionWakeUp.WakeUrl([]));
    }

    [Fact]
    public void Waking_opens_the_profile_at_that_page()
    {
        var launcher = new RecordingLauncher();
        var wake = new ExtensionWakeUp(
            new NoProbe(), launcher, () => [new AllowedDomainDto("Wildcard", "*.smartschool.be")]);

        Assert.Equal("https://smartschool.be/", wake.Wake("Profile 1"));
        Assert.Equal(new[] { ("Profile 1", (string?)"https://smartschool.be/") }, launcher.Opened);
    }

    private sealed class RecordingLauncher : IBrowserProfileLauncher
    {
        public List<(string Profile, string? Url)> Opened { get; } = new();
        public void CloseWindow(nint windowHandle) { }
        public void OpenProfile(string profile, string? url = null) => Opened.Add((profile, url));
    }

    private sealed class NoProbe : IOpenProfileExtensionProbe
    {
        public IReadOnlyList<string> FindSwitchedOff() => [];
        public ProfileExtensionState ExtensionState(string profile) => ProfileExtensionState.Absent;
        public IReadOnlyList<ProfileWithExtension> FindWithExtension(bool openOnly) => [];
    }
}
