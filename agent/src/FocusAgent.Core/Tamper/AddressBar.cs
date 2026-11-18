using System.Text.RegularExpressions;

namespace FocusAgent.Core.Tamper;

/// <summary>What the address bar of an Edge window shows (#526).</summary>
/// <param name="Text">Its text, as Edge shows it.</param>
/// <param name="HasFocus">
/// Whether it has the keyboard: then the student may be typing in it, and the
/// text is what they typed, not the page that is open.
/// </param>
public sealed record AddressBarReading(string Text, bool HasFocus);

/// <summary>
/// Reads Edge's address bar from outside the browser (#526): the Native
/// implementation goes through UI Automation. The indirection keeps
/// <see cref="AddressBarWitness"/> testable without a browser.
/// </summary>
public interface IAddressBarReader
{
    /// <summary>
    /// The foreground window, when it is a top-level window of
    /// <paramref name="processName"/>; 0 otherwise.
    /// </summary>
    nint ForegroundWindowOf(string processName);

    /// <summary>
    /// What the address bar of <paramref name="windowHandle"/> shows now; null
    /// when the window has none that can be read (it is gone, it is no browser
    /// window, or Edge's UI changed). Never throws.
    /// </summary>
    AddressBarReading? Read(nint windowHandle);
}

/// <summary>
/// The host of the web page an Edge address bar shows (#526), or null for
/// anything that is no web page. The extension filters only http(s) pages
/// (<c>isUrlAllowed</c> in <c>extension/src/shared/host-matcher.ts</c>), so
/// nothing else is held against the allowlist. As Edge 157 shows them (measured):
/// <list type="bullet">
/// <item>An https page in full (<c>https://example.com/a?b</c>), an http page
/// without its scheme (<c>example.com:8080/a?b</c>).</item>
/// <item>The block page with its own scheme
/// (<c>extension://&lt;id&gt;/block-page.html?…</c>), and Edge's own pages likewise
/// (<c>edge://settings</c>, <c>about:blank</c>).</item>
/// <item>The new-tab page as nothing at all, a local file as its path
/// (<c>C:/Users/…</c>).</item>
/// </list>
/// Only the host is kept: the path and query string can hold search terms and
/// other personal detail.
/// </summary>
public static class AddressBarHost
{
    // A scheme Edge shows as it is, like about:blank or data:…; not host:port.
    private static readonly Regex Scheme = new(@"^[A-Za-z][A-Za-z0-9+.\-]*:(?![0-9])", RegexOptions.CultureInvariant);

    /// <summary>The lower-case host of the http(s) page <paramref name="text"/> shows, or null.</summary>
    public static string? Of(string? text)
    {
        var value = text?.Trim();
        // Nothing (the new-tab page), or text with spaces in it: no address.
        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace))
            return null;
        // A local file (C:/…) or a share (\\server\…).
        if (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '/' or '\\')
            return null;
        if (value.StartsWith(@"\\", StringComparison.Ordinal))
            return null;

        string address;
        if (value.Contains("://", StringComparison.Ordinal))
            address = value;
        else if (Scheme.IsMatch(value))
            return null;
        else
            address = "http://" + value;

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;
        // IdnHost: lower case, and punycode for an internationalized name, as the
        // extension reads a URL's hostname and the backend writes the allowlist.
        var host = uri.IdnHost.TrimEnd('.');
        return host.Length == 0 ? null : host.ToLowerInvariant();
    }
}
