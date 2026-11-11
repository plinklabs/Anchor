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
/// How far the startup check for the extension got (#488), for the log and the
/// e2e's status endpoint.
/// </summary>
public enum ExtensionVerification
{
    /// <summary>Not checked yet (the setup window may be open).</summary>
    NotStarted,

    /// <summary>
    /// Not installed in any Edge profile and Edge isn't running: the wait for the
    /// extension starts once Edge does, since only a running Edge can install it.
    /// </summary>
    WaitingForBrowser,

    /// <summary>Edge is running; the grace period for the extension to check in is counting.</summary>
    WaitingForCheckIn,

    /// <summary>The extension checked in over the witness link: installed and running.</summary>
    CheckedIn,

    /// <summary>
    /// The extension is installed in an Edge profile on disk; it checks in once
    /// Edge has loaded it. Nothing to install.
    /// </summary>
    Installed,

    /// <summary>Neither: the guided install was opened.</summary>
    GuidedInstall,
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
/// Once a policy is in place, the extension counts as there when it is installed
/// in an Edge profile on disk (<see cref="EdgeProfileExtensionProbe"/>, #488) or
/// has checked in over the agent↔extension witness link. The check-in can't
/// happen before Edge runs, and right after sign-in the agent is up long before
/// Edge is, so the grace period for the check-in only starts once Edge is
/// running. If, after it, the extension is neither installed nor checked in, the
/// guided install opens after all.
///
/// Pure orchestration — the registry reads (<see cref="IExtensionPolicyStore"/>),
/// the elevated write, the remembered answer, the consent window, the witness
/// signal, the profile probe, the "is Edge running" check, the delay and the
/// guided fallback are all injected — so every branch is
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
    private readonly Func<string?> _installedProfile;
    private readonly Func<bool> _browserRunning;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _grace;
    private readonly TimeSpan _browserPoll;
    private readonly ILogger<ExtensionSelfRegistrar> _log;
    private readonly object _verifyGate = new();
    private Task<bool>? _verifying;
    private int _offering;
    private int _verification;

    /// <summary>
    /// Default time, once Edge is running, to let it pick up the policy and the
    /// extension check in.
    /// </summary>
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(45);

    /// <summary>How often to look whether Edge has started while waiting for it (#488).</summary>
    public static readonly TimeSpan DefaultBrowserPollInterval = TimeSpan.FromSeconds(5);

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
    /// The "running" signal: true once the extension has connected over the witness
    /// link, i.e. it is installed and running, which needs a running Edge.
    /// </param>
    /// <param name="showGuidedInstall">Opens the guided one-click install (the store listing).</param>
    /// <param name="installedProfile">
    /// The "installed" signal (#488): the Edge profile the extension is installed
    /// in on disk, or null when none has it
    /// (<see cref="EdgeProfileExtensionProbe.FindInstalledProfile"/>). Defaults to
    /// never installed.
    /// </param>
    /// <param name="browserRunning">
    /// True while Edge is running in the student's session (#488): the grace
    /// period only starts once it is. Defaults to always running.
    /// </param>
    /// <param name="delayAsync">
    /// The grace-period wait and the wait between looks for Edge. Injected so tests
    /// run instantly; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// </param>
    /// <param name="gracePeriod">
    /// How long to wait for the check-in once Edge runs. Defaults to <see cref="DefaultGracePeriod"/>.
    /// </param>
    /// <param name="browserPollInterval">
    /// How often to look for Edge while it isn't running. Defaults to <see cref="DefaultBrowserPollInterval"/>.
    /// </param>
    public ExtensionSelfRegistrar(
        IExtensionPolicyStore machineStore,
        IExtensionPolicyStore userStore,
        IElevatedPolicyWriter elevatedWriter,
        IExtensionPolicyStateStore stateStore,
        Func<CancellationToken, Task<bool?>> askConsent,
        Func<bool> extensionCheckedIn,
        Func<CancellationToken, Task> showGuidedInstall,
        Func<string?>? installedProfile = null,
        Func<bool>? browserRunning = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? gracePeriod = null,
        TimeSpan? browserPollInterval = null,
        ILogger<ExtensionSelfRegistrar>? log = null)
    {
        _machine = machineStore;
        _user = userStore;
        _elevated = elevatedWriter;
        _state = stateStore;
        _askConsent = askConsent;
        _extensionCheckedIn = extensionCheckedIn;
        _showGuidedInstall = showGuidedInstall;
        _installedProfile = installedProfile ?? (() => null);
        _browserRunning = browserRunning ?? (() => true);
        _delayAsync = delayAsync ?? Task.Delay;
        _grace = gracePeriod ?? DefaultGracePeriod;
        _browserPoll = browserPollInterval ?? DefaultBrowserPollInterval;
        _log = log ?? NullLogger<ExtensionSelfRegistrar>.Instance;
    }

