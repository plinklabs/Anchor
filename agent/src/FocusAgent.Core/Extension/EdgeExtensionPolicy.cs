namespace FocusAgent.Core.Extension;

/// <summary>
/// The identity + registry shape of the Edge force-install policy that makes Edge
/// install Anchor's canonical extension itself (#211, #405).
///
/// Edge reads the <c>ExtensionInstallForcelist</c> policy as a key whose
/// <em>values</em> are numbered <c>1, 2, 3, …</c>, each a string of the form
/// <c>&lt;extension-id&gt;;&lt;update-url&gt;</c>. On the next browser launch Edge
/// pulls the extension from the Add-ons store and pins it (no "Add" click, not
/// user-removable). Removing our value (on uninstall) un-pins it.
///
/// Edge honours the key under both <c>HKLM</c> and <c>HKCU</c>, but the agent can
/// only ever <em>write</em> the HKLM one, and only with one UAC prompt (#405): on a
/// default Windows profile <c>HKCU\Software\Policies</c> gives the user
/// <c>ReadKey</c> only (just SYSTEM and an <em>elevated</em> Administrators token
/// may write it), and the agent runs unelevated from its per-user Run entry. So the
/// agent reads both hives without elevation, and when neither has an Anchor entry
/// it asks the student once and has Windows run the system <c>reg.exe</c> elevated
/// to add the entry under HKLM (<see cref="RegAddArguments"/>). HKLM, not the
/// elevated process's HKCU: under over-the-shoulder elevation (an admin types their
/// password) the elevated process runs as that admin, so its HKCU is the admin's
/// hive, not the student's. On unmanaged devices Edge honours forcelist entries
/// only for Add-ons store extensions, which ours is (<see cref="UpdateUrl"/>).
///
/// The pure shape lives here — separate from the Windows <c>Microsoft.Win32</c>
/// read and the elevated write — so the find/insert/normalise logic is
/// unit-testable on any OS and the constants can't drift between the writer, the
/// remover, and the tests.
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
    /// The policy key path, relative to its hive. Edge uses the same path under
    /// <c>HKEY_LOCAL_MACHINE</c> (where the agent adds its entry, elevated) and
    /// <c>HKEY_CURRENT_USER</c> (which the agent only reads: a standard profile
    /// can't write <c>HKCU\Software\Policies</c>, see the type remarks).
    /// </summary>
    public const string ForcelistKeyPath = @"Software\Policies\Microsoft\Edge\ExtensionInstallForcelist";

    /// <summary>The machine-wide forcelist key as <c>reg.exe</c> names it.</summary>
    public const string MachineForcelistRegPath = @"HKLM\" + ForcelistKeyPath;

    /// <summary>
    /// The Edge Add-ons store listing the guided-install fallback opens when the
    /// force-install policy isn't there or didn't take (the student said "not now"
    /// or declined the UAC prompt, or isn't an administrator). The student clicks
    /// <em>Get → Add</em> there.
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

    /// <summary>
    /// The <c>reg.exe</c> arguments that add Anchor's entry under
    /// <see cref="MachineForcelistRegPath"/> at <paramref name="valueName"/> (#405).
    /// The agent runs this elevated, through one UAC prompt, at the slot
    /// <see cref="NextFreeIndex"/> picked from an unelevated read of the key, so no
    /// other product's entry is touched. <c>/f</c> skips reg.exe's "overwrite?"
    /// question, which would hang a hidden window; <c>/reg:64</c> pins the native
    /// registry view Edge reads. <paramref name="regKeyPath"/> exists for the
    /// integration test, which runs the same command line unelevated against a
    /// throwaway HKCU key.
    /// </summary>
    public static string RegAddArguments(string valueName, string entry, string regKeyPath = MachineForcelistRegPath)
    {
        EnsureSlotName(valueName);
        EnsureNoQuote(entry, nameof(entry));
        EnsureNoQuote(regKeyPath, nameof(regKeyPath));
        return $"add \"{regKeyPath}\" /v {valueName} /t REG_SZ /d \"{entry}\" /f /reg:64";
    }

    /// <summary>
    /// The <c>reg.exe</c> arguments that delete the single value
    /// <paramref name="valueName"/> under <see cref="MachineForcelistRegPath"/> —
    /// the uninstall half of <see cref="RegAddArguments"/>. Deletes that one value,
    /// never the key or its other entries.
    /// </summary>
    public static string RegDeleteArguments(string valueName, string regKeyPath = MachineForcelistRegPath)
    {
        EnsureSlotName(valueName);
        EnsureNoQuote(regKeyPath, nameof(regKeyPath));
        return $"delete \"{regKeyPath}\" /v {valueName} /f /reg:64";
    }

    /// <summary>
    /// True when <paramref name="valueName"/> is a forcelist slot name: a positive
    /// integer, the only shape Edge reads (and the only one these command lines
    /// accept, so a value name can never smuggle extra reg.exe arguments in).
    /// </summary>
    public static bool IsSlotName(string? valueName) =>
        !string.IsNullOrEmpty(valueName)
        && valueName.All(char.IsAsciiDigit)
        && int.TryParse(valueName, out var n)
        && n > 0;

    private static void EnsureNoQuote(string value, string name)
    {
        if (value.Contains('"'))
            throw new ArgumentException("A reg.exe argument here can't contain a double quote.", name);
    }

    private static void EnsureSlotName(string valueName)
    {
        if (!IsSlotName(valueName))
            throw new ArgumentException($"'{valueName}' is not a forcelist slot (a positive number).", nameof(valueName));
    }
}
