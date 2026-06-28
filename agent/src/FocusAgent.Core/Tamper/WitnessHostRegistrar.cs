using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Tamper;

/// <summary>
/// Registers the witness native-messaging host so Edge can launch
/// <c>anchor-witness-host.exe</c> when the extension <c>connectNative()</c>s (#288).
///
/// In dev this is <c>scripts/dev/register-witness-host.ps1</c>'s job; the release
/// installer never did the equivalent, so the extension couldn't connect — leaving
/// <c>ExtensionWitnessMonitor.IsConnected</c> false (the guided-install popup fired
/// on every launch) and the <c>backend_url</c> hand-down stranded. The agent now does
/// it itself on startup, idempotently, mirroring how it writes the Edge force-install
/// policy (<see cref="Extension.ExtensionSelfRegistrar"/>) and the run-at-login entry
/// (<c>StartupRegistrar</c>).
///
/// On each launch it ensures three artifacts agree with the current install:
///   1. the host manifest JSON next to the exe (embeds the exe's absolute path),
///   2. the <c>backend-url.json</c> next to the exe (the configured backend), and
///   3. the HKCU key whose <c>(default)</c> value points at that manifest.
/// A Velopack update lands the agent in a new versioned dir, so the recorded paths /
/// content change between versions; re-running re-points them and never churns when
/// nothing changed.
///
/// Pure orchestration — the registry + file writes (<see cref="IWitnessHostStore"/>)
/// are injected — so the present/update/remove logic is unit-testable on any OS
/// without a real registry or browser, while the real HKCU + filesystem paths are
/// exercised end-to-end by the integration test.
/// </summary>
public sealed class WitnessHostRegistrar
{
    private readonly IWitnessHostStore _store;
    private readonly ILogger<WitnessHostRegistrar> _log;

    public WitnessHostRegistrar(
        IWitnessHostStore store,
        ILogger<WitnessHostRegistrar>? log = null)
    {
        _store = store;
        _log = log ?? NullLogger<WitnessHostRegistrar>.Instance;
    }

    /// <summary>
    /// Ensure the manifest, backend-url file, optional auth-config file, and HKCU key
    /// all describe the host exe at <paramref name="hostExePath"/> targeting
    /// <paramref name="backendUrl"/>, returning whether any write happened. Idempotent:
    /// each artifact is only (re-)written when missing or stale, so calling on every
    /// launch never churns identical content and never leaks a duplicate.
    /// </summary>
    /// <param name="auth">
    /// The deployment's Entra config (#289). When supplied, an <c>auth-config.json</c>
    /// is written next to the host so the extension can mint a real student token; null
    /// in dev (no per-deployment auth), where the extension uses the dev impersonation
    /// shortcut and the host sends no <c>auth_config</c>.
    /// </param>
    public bool EnsureRegistered(string hostExePath, string backendUrl, WitnessAuthConfig? auth = null)
    {
        if (string.IsNullOrWhiteSpace(hostExePath))
            throw new ArgumentException("Host exe path must be provided.", nameof(hostExePath));

        var hostDir = Path.GetDirectoryName(hostExePath)
            ?? throw new ArgumentException($"Host exe path '{hostExePath}' has no directory.", nameof(hostExePath));

        var manifestPath = WitnessHostManifest.ManifestPathFor(hostDir);
        var backendUrlPath = WitnessHostManifest.BackendUrlPathFor(hostDir);

        var wroteManifest = EnsureFile(manifestPath, WitnessHostManifest.BuildManifest(hostExePath), "host manifest");
        var wroteBackend = EnsureFile(backendUrlPath, WitnessHostManifest.BuildBackendUrlFile(backendUrl), "backend-url file");

        var wroteAuth = false;
        if (auth is not null)
        {
            var authPath = WitnessHostManifest.AuthConfigPathFor(hostDir);
            wroteAuth = EnsureFile(authPath, WitnessHostManifest.BuildAuthConfigFile(auth), "auth-config file");
        }

        var wroteKey = EnsureKey(manifestPath);
        return wroteManifest || wroteBackend || wroteAuth || wroteKey;
    }

    /// <summary>
    /// Remove the host's HKCU key — called from the uninstall hook so uninstalling the
    /// agent un-registers the host it installed (the manifest / backend-url files go
    /// away with the install dir). Best-effort and idempotent: a missing key is fine,
    /// and any failure is logged rather than thrown so it can never block the uninstall.
    /// </summary>
    public void Remove()
    {
        try
        {
            _store.RemoveRegistration();
            _log.LogInformation("Removed witness host registration {Key}.", WitnessHostManifest.RegistryKeyPath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Removing the witness host registration failed (uninstall).");
        }
    }

    private bool EnsureFile(string path, string content, string label)
    {
        if (string.Equals(_store.ReadFile(path), content, StringComparison.Ordinal))
        {
            _log.LogInformation("Witness {Label} already up to date at {Path}; not re-writing.", label, path);
            return false;
        }

        _store.WriteFile(path, content);
        _log.LogInformation("Wrote witness {Label} to {Path}.", label, path);
        return true;
    }

    private bool EnsureKey(string manifestPath)
    {
        var current = _store.GetRegisteredManifestPath();
        if (current is not null &&
            string.Equals(current.Trim(), manifestPath.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            _log.LogInformation(
                "Witness host key already points at {ManifestPath}; not re-writing.", manifestPath);
            return false;
        }

        _store.SetRegisteredManifestPath(manifestPath);
        _log.LogInformation(
            current is null
                ? "Wrote witness host key: HKCU\\{Key}\\(default) = {ManifestPath}"
                : "Updated witness host key: HKCU\\{Key}\\(default) = {ManifestPath}",
            WitnessHostManifest.RegistryKeyPath, manifestPath);
        return true;
    }
}
