using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Extension;

/// <summary>Where an Anchor force-install entry was found (#405).</summary>
public enum ExtensionPolicyLocation
{
    /// <summary>Neither forcelist has an Anchor entry.</summary>
    None,

    /// <summary>The machine-wide <c>HKLM</c> forcelist (written by us, Intune or GPO).</summary>
    Machine,

    /// <summary>The per-user <c>HKCU</c> forcelist (written by IT or an older build).</summary>
    User,
}

/// <summary>
/// Makes Edge install Anchor's canonical extension itself, with a guided-install
/// fallback (#211), through a one-time UAC prompt (#405).
///
/// Zero-friction, unmanaged BYOD: the installer never bundles the extension, and
/// the agent stays a per-user install so Velopack installs and updates stay silent.
/// Elevation is asked for exactly one thing, the Edge force-install policy:
/// <list type="number">
///   <item>At startup, read the forcelist under both <c>HKLM</c> and <c>HKCU</c>
///   (no elevation needed). An Anchor entry in either — ours from an earlier run,
///   or one Intune/GPO pushed — means there is nothing to do.</item>
///   <item>Otherwise explain it in the agent's own window first, and if the student
///   agrees, add the entry under HKLM at the next free slot through one elevated
///   <c>reg.exe</c> (<see cref="IElevatedPolicyWriter"/>), never touching other
///   entries. Writing the HKCU key isn't an option: a standard profile can't.</item>
///   <item>"Not now", a declined UAC prompt, a student who isn't an administrator,
///   or a failed write all fall back to the guided install (the store listing).
///   The answer is remembered so startup doesn't ask on every launch; the tray
///   menu offers the setup again (<see cref="OfferAgainAsync"/>).</item>
/// </list>
/// Once a policy is in place, the success signal is the existing agent↔extension
/// witness link: if the extension hasn't checked in after a grace period, the
/// guided install opens after all.
///
/// Pure orchestration — the registry reads (<see cref="IExtensionPolicyStore"/>),
/// the elevated write, the remembered answer, the consent window, the witness
/// signal, the delay and the guided fallback are all injected — so every branch is
/// unit-testable on any OS without a registry, a UAC prompt, a browser or a real
/// wall-clock wait.
/// </summary>
public sealed class ExtensionSelfRegistrar
{
    private readonly IExtensionPolicyStore _machine;
    private readonly IExtensionPolicyStore _user;
    private readonly IElevatedPolicyWriter _elevated;
    private readonly IExtensionPolicyStateStore _state;
    private readonly Func<CancellationToken, Task<bool?>> _askConsent;
    private readonly Func<bool> _extensionCheckedIn;
    private readonly Func<CancellationToken, Task> _showGuidedInstall;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _grace;
    private readonly ILogger<ExtensionSelfRegistrar> _log;
    private int _offering;

    /// <summary>Default time to let Edge pick up the policy + the extension check in.</summary>
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(45);

    /// <param name="machineStore">Reads the <c>HKLM</c> forcelist (no elevation).</param>
    /// <param name="userStore">Reads the <c>HKCU</c> forcelist; uninstall also clears old entries there.</param>
    /// <param name="elevatedWriter">Adds/removes the HKLM entry through a UAC prompt.</param>
    /// <param name="stateStore">Remembers a "not now" and which HKLM entry is ours.</param>
    /// <param name="askConsent">
    /// Shows the agent's own explanation before the UAC prompt; true when the
    /// student chose to go ahead, false for "not now" or closing the window, null
    /// when the window went away without an answer (the agent is shutting down).
    /// </param>
    /// <param name="extensionCheckedIn">
    /// The success signal: true once the extension has connected over the witness
    /// link, i.e. it is installed and running.
    /// </param>
    /// <param name="showGuidedInstall">Opens the guided one-click install (the store listing).</param>
    /// <param name="delayAsync">
    /// The grace-period wait. Injected so tests run instantly; defaults to
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// </param>
    /// <param name="gracePeriod">How long to wait for the check-in. Defaults to <see cref="DefaultGracePeriod"/>.</param>
    public ExtensionSelfRegistrar(
        IExtensionPolicyStore machineStore,
        IExtensionPolicyStore userStore,
        IElevatedPolicyWriter elevatedWriter,
        IExtensionPolicyStateStore stateStore,
        Func<CancellationToken, Task<bool?>> askConsent,
        Func<bool> extensionCheckedIn,
        Func<CancellationToken, Task> showGuidedInstall,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? gracePeriod = null,
        ILogger<ExtensionSelfRegistrar>? log = null)
    {
        _machine = machineStore;
        _user = userStore;
        _elevated = elevatedWriter;
        _state = stateStore;
        _askConsent = askConsent;
        _extensionCheckedIn = extensionCheckedIn;
        _showGuidedInstall = showGuidedInstall;
        _delayAsync = delayAsync ?? Task.Delay;
        _grace = gracePeriod ?? DefaultGracePeriod;
        _log = log ?? NullLogger<ExtensionSelfRegistrar>.Instance;
    }

