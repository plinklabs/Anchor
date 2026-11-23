using FocusAgent.Core.Extension;
using FocusAgent.Core.Logging;
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
/// on every launch). The agent now does it itself on startup, idempotently, mirroring
/// how it writes the Edge force-install policy
/// (<see cref="Extension.ExtensionSelfRegistrar"/>) and the run-at-login entry
/// (<c>StartupRegistrar</c>).
///
/// On each launch it ensures two artifacts agree with the current install:
///   1. the host manifest JSON next to the exe (embeds the exe's absolute path), and
///   2. the HKCU key whose <c>(default)</c> value points at that manifest.
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
    private readonly AnchorExtensionIds _extensionIds;
    private readonly ILogger<WitnessHostRegistrar> _log;

    /// <param name="store">The registry + file writes.</param>
    /// <param name="log">Logger.</param>
    /// <param name="extensionIds">
    /// The extensions the host manifest lets connect (#497); defaults to
    /// <see cref="AnchorExtensionIds.Configured"/>.
    /// </param>
    public WitnessHostRegistrar(
        IWitnessHostStore store,
        ILogger<WitnessHostRegistrar>? log = null,
        AnchorExtensionIds? extensionIds = null)
    {
        _store = store;
        _extensionIds = extensionIds ?? AnchorExtensionIds.Configured;
        _log = log ?? NullLogger<WitnessHostRegistrar>.Instance;
    }

    /// <summary>
    /// Ensure the manifest and the HKCU key both describe the host exe at
    /// <paramref name="hostExePath"/>, returning whether any write happened. Idempotent:
    /// each artifact is only (re-)written when missing or stale, so calling on every
    /// launch never churns identical content and never leaks a duplicate.
    /// </summary>
    public bool EnsureRegistered(string hostExePath)
    {
        if (string.IsNullOrWhiteSpace(hostExePath))
            throw new ArgumentException("Host exe path must be provided.", nameof(hostExePath));

        var hostDir = Path.GetDirectoryName(hostExePath)
            ?? throw new ArgumentException($"Host exe path '{hostExePath}' has no directory.", nameof(hostExePath));

        var manifestPath = WitnessHostManifest.ManifestPathFor(hostDir);

        var wroteManifest = EnsureFile(manifestPath, WitnessHostManifest.BuildManifest(hostExePath, _extensionIds), "host manifest");
        var wroteKey = EnsureKey(manifestPath);
        return wroteManifest || wroteKey;
    }

    /// <summary>
    /// Register the host exe shipped at <paramref name="hostExePath"/>, never
    /// throwing, and log the outcome either way (#561): the agent-startup
    /// registration and the link self-check at session join. A missing exe (a
    /// dev or debug build, or an install that lost it) leaves any registration
    /// in place rather than pointing Edge at a path that won't launch. Returns
    /// whether any write happened.
    /// </summary>
    /// <param name="hostExePath">The host exe next to the running agent.</param>
    /// <param name="occasion">What asked, for the log line ("startup", "self-check").</param>
    public bool TryEnsureRegistered(string hostExePath, string occasion)
    {
        try
        {
            if (!_store.FileExists(hostExePath))
            {
                _log.LogWarning(
                    "Witness host registration ({Occasion}): {HostExe} doesn't exist; not registering it, " +
                    "and leaving any registration in place.",
                    occasion, LogPath.Redact(hostExePath));
                return false;
            }

            var wrote = EnsureRegistered(hostExePath);
            _log.LogInformation(
                "Witness host registration ({Occasion}) for {HostExe}: {Outcome}.",
                occasion, LogPath.Redact(hostExePath), wrote ? "rewrote it" : "already up to date");
            return wrote;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Witness host registration ({Occasion}) for {HostExe} failed: {Error}",
                occasion, LogPath.Redact(hostExePath), ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Remove the host's HKCU key — called from the uninstall hook so uninstalling the
    /// agent un-registers the host it installed (the manifest goes away with the
    /// install dir). Best-effort and idempotent: a missing key is fine,
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
            _log.LogInformation("Witness {Label} already up to date at {Path}; not re-writing.", label, LogPath.Redact(path));
            return false;
        }

        _store.WriteFile(path, content);
        _log.LogInformation("Wrote witness {Label} to {Path}.", label, LogPath.Redact(path));
        return true;
    }

    private bool EnsureKey(string manifestPath)
    {
        var current = _store.GetRegisteredManifestPath();
        if (current is not null &&
            string.Equals(current.Trim(), manifestPath.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            _log.LogInformation(
                "Witness host key already points at {ManifestPath}; not re-writing.", LogPath.Redact(manifestPath));
            return false;
        }

        _store.SetRegisteredManifestPath(manifestPath);
        _log.LogInformation(
            current is null
                ? "Wrote witness host key: HKCU\\{Key}\\(default) = {ManifestPath}"
                : "Updated witness host key: HKCU\\{Key}\\(default) = {ManifestPath}",
            WitnessHostManifest.RegistryKeyPath, LogPath.Redact(manifestPath));
        return true;
    }
}
