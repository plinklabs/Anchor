namespace FocusAgent.Core.Extension;

/// <summary>
/// Access to one Edge <c>ExtensionInstallForcelist</c> policy key (#211, #405):
/// the machine-wide one under <c>HKLM</c> or the per-user one under <c>HKCU</c>.
/// Abstracted off the concrete <c>Microsoft.Win32.Registry</c> access so the
/// orchestration in <see cref="ExtensionSelfRegistrar"/> — and its find/insert/
/// remove decisions — is unit-testable with an in-memory fake, while the real
/// registry path is driven end-to-end by the integration tests against a throwaway
/// HKCU subtree.
///
/// The running agent only <em>reads</em> both keys: a standard profile can write
/// neither (see <see cref="EdgeExtensionPolicy"/>). The HKLM entry is added by an
/// elevated <c>reg.exe</c> (<see cref="IElevatedPolicyWriter"/>), not through this
/// interface; <see cref="AddForcelistEntry"/> serves the dev-only CLI mode and the
/// tests' simulated elevation.
/// </summary>
public interface IExtensionPolicyStore
{
    /// <summary>
    /// The forcelist value strings currently present (the values, not their
    /// numbered names) — empty when the policy key doesn't exist yet.
    /// </summary>
    IReadOnlyList<string> GetForcelistEntries();

    /// <summary>
    /// The numbered value names currently present under the forcelist key (so the
    /// caller can pick the next free slot). Empty when the key doesn't exist.
    /// </summary>
    IReadOnlyList<string> GetForcelistValueNames();

    /// <summary>
    /// The string stored under <paramref name="valueName"/>, or null when the key
    /// or the value is absent (or isn't a string).
    /// </summary>
    string? GetForcelistValue(string valueName);

    /// <summary>
    /// Add <paramref name="entry"/> under value name <paramref name="valueName"/>,
    /// creating the forcelist key if needed.
    /// </summary>
    void AddForcelistEntry(string valueName, string entry);

    /// <summary>
    /// Remove every forcelist value whose id segment is Anchor's extension (#211
    /// uninstall). Leaves any co-installed product's entries untouched. No-op when
    /// the key or the entry is absent.
    /// </summary>
    void RemoveAnchorForcelistEntries();
}