    /// <summary>
    /// Where an Anchor entry already is, read without elevation: the HKLM
    /// forcelist first, then the HKCU one. A key that can't be read counts as
    /// having no entry.
    /// </summary>
    public ExtensionPolicyLocation FindPolicy()
    {
        if (HasAnchorEntry(_machine, "HKLM")) return ExtensionPolicyLocation.Machine;
        if (HasAnchorEntry(_user, "HKCU")) return ExtensionPolicyLocation.User;
        return ExtensionPolicyLocation.None;
    }

    /// <summary>
    /// Startup flow. With a policy already in place, verify the extension checks
    /// in (else open the guided install). Without one, ask the student — unless
    /// they said "not now" before — and add the HKLM entry through one UAC prompt;
    /// any outcome but a written entry falls back to the guided install. Returns
    /// true when the extension is confirmed present, false when the guided install
    /// was opened.
    /// </summary>
    public async Task<bool> RegisterAndVerifyAsync(CancellationToken ct = default)
    {
        var location = FindPolicy();
        if (location != ExtensionPolicyLocation.None)
        {
            _log.LogInformation(
                "Edge force-install policy for {ExtensionId} already present ({Location}); not asking.",
                EdgeExtensionPolicy.ExtensionId, location);
            return await VerifyAsync(ct).ConfigureAwait(false);
        }

        var state = _state.Load();
        if (state.PromptDeclined)
        {
            _log.LogInformation(
                "No Edge force-install policy, but the setup was declined on {DeclinedAt:u}; not asking at startup " +
                "(the tray menu offers it).",
                state.PromptDeclinedAt);
            return await VerifyAsync(ct).ConfigureAwait(false);
        }

        return await OfferAndFollowUpAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The tray menu's "set up the Edge extension" action: the same explanation
    /// and UAC prompt as at startup, asked again even after an earlier "not now".
    /// A no-op when a policy is already in place or the setup is already on screen.
    /// </summary>
    public async Task<bool> OfferAgainAsync(CancellationToken ct = default)
    {
        var location = FindPolicy();
        if (location != ExtensionPolicyLocation.None)
        {
            _log.LogInformation("Edge force-install policy already present ({Location}); nothing to set up.", location);
            return true;
        }

        return await OfferAndFollowUpAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The uninstall hook's part (#211, #405). Clears Anchor's entries from the
    /// HKCU forcelist (left by older builds or by IT, where it's writable), and
    /// starts the elevated removal of the HKLM entry — but only the one this agent
    /// wrote (per the saved state), and only while that slot still holds Anchor's
    /// entry, so an entry Intune or Group Policy manages is never touched and never
    /// costs the student a UAC prompt. Returns the removal's task (completes once
    /// the prompt is answered) so the hook can wait on it for a bounded time.
    /// Best-effort: never throws.
    /// </summary>
    public Task RemoveForUninstall()
    {
        try
        {
            _user.RemoveAnchorForcelistEntries();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Removing the HKCU Edge force-install entry failed (uninstall).");
        }

        try
        {
            var ours = _state.Load().MachineEntryValueName;
            if (ours is null || !EdgeExtensionPolicy.IsSlotName(ours))
                return Task.CompletedTask;

            if (!EdgeExtensionPolicy.IsAnchorEntry(_machine.GetForcelistValue(ours)))
            {
                _log.LogInformation(
                    "HKLM forcelist slot {Slot} no longer holds Anchor's entry; leaving HKLM alone.", ours);
                return Task.CompletedTask;
            }

            _log.LogInformation("Removing Anchor's HKLM forcelist entry {Slot} (elevated).", ours);
            return _elevated.StartRemovingMachineEntry(ours);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Removing the HKLM Edge force-install entry failed (uninstall).");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Add Anchor's entry to <paramref name="store"/> at the next free slot unless
    /// one is already there; returns the slot written, or null when nothing was.
    /// The dev-only <c>--register-extension</c> mode uses it against a throwaway key.
    /// </summary>
    public static string? AddAnchorEntryIfMissing(IExtensionPolicyStore store)
    {
        if (store.GetForcelistEntries().Any(EdgeExtensionPolicy.IsAnchorEntry))
            return null;

        var slot = EdgeExtensionPolicy.NextFreeIndex(store.GetForcelistValueNames());
        store.AddForcelistEntry(slot, EdgeExtensionPolicy.ForcelistEntry);
        return slot;
    }

    private enum OfferOutcome { Written, NotWritten, AlreadyOpen, Unanswered }

    private async Task<bool> OfferAndFollowUpAsync(CancellationToken ct)
    {
        var outcome = await OfferMachinePolicyAsync(ct).ConfigureAwait(false);
        return outcome switch
        {
            OfferOutcome.Written => await VerifyAsync(ct).ConfigureAwait(false),
            // The setup is already on screen (startup and the tray raced); the
            // first one follows up.
            OfferOutcome.AlreadyOpen => false,
            // The agent is shutting down: nothing to follow up.
            OfferOutcome.Unanswered => false,
            _ => await GuidedUnlessCheckedInAsync(ct).ConfigureAwait(false),
        };
    }

    private async Task<OfferOutcome> OfferMachinePolicyAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _offering, 1) == 1)
        {
            _log.LogInformation("The Edge extension setup is already on screen.");
            return OfferOutcome.AlreadyOpen;
        }

        try
        {
            bool? agreed;
            try
            {
                agreed = await _askConsent(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The explanation couldn't be shown. Not the student's answer, so
                // nothing is remembered; the guided install still gets them there.
                _log.LogWarning(ex, "Showing the Edge extension setup failed; falling back to guided install.");
                return OfferOutcome.NotWritten;
            }

            if (agreed is null)
            {
                // Not the student's answer (the agent closed the window on its way
                // out), so nothing is remembered: ask again next start.
                _log.LogInformation("Edge extension setup closed without an answer.");
                return OfferOutcome.Unanswered;
            }

            if (agreed == false)
            {
                _log.LogInformation("Edge extension setup: the student chose not now.");
                RememberDeclined();
                return OfferOutcome.NotWritten;
            }

            var slot = EdgeExtensionPolicy.NextFreeIndex(_machine.GetForcelistValueNames());
            var result = await _elevated
                .AddMachineEntryAsync(slot, EdgeExtensionPolicy.ForcelistEntry, ct)
                .ConfigureAwait(false);

            // Trust the registry, not just the exit code: the entry must now be
            // readable where Edge will look for it.
            if (result == ElevatedPolicyWriteResult.Succeeded &&
                !EdgeExtensionPolicy.IsAnchorEntry(_machine.GetForcelistValue(slot)))
            {
                _log.LogWarning(
                    "The elevated write reported success, but HKLM forcelist slot {Slot} doesn't hold Anchor's entry.",
                    slot);
                result = ElevatedPolicyWriteResult.Failed;
            }

            switch (result)
            {
                case ElevatedPolicyWriteResult.Succeeded:
                    _log.LogInformation(
                        "Wrote Edge force-install policy: HKLM\\{Key}\\{Slot} = {Entry}",
                        EdgeExtensionPolicy.ForcelistKeyPath, slot, EdgeExtensionPolicy.ForcelistEntry);
                    Save(_state.Load() with
                    {
                        PromptDeclined = false,
                        PromptDeclinedAt = null,
                        MachineEntryValueName = slot,
                    });
                    return OfferOutcome.Written;

                case ElevatedPolicyWriteResult.Declined:
                    _log.LogInformation("Edge extension setup: the UAC prompt was declined; falling back to guided install.");
                    RememberDeclined();
                    return OfferOutcome.NotWritten;

                default:
                    // Remembered like a decline, so a write that keeps failing doesn't
                    // ask on every start; the tray menu can still retry it.
                    _log.LogWarning("Edge extension setup: the elevated write failed; falling back to guided install.");
                    RememberDeclined();
                    return OfferOutcome.NotWritten;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Edge extension setup failed; falling back to guided install.");
            return OfferOutcome.NotWritten;
        }
        finally
        {
            Volatile.Write(ref _offering, 0);
        }
    }

    /// <summary>
    /// With a policy in place: if the extension already checked in, done; else wait
    /// the grace period for Edge to install it, and open the guided install if it
    /// still hasn't checked in.
    /// </summary>
    private async Task<bool> VerifyAsync(CancellationToken ct)
    {
        if (_extensionCheckedIn())
        {
            _log.LogInformation("Extension already checked in; no install needed.");
            return true;
        }

        _log.LogInformation(
            "Waiting {Seconds:N0}s for the extension to check in.",
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
            _log.LogInformation("Extension checked in within the grace period.");
            return true;
        }

        _log.LogWarning(
            "Extension did not check in within {Seconds:N0}s — opening guided install.",
            _grace.TotalSeconds);
        await _showGuidedInstall(ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Without a policy (the student just said no, or the write failed): the
    /// student is at the screen now, so open the guided install straight away
    /// unless the extension is already running (installed from the store before).
    /// </summary>
    private async Task<bool> GuidedUnlessCheckedInAsync(CancellationToken ct)
    {
        if (_extensionCheckedIn())
        {
            _log.LogInformation("Extension already checked in; no guided install needed.");
            return true;
        }

        await _showGuidedInstall(ct).ConfigureAwait(false);
        return false;
    }

    private bool HasAnchorEntry(IExtensionPolicyStore store, string hive)
    {
        try
        {
            return store.GetForcelistEntries().Any(EdgeExtensionPolicy.IsAnchorEntry);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reading the {Hive} Edge forcelist failed; treating it as empty.", hive);
            return false;
        }
    }

    private void RememberDeclined() =>
        Save(_state.Load() with { PromptDeclined = true, PromptDeclinedAt = DateTimeOffset.UtcNow });

    private void Save(ExtensionPolicyState state)
    {
        try
        {
            _state.Save(state);
        }
        catch (Exception ex)
        {
            // Forgetting the answer only means asking once more next start.
            _log.LogWarning(ex, "Saving the Edge extension setup state failed.");
        }
    }
}
