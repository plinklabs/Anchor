namespace FocusAgent.Core.Focus;

/// <summary>
/// Enumerates the visible, top-level application windows currently open on the
/// desktop, each resolved to the identity of its owning app. The session-start
/// sweep (#104) consumes this: the foreground hook only fires on focus
/// <em>changes</em>, so an off-list app already foregrounded when a session
/// begins would never be enforced against until the student alt-tabbed. Sweeping
/// the open windows on join closes that gap.
/// </summary>
public interface IWindowEnumerator
{
    /// <summary>
    /// Snapshot of the visible top-level windows (the same set Windows would
    /// surface in alt-tab), each paired with its resolved <see cref="AppInfo"/>.
    /// Windows whose identity can't be resolved are omitted.
    /// </summary>
    IReadOnlyList<OpenWindow> GetOpenWindows();
}

/// <summary>A visible top-level window paired with its resolved app identity.</summary>
public sealed record OpenWindow(nint Handle, AppInfo App);
