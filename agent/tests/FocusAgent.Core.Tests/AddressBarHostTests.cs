using FocusAgent.Core.Dtos;
using FocusAgent.Core.Extension;
using FocusAgent.Core.Tamper;

namespace FocusAgent.Core.Tests;

/// <summary>
/// #526: the host of the page Edge's address bar shows, as Edge 157 shows it,
/// and the allowlist matched as the extension matches it.
/// </summary>
public sealed class AddressBarHostTests
{
    [Theory]
    // An http page without its scheme, its port and path and query left out.
    [InlineData("offlist.test:8099/some/path?q=secret", "offlist.test")]
    [InlineData("example.com", "example.com")]
    [InlineData("127.0.0.1:5276/allowed", "127.0.0.1")]
    [InlineData("localhost:5173", "localhost")]
    // An https page in full.
    [InlineData("https://allowed.test/x?y=1", "allowed.test")]
    [InlineData("HTTPS://Www.Example.COM/A", "www.example.com")]
    [InlineData("http://example.com./", "example.com")]
    // An internationalized name as the extension reads it: punycode.
    [InlineData("https://bücher.example/", "xn--bcher-kva.example")]
    public void The_host_of_a_web_page(string shown, string host) =>
        Assert.Equal(host, AddressBarHost.Of(shown));

    [Theory]
    // The new-tab page shows nothing.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    // The block page, and Edge's own pages.
    [InlineData("extension://dnkimhodjfogjibnbbfdjdapgmmiojio/block-page.html?url=http%3A%2F%2Foff.test%2F")]
    [InlineData("chrome-extension://dnkimhodjfogjibnbbfdjdapgmmiojio/block-page.html")]
    [InlineData("edge://settings")]
    [InlineData("edge://newtab/")]
    [InlineData("about:blank")]
    [InlineData("view-source:https://example.com/")]
    [InlineData("data:text/html,hi")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("ftp://example.com/file")]
    // A local file, as its path, and a share.
    [InlineData("C:/Windows/win.ini")]
    [InlineData(@"C:\Users\student\notes.pdf")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("file:///C:/Windows/win.ini")]
    // Search terms left in it.
    [InlineData("how to bypass anchor")]
    public void Nothing_for_what_is_no_web_page(string? shown) =>
        Assert.Null(AddressBarHost.Of(shown));

    private static readonly AllowedDomainDto[] Domains =
    {
        new("Exact", "127.0.0.1"),
        new("Wildcard", "*.microsoftonline.com"),
        new("Suffix", "school.be"),
        new("Wildcard", "sharepoint.com"),
        new("Exact", "  Docs.Example.com "),
        new("Unknown", "anything.test"),
        new("Wildcard", "*."),
    };

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("login.microsoftonline.com", true)]
    [InlineData("microsoftonline.com", true)]
    [InlineData("evilmicrosoftonline.com", false)]
    [InlineData("school.be", true)]
    [InlineData("www.school.be", true)]
    [InlineData("team.sharepoint.com", true)]
    [InlineData("docs.example.com", true)]
    [InlineData("example.com", false)]
    [InlineData("anything.test", false)]
    [InlineData("offlist.test", false)]
    [InlineData("", false)]
    public void The_allowlist_is_matched_as_the_extension_matches_it(string host, bool allowed) =>
        Assert.Equal(allowed, SiteAllowlist.Allows(Domains, host));
}
