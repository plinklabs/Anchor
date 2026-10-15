using System.Runtime.Versioning;
using FocusAgent.Core.Extension;

namespace FocusAgent.App.Extension;

/// <summary>
/// Static entry points for the Edge force-install policy outside the DI container
/// (#211, #405): the Velopack uninstall hook and the dev-only
/// <c>--register-extension</c> / <c>--unregister-extension</c> CLI modes, which all
/// run in <c>Program.Main</c> before the host is built.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ExtensionRegistration
{
    /// <summary>
    /// Add Anchor's entry to the HKCU-relative key <paramref name="keyOverride"/>
    /// (or the live HKCU forcelist) at the next free slot, if absent. Used by the
    /// dev CLI mode, which the integration test points at a throwaway key; the
    /// running agent never writes HKCU (a standard profile can't).
    /// </summary>
    public static void WriteForcelistPolicy(string? keyOverride) =>
        ExtensionSelfRegistrar.AddAnchorEntryIfMissing(RegistryExtensionPolicyStore.ForUser(keyOverride));

    /// <summary>Remove Anchor's entries from the HKCU-relative key. Used by the dev CLI mode.</summary>
    public static void RemoveForcelistPolicy(string? keyOverride) =>
        RegistryExtensionPolicyStore.ForUser(keyOverride).RemoveAnchorForcelistEntries();

    /// <summary>The forcelist entries currently written (for harness assertions).</summary>
    public static IReadOnlyList<string> ReadForcelistEntries(string? keyOverride) =>
        RegistryExtensionPolicyStore.ForUser(keyOverride).GetForcelistEntries();

    /// <summary>
    /// The Velopack uninstall hook (#211, #405): clear Anchor's HKCU entries (older
    /// builds, IT) and fire-and-forget the elevated removal of the HKLM entry this
    /// agent wrote — one UAC prompt, running the system <c>reg.exe</c>, so it doesn't
    /// depend on the app folder Velopack is about to delete. Returns a task that
    /// completes once that prompt is answered (or at once when there's nothing to
    /// remove); see <see cref="WaitForUninstallPrompt"/>. Never throws, so it can
    /// never block the uninstall.
    /// </summary>
    public static Task RemovePolicyForUninstall()
    {
        try
        {
            var registrar = new ExtensionSelfRegistrar(
                RegistryExtensionPolicyStore.ForMachine(),
                RegistryExtensionPolicyStore.ForUser(),
                new ShellElevatedPolicyWriter(),
                new FileExtensionPolicyStateStore(),
                // No consent window, witness or guided install on the uninstall path.
                askConsent: _ => Task.FromResult<bool?>(false),
                extensionCheckedIn: () => false,
                showGuidedInstall: _ => Task.CompletedTask);
            return registrar.RemoveForUninstall();
        }
        catch
        {
            // Uninstall must proceed regardless.
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Keep the uninstall hook alive up to <paramref name="budget"/> for the student
    /// to answer the UAC prompt of <paramref name="removal"/>. Velopack calls
    /// <c>Environment.Exit</c> once the hook returns (and kills it after 30 s), and a
    /// prompt whose requesting process has gone can't launch its elevated process.
    /// Past the budget the entry simply stays; uninstalling never waits on it.
    /// </summary>
    public static void WaitForUninstallPrompt(Task removal, TimeSpan budget)
    {
        try
        {
            removal.Wait(budget);
        }
        catch
        {
            // Best-effort only.
        }
    }
}
