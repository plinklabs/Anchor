using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Extension;

/// <summary>
/// Makes Edge install Anchor's canonical extension itself on first run, with a
/// guided-install fallback (#211).
///
/// Zero-friction, unmanaged BYOD: the installer never bundles the extension. On
/// startup the agent writes the per-user <c>ExtensionInstallForcelist</c> policy
/// (<see cref="EdgeExtensionPolicy"/>) so Edge force-installs + pins the listing
/// with no "Add" click. The one real-world uncertainty is whether a per-user
/// (HKCU) force-install actually takes on a clean BYOD box, so the success signal
/// is the existing mutual agent↔extension witness link: after writing the policy
/// and waiting a grace period, if the extension still hasn't checked in we fall
/// back to a guided one-click install (opening the store listing).
///
/// Pure orchestration — the registry write (<see cref="IExtensionPolicyStore"/>),
/// the witness signal (<c>extensionCheckedIn</c>), the delay
/// (<c>delayAsync</c>), and the guided fallback (<c>showGuidedInstall</c>) are all
/// injected — so the branch logic is unit-testable on any OS without a real
/// registry, a real browser, or a real wall-clock wait.
/// </summary>
public sealed class ExtensionSelfRegistrar
{
    private readonly IExtensionPolicyStore _store;
    private readonly Func<bool> _extensionCheckedIn;
    private readonly Func<CancellationToken, Task> _showGuidedInstall;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _grace;
    private readonly ILogger<ExtensionSelfRegistrar> _log;

    /// <summary>Default time to let Edge pick up the policy + the extension check in.</summary>
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(45);

    /// <param name="store">Reads/writes the HKCU forcelist policy.</param>
    /// <param name="extensionCheckedIn">
    /// The success signal: true once the extension has connected over the witness
    /// link, i.e. it is installed and running. Wire to the witness monitor's
    /// connected state.
    /// </param>
    /// <param name="showGuidedInstall">
    /// Opens the guided one-click install (the store listing) when the policy
    /// didn't take within the grace period.
    /// </param>
    /// <param name="delayAsync">
    /// The grace-period wait. Injected so tests run instantly; defaults to
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// </param>
    /// <param name="gracePeriod">How long to wait for the check-in. Defaults to <see cref="DefaultGracePeriod"/>.</param>
    public ExtensionSelfRegistrar(
        IExtensionPolicyStore store,
        Func<bool> extensionCheckedIn,
        Func<CancellationToken, Task> showGuidedInstall,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? gracePeriod = null,
        ILogger<ExtensionSelfRegistrar>? log = null)
    {
        _store = store;
        _extensionCheckedIn = extensionCheckedIn;
        _showGuidedInstall = showGuidedInstall;
        _delayAsync = delayAsync ?? Task.Delay;
        _grace = gracePeriod ?? DefaultGracePeriod;
        _log = log ?? NullLogger<ExtensionSelfRegistrar>.Instance;
    }

    /// <summary>
    /// Ensure the forcelist policy is present, returning whether a write happened.
    /// Idempotent: if Anchor's entry is already there (a prior run wrote it) it's a
    /// no-op, so re-running on every launch never accumulates duplicate entries.
    /// Separated from the wait/fallback so the integration test can assert the
    /// registry write in isolation, and so a synchronous startup can fire-and-
    /// forget <see cref="RegisterAndVerifyAsync"/>.
    /// </summary>
    public bool EnsurePolicyWritten()
    {
        if (_store.GetForcelistEntries().Any(EdgeExtensionPolicy.IsAnchorEntry))
        {
            _log.LogInformation("Edge force-install policy already present for {ExtensionId}; not re-writing.",
                EdgeExtensionPolicy.ExtensionId);
            return false;
        }

        var index = EdgeExtensionPolicy.NextFreeIndex(_store.GetForcelistValueNames());
        _store.AddForcelistEntry(index, EdgeExtensionPolicy.ForcelistEntry);
        _log.LogInformation(
            "Wrote Edge force-install policy: HKCU\\{Key}\\{Index} = {Entry}",
            EdgeExtensionPolicy.ForcelistKeyPath, index, EdgeExtensionPolicy.ForcelistEntry);
        return true;
    }

    /// <summary>
    /// First-run flow: write the policy (if absent), wait the grace period, and —
    /// if the extension still hasn't checked in over the witness link — open the
    /// guided install. Returns true when the extension is confirmed present (the
    /// policy took, or it was already installed), false when we fell back to the
    /// guided path.
    ///
    /// If the extension is <em>already</em> checked in when we start (it was
    /// installed on a previous run), we skip the wait entirely.
    /// </summary>
    public async Task<bool> RegisterAndVerifyAsync(CancellationToken ct = default)
    {
        try
        {
            EnsurePolicyWritten();
        }
        catch (Exception ex)
        {
            // A failed policy write (locked-down HKCU, Edge group-policy lockout)
            // is exactly the case the guided fallback exists for — log and fall
            // through to it rather than crashing startup.
            _log.LogWarning(ex, "Writing the Edge force-install policy failed; falling back to guided install.");
            await _showGuidedInstall(ct).ConfigureAwait(false);
            return false;
        }

        if (_extensionCheckedIn())
        {
            _log.LogInformation("Extension already checked in; no install needed.");
            return true;
        }

        _log.LogInformation(
            "Waiting {Seconds:N0}s for Edge to force-install the extension and it to check in.",
            _grace.TotalSeconds);
        try
        {
            await _delayAsync(_grace, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return _extensionCheckedIn();
        }

        if (_extensionCheckedIn())
        {
            _log.LogInformation("Extension checked in within the grace period; force-install took.");
            return true;
        }

        _log.LogWarning(
            "Extension did not check in within {Seconds:N0}s — opening guided install.",
            _grace.TotalSeconds);
        await _showGuidedInstall(ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Remove Anchor's forcelist entry — called from the uninstall hook so the
    /// agent un-pins the extension it installed (the issue's "remove it on
    /// uninstall"). Best-effort and idempotent.
    /// </summary>
    public void RemovePolicy()
    {
        try
        {
            _store.RemoveAnchorForcelistEntries();
            _log.LogInformation("Removed Edge force-install policy for {ExtensionId}.",
                EdgeExtensionPolicy.ExtensionId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Removing the Edge force-install policy failed (uninstall).");
        }
    }
}
