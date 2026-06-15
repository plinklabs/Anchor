using System.Runtime.Versioning;
using FocusAgent.Core.Extension;

namespace FocusAgent.App.Extension;

/// <summary>
/// Static entry points for the Edge force-install policy write/remove (#211),
/// used outside the DI container: the Velopack uninstall hook and the dev-only
/// <c>--register-extension</c> / <c>--unregister-extension</c> CLI modes both run
/// in <c>Program.Main</c> before the host is built. Each composes the same
/// <see cref="RegistryExtensionPolicyStore"/> + <see cref="ExtensionSelfRegistrar"/>
/// the running agent uses, so there's one code path to the registry — exercised
/// end-to-end by the integration test.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ExtensionRegistration
{
    private static ExtensionSelfRegistrar Build(string? keyOverride) =>
        new(
            new RegistryExtensionPolicyStore(keyOverride),
            // No live witness in these standalone modes; the verify/witness signal
            // only matters for the in-process startup flow.
            extensionCheckedIn: () => false,
            showGuidedInstall: _ => Task.CompletedTask);

    /// <summary>Write the forcelist policy (idempotent). Used by the dev CLI mode.</summary>
    public static void WriteForcelistPolicy(string? keyOverride) =>
        Build(keyOverride).EnsurePolicyWritten();

    /// <summary>Remove Anchor's forcelist entry. Used by the dev CLI mode.</summary>
    public static void RemoveForcelistPolicy(string? keyOverride) =>
        Build(keyOverride).RemovePolicy();

    /// <summary>The forcelist entries currently written (for harness assertions).</summary>
    public static IReadOnlyList<string> ReadForcelistEntries(string? keyOverride) =>
        new RegistryExtensionPolicyStore(keyOverride).GetForcelistEntries();

    /// <summary>
    /// The Velopack uninstall hook (#211): remove the production forcelist entry so
    /// uninstalling the agent un-pins the extension it installed. Best-effort — any
    /// failure is swallowed so it can never block the uninstall.
    /// </summary>
    public static void RemovePolicyForUninstall()
    {
        try
        {
            Build(keyOverride: null).RemovePolicy();
        }
        catch
        {
            // Uninstall must proceed regardless.
        }
    }
}
