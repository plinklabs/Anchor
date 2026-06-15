namespace FocusAgent.Core.Extension;

/// <summary>
/// The identity + registry shape of the per-user Edge force-install policy that
/// makes Edge install Anchor's canonical extension itself (#211). Anchor is
/// unmanaged BYOD, so the agent writes this under <c>HKCU</c> (no admin, no
/// enterprise MDM) rather than the usual managed-device <c>HKLM</c> path.
///
/// Edge reads the <c>ExtensionInstallForcelist</c> policy as a key whose
/// <em>values</em> are numbered <c>1, 2, 3, …</c>, each a string of the form
/// <c>&lt;extension-id&gt;;&lt;update-url&gt;</c>. On the next browser launch Edge
/// pulls the extension from the Add-ons store and pins it (no "Add" click, not
/// user-removable). Removing our value (on uninstall) un-pins it.
///
/// The pure shape lives here — separate from the Windows <c>Microsoft.Win32</c>
/// write — so the find/insert/normalise logic is unit-testable on any OS and the
/// constants can't drift between the writer, the remover, and the tests.
/// </summary>
public static class EdgeExtensionPolicy
{
    /// <summary>
    /// The pinned stable extension ID (see <c>extension/README.md</c> "Stable
    /// extension ID"). The committed manifest <c>key</c> fixes this ID across every
    /// build and machine, which is exactly what lets a per-user policy pin it — a
    /// policy keys an extension by ID.
    /// </summary>
    public const string ExtensionId = "dnkimhodjfogjibnbbfdjdapgmmiojio";

    /// <summary>
    /// The Edge Add-ons store update manifest. Edge resolves the pinned listing
    /// (#210) through this well-known endpoint; it's the same URL Edge uses for
    /// every store-hosted force-installed extension.
    /// </summary>
    public const string UpdateUrl = "https://edge.microsoft.com/extensionwebstorebase/v1/crx";

    /// <summary>
    /// The per-user policy key path under <c>HKEY_CURRENT_USER</c>. Writing here
    /// needs no admin rights, which is the whole point on an unmanaged BYOD box.
    /// </summary>
    public const string ForcelistKeyPath = @"Software\Policies\Microsoft\Edge\ExtensionInstallForcelist";

    /// <summary>
    /// The Edge Add-ons store listing the guided-install fallback opens when the
    /// force-install policy doesn't take (e.g. Edge refused the per-user policy on
    /// a locked-down box). The student clicks <em>Get → Add</em> there.
    /// </summary>
    public const string StoreListingUrl =
        "https://microsoftedge.microsoft.com/addons/detail/" + ExtensionId;

    /// <summary>
    /// The forcelist value string for Anchor's extension:
    /// <c>&lt;id&gt;;&lt;update-url&gt;</c>.
    /// </summary>
    public static string ForcelistEntry => $"{ExtensionId};{UpdateUrl}";

    /// <summary>
    /// True when <paramref name="value"/> is a forcelist entry for our extension —
    /// i.e. its <c>id</c> segment (before the first <c>;</c>) equals
    /// <see cref="ExtensionId"/>, regardless of the update-url segment. Lets the
    /// writer recognise an existing Anchor entry (and skip re-adding it) even if a
    /// past version wrote a slightly different update URL.
    /// </summary>
    public static bool IsAnchorEntry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var semicolon = value.IndexOf(';');
        var id = semicolon < 0 ? value : value[..semicolon];
        return string.Equals(id.Trim(), ExtensionId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Given the value names already present in the forcelist key, returns the
    /// next free numbered slot. Edge numbers entries <c>1, 2, 3, …</c>; we add to
    /// the smallest unused positive integer so we never clobber a co-installed
    /// extension's entry (a different product's force-install policy, say).
    /// </summary>
    public static string NextFreeIndex(IEnumerable<string> existingValueNames)
    {
        var taken = new HashSet<int>();
        foreach (var name in existingValueNames)
        {
            if (int.TryParse(name, out var n) && n > 0)
                taken.Add(n);
        }

        var i = 1;
        while (taken.Contains(i)) i++;
        return i.ToString();
    }
}
