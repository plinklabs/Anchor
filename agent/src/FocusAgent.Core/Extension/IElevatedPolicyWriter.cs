namespace FocusAgent.Core.Extension;

/// <summary>How the one elevated policy write ended (#405).</summary>
public enum ElevatedPolicyWriteResult
{
    /// <summary>The elevated write ran and exited cleanly.</summary>
    Succeeded,

    /// <summary>
    /// The student said no in the UAC prompt, or couldn't give an administrator's
    /// password and cancelled it.
    /// </summary>
    Declined,

    /// <summary>Anything else: the write couldn't start, failed, or timed out.</summary>
    Failed,
}

/// <summary>
/// The one part of the Edge policy setup that needs administrator rights (#405):
/// adding Anchor's forcelist entry under <c>HKLM</c> at install time, and removing
/// it again on uninstall. The real implementation has Windows run the system
/// <c>reg.exe</c> with ShellExecute's <c>runas</c> verb, which shows the UAC prompt.
///
/// A seam rather than a direct call because a real UAC prompt must never appear in
/// a test or dev run: it takes over the secure desktop and waits for an answer
/// nobody is there to give. Tests inject fakes; dev and test runs of the app get a
/// writer that never elevates (see the agent's composition root).
/// </summary>
public interface IElevatedPolicyWriter
{
    /// <summary>
    /// Add <paramref name="entry"/> as value <paramref name="valueName"/> under the
    /// machine-wide forcelist key, elevated. Completes once the elevated write has
    /// finished (or was declined / failed); never throws for a decline or failure.
    /// </summary>
    Task<ElevatedPolicyWriteResult> AddMachineEntryAsync(
        string valueName, string entry, CancellationToken ct = default);

    /// <summary>
    /// Fire-and-forget removal of value <paramref name="valueName"/> under the
    /// machine-wide forcelist key, for the uninstall hook. The returned task
    /// completes once the student has answered the UAC prompt (the elevated
    /// removal then runs on its own) or the request failed; it never faults, so
    /// the caller can wait on it for a bounded time, or not at all.
    /// </summary>
    Task StartRemovingMachineEntry(string valueName);
}
