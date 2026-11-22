using FocusAgent.Core.Dtos;
using FocusAgent.Core.Extension;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Tamper;

/// <summary>
/// What <see cref="ExtensionGuard"/> uses when the extension is installed but its
/// link never came up since the agent started (#556). The witness link alone
/// can't tell that from a device without the extension, so this reads Edge's
/// profiles (<see cref="IOpenProfileExtensionProbe"/>): where the extension is
/// installed, and which of those profiles has a window open. And it tries to wake
/// the extension: it opens Edge in such a profile on an http(s) page
/// (<see cref="IBrowserProfileLauncher"/>), whose navigation wakes the
/// extension's service worker, which then connects at once. The page is one the
/// session allows, so it isn't blocked once the session is handed down.
///
/// Registering the host again, the other cheap repair, is already done when the
/// session is joined without a link (<see cref="WitnessLinkSelfCheckOnJoin"/>, #561).
/// </summary>
public sealed class ExtensionWakeUp
{
    private readonly IOpenProfileExtensionProbe _probe;
    private readonly IBrowserProfileLauncher _launcher;
    private readonly Func<IReadOnlyList<AllowedDomainDto>> _allowedDomains;
    private readonly ILogger<ExtensionWakeUp> _log;

    /// <param name="probe">Reads which Edge profiles have the extension, and which are open.</param>
    /// <param name="launcher">Opens Edge in a profile at a page.</param>
    /// <param name="allowedDomains">The domains the session allows, as last handed down.</param>
    public ExtensionWakeUp(
        IOpenProfileExtensionProbe probe,
        IBrowserProfileLauncher launcher,
        Func<IReadOnlyList<AllowedDomainDto>> allowedDomains,
        ILogger<ExtensionWakeUp>? log = null)
    {
        _probe = probe;
        _launcher = launcher;
        _allowedDomains = allowedDomains;
        _log = log ?? NullLogger<ExtensionWakeUp>.Instance;
    }

    /// <summary>
    /// True when some Edge profile has the extension installed, on or off, open or
    /// not. False on a device without it in any profile, which the guard leaves
    /// alone. Never throws.
    /// </summary>
    public bool IsInstalled()
    {
        try
        {
            return _probe.FindWithExtension(openOnly: false).Count > 0;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Looking for the extension in Edge's profiles threw; treating it as not installed");
            return false;
        }
    }

    /// <summary>The profiles with a window open that have the extension installed, on or off. Never throws.</summary>
    public IReadOnlyList<ProfileWithExtension> FindOpen()
    {
        try
        {
            return _probe.FindWithExtension(openOnly: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Looking for open Edge profiles with the extension threw");
            return Array.Empty<ProfileWithExtension>();
        }
    }

    /// <summary>
    /// Opens Edge in <paramref name="profile"/> at a page the session allows
    /// (<see cref="WakeUrl"/>), so the extension's service worker wakes and
    /// connects. Returns the page opened.
    /// </summary>
    public string Wake(string profile)
    {
        IReadOnlyList<AllowedDomainDto> domains;
        try
        {
            domains = _allowedDomains();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reading the session's allowed domains threw; waking the extension at its store listing");
            domains = Array.Empty<AllowedDomainDto>();
        }
        var url = WakeUrl(domains);
        _log.LogInformation("Waking the extension: opening Edge profile {Profile} at {Url}", profile, url);
        _launcher.OpenProfile(profile, url);
        return url;
    }

    /// <summary>
    /// The page that wakes the extension: the first domain the session allows,
    /// as an https address (<c>*.example.org</c> as <c>https://example.org/</c>),
    /// so it isn't blocked once the session is handed down. With none that is a
    /// host name, the extension's own store listing: any http(s) navigation wakes
    /// it, and should it be blocked, the extension is back.
    /// </summary>
    public static string WakeUrl(IEnumerable<AllowedDomainDto> domains)
    {
        foreach (var domain in domains)
        {
            var value = (domain.Value ?? "").Trim().ToLowerInvariant();
            if (value.StartsWith("*.", StringComparison.Ordinal))
                value = value[2..];
            if (value.Length > 0 && Uri.CheckHostName(value) == UriHostNameType.Dns)
                return $"https://{value}/";
        }
        return EdgeExtensionPolicy.StoreListingUrl;
    }
}
