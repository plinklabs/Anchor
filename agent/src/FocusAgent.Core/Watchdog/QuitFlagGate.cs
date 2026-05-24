namespace FocusAgent.Core.Watchdog;

/// <summary>
/// Decides whether a <c>quit.flag</c> sentinel should suppress relaunch.
/// Pure logic — callers supply the file's last-write timestamp (or
/// <c>null</c> if absent) and the current time; the gate has no filesystem
/// access of its own so the policy is straightforward to unit-test.
/// </summary>
public static class QuitFlagGate
{
    /// <summary>
    /// Returns <c>true</c> if the flag is present AND its last-write time is
    /// within <paramref name="freshness"/> of <paramref name="now"/>. A stale
    /// flag (older than the window) is intentionally ignored — it usually
    /// means the user quit hours ago and the App has since been relaunched
    /// and re-quitted by some other path, or the LocalAppData was restored
    /// from a backup. We don't want an ancient sentinel to permanently
    /// disable supervision.
    /// </summary>
    public static bool ShouldSuppressRelaunch(
        DateTimeOffset? flagLastWriteUtc,
        DateTimeOffset now,
        TimeSpan freshness)
    {
        if (flagLastWriteUtc is not { } written) return false;
        var age = now - written;
        if (age < TimeSpan.Zero) return true; // clock skew: treat as fresh
        return age <= freshness;
    }
}