    /// <summary>How far the check for the extension got (#488).</summary>
    public ExtensionVerification Verification => (ExtensionVerification)Volatile.Read(ref _verification);

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
    /// Startup flow. With a policy already in place, verify the extension is
    /// installed, or checks in once Edge runs (else open the guided install).
    /// Without one, ask the student — unless they said "not now" before — and add
    /// the HKLM entry through one UAC prompt; any outcome but a written entry falls
    /// back to the guided install. Returns true when the extension is confirmed
    /// present, false when the guided install was opened.
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
    /// With a policy in place (or a remembered "not now"): done when the extension
    /// is installed in an Edge profile or has checked in. Else wait for Edge to run
    /// — right after sign-in it usually doesn't yet, and only a running Edge can
    /// install the extension or let it check in (#488) — then the grace period,
    /// and open the guided install if the extension is still neither. One wait at
    /// a time: a second call (the tray's setup while startup still waits for Edge)
    /// joins the one under way, so the guided install can't open twice.
    /// </summary>
    private Task<bool> VerifyAsync(CancellationToken ct)
    {
        lock (_verifyGate)
        {
            if (_verifying is { IsCompleted: false })
            {
                _log.LogInformation("Already waiting for the extension; joining that wait.");
                return _verifying;
            }
            return _verifying = VerifyOnceAsync(ct);
        }
    }

    private async Task<bool> VerifyOnceAsync(CancellationToken ct)
    {
        if (IsPresent()) return true;

        if (!IsBrowserRunning())
        {
            SetVerification(ExtensionVerification.WaitingForBrowser);
            _log.LogInformation(
                "The extension isn't installed in an Edge profile and Edge isn't running; " +
                "the {Seconds:N0}s wait for it starts once Edge runs.",
                _grace.TotalSeconds);
            do
            {
                try
                {
                    await _delayAsync(_browserPoll, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return IsPresent();
                }

                // Edge may have come and gone between two looks.
                if (IsPresent()) return true;
            }
            while (!IsBrowserRunning());
            _log.LogInformation("Edge is running.");
        }

        SetVerification(ExtensionVerification.WaitingForCheckIn);
        _log.LogInformation(
            "Waiting {Seconds:N0}s for the extension to check in.",
            _grace.TotalSeconds);
        try
        {
            await _delayAsync(_grace, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return IsPresent();
        }

        if (IsPresent()) return true;

        _log.LogWarning(
            "Extension did not check in within {Seconds:N0}s of Edge running and isn't installed in any " +
            "Edge profile — opening guided install.",
            _grace.TotalSeconds);
        SetVerification(ExtensionVerification.GuidedInstall);
        await _showGuidedInstall(ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Without a policy (the student just said no, or the write failed): the
    /// student is at the screen now, so open the guided install straight away
    /// unless the extension is already there (installed from the store before).
    /// </summary>
    private async Task<bool> GuidedUnlessCheckedInAsync(CancellationToken ct)
    {
        if (IsPresent()) return true;

        SetVerification(ExtensionVerification.GuidedInstall);
        await _showGuidedInstall(ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Whether the extension is there now: checked in over the witness link, or
    /// installed in an Edge profile on disk (#488), which needs no running Edge.
    /// </summary>
    private bool IsPresent()
    {
        if (_extensionCheckedIn())
        {
            SetVerification(ExtensionVerification.CheckedIn);
            _log.LogInformation("Extension checked in; no install needed.");
            return true;
        }

        string? profile;
        try
        {
            profile = _installedProfile();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Looking for the extension in Edge's profiles failed; treating it as not installed.");
            profile = null;
        }

        if (profile is null) return false;

        SetVerification(ExtensionVerification.Installed);
        _log.LogInformation(
            "Extension is installed in Edge ({Profile}); it checks in once Edge has loaded it. No install needed.",
            profile);
        return true;
    }

    private bool IsBrowserRunning()
    {
        try
        {
            return _browserRunning();
        }
        catch (Exception ex)
        {
            // Can't tell: count Edge as running, so the grace period isn't put off forever.
            _log.LogWarning(ex, "Checking whether Edge is running failed; treating it as running.");
            return true;
        }
    }

    private void SetVerification(ExtensionVerification verification) =>
        Volatile.Write(ref _verification, (int)verification);

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
