using System.Runtime.Versioning;
using FocusAgent.Core.Startup;

namespace FocusAgent.App.Startup;

/// <summary>
/// Static entry points for the per-user "start at login" Run-key write/remove
/// (#225), used outside the DI container: the Velopack install / update / uninstall
/// hooks and the dev-only <c>--register-startup</c> / <c>--unregister-startup</c>
/// CLI modes all run in <c>Program.Main</c> before the host is built. Each composes
/// the same <see cref="RegistryStartupStore"/> + <see cref="StartupRegistrar"/>, so
/// there's one code path to the registry — exercised end-to-end by the integration
/// test.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class StartupRegistration
{
    private static StartupRegistrar Build(string? keyOverride) =>
        new(new RegistryStartupStore(keyOverride));

    /// <summary>
    /// The full path of the installed agent exe to register at login. The Velopack
    /// install/update lands the agent in a versioned dir and runs the same exe for
    /// the hook, so the current process path <em>is</em> the path Windows should
    /// launch at sign-in.
    /// </summary>
    private static string CurrentExePath() => Environment.ProcessPath
        ?? throw new InvalidOperationException("Could not resolve the current process path.");

    /// <summary>
    /// Ensure the Run entry points at <paramref name="exePath"/> (or the current
    /// exe when null). Idempotent — used by the dev CLI mode and the install/update
    /// hooks. Returns whether a write happened.
    /// </summary>
    public static bool EnsureRegistered(string? keyOverride, string? exePath = null) =>
        Build(keyOverride).EnsureRegistered(exePath ?? CurrentExePath());

    /// <summary>Remove Anchor's Run entry. Used by the dev CLI mode.</summary>
    public static void RemoveRegistration(string? keyOverride) =>
        Build(keyOverride).Remove();

    /// <summary>The command currently registered under Anchor's Run value (for harness assertions).</summary>
    public static string? ReadRegisteredCommand(string? keyOverride) =>
        new RegistryStartupStore(keyOverride).GetRegisteredCommand();

    /// <summary>
    /// The Velopack install / update hook (#225): (re-)point the Run entry at the
    /// freshly-installed exe so the agent starts at login. Runs on first install and
    /// on every update — a Velopack update lands in a new versioned install dir, so
    /// the command must be re-pointed (idempotently) to the new path. Best-effort:
    /// a failure here must not block the install/update.
    /// </summary>
    public static void RegisterForInstall()
    {
        try
        {
            Build(keyOverride: null).EnsureRegistered(CurrentExePath());
        }
        catch
        {
            // Install/update must proceed regardless; auto-start is a convenience.
        }
    }

    /// <summary>
    /// The Velopack uninstall hook (#225): remove the Run entry so uninstalling the
    /// agent stops it launching at login — leaving the box as it found it.
    /// Best-effort: any failure is swallowed so it can never block the uninstall.
    /// </summary>
    public static void RemoveForUninstall()
    {
        try
        {
            Build(keyOverride: null).Remove();
        }
        catch
        {
            // Uninstall must proceed regardless.
        }
    }
}
