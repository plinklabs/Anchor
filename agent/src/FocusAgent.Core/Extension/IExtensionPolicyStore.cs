namespace FocusAgent.Core.Extension;

/// <summary>
/// The agent's read/write access to the Edge <c>ExtensionInstallForcelist</c>
/// policy under <c>HKCU</c> (#211). Abstracted off the concrete
/// <c>Microsoft.Win32.Registry</c> write so the orchestration in
/// <see cref="ExtensionSelfRegistrar"/> — and its find/insert/remove decisions —
/// is unit-testable with an in-memory fake, while the real registry path is
/// driven end-to-end by the integration test against a throwaway HKCU subtree.
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
