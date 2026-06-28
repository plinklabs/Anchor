using System.Runtime.Versioning;
using FocusAgent.Core.Tamper;

namespace FocusAgent.App.Tamper;

/// <summary>
/// Static entry points for the witness host registration write/remove (#288), used
/// outside the DI container: the agent startup path, the Velopack uninstall hook, and
/// the dev-only <c>--register-witness-host</c> / <c>--unregister-witness-host</c> CLI
/// modes all run before (or independently of) the host being built. Each composes the
/// same <see cref="RegistryWitnessHostStore"/> + <see cref="WitnessHostRegistrar"/>,
/// so there's one code path to the registry + files — exercised end-to-end by the
/// integration test.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WitnessHostRegistration
{
    private static WitnessHostRegistrar Build(string? keyOverride) =>
        new(new RegistryWitnessHostStore(keyOverride));

    /// <summary>
    /// The host exe the release publish lands next to the agent (pack-release.ps1
    /// step 3): <c>anchor-witness-host.exe</c> in the agent's own base directory.
    /// </summary>
    public static string DefaultHostExePath() =>
        Path.Combine(AppContext.BaseDirectory, WitnessHostManifest.HostExeName);

    /// <summary>
    /// Register the host (idempotent). Used by the dev CLI mode, which supplies an
    /// explicit throwaway key + host exe path so the test never touches the live Edge
    /// key. Returns whether a write happened.
    /// </summary>
    public static bool EnsureRegistered(string? keyOverride, string hostExePath, string backendUrl) =>
        Build(keyOverride).EnsureRegistered(hostExePath, backendUrl);

    /// <summary>Remove the host registration. Used by the dev CLI mode.</summary>
    public static void RemoveRegistration(string? keyOverride) =>
        Build(keyOverride).Remove();

    /// <summary>The manifest path currently registered under the host key (for harness assertions).</summary>
    public static string? ReadRegisteredManifestPath(string? keyOverride) =>
        new RegistryWitnessHostStore(keyOverride).GetRegisteredManifestPath();

    /// <summary>
    /// The agent-startup registrar (#288): register the witness host so Edge can launch
    /// it on the next <c>connectNative()</c>. Only registers when the host exe is
    /// actually shipped next to the agent — a release publish puts it there, but a debug
    /// build doesn't, and a dev box registers via <c>register-witness-host.ps1</c>, so
    /// we must not clobber that dev registration with a path to a non-existent exe.
    /// Best-effort: the witness link is a tamper signal, never load-bearing for the
    /// agent itself, so a failure here must never block startup.
    /// </summary>
    public static void RegisterForStartup(string backendUrl)
    {
        try
        {
            var hostExePath = DefaultHostExePath();
            if (!File.Exists(hostExePath))
            {
                // Host not shipped beside the agent (a dev/debug build) — leave any
                // dev-script registration intact rather than pointing Edge at a path
                // that won't launch.
                return;
            }

            Build(keyOverride: null).EnsureRegistered(hostExePath, backendUrl);
        }
        catch
        {
            // Startup must proceed regardless; the witness link is best-effort.
        }
    }

    /// <summary>
    /// The Velopack uninstall hook (#288): remove the host's HKCU key so uninstalling
    /// the agent un-registers the native-messaging host it wrote — leaving the box as
    /// it found it. Best-effort — any failure is swallowed so it can never block the
    /// uninstall.
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
