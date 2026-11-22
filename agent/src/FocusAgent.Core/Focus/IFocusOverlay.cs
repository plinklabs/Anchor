using System.Collections.Generic;
using FocusAgent.Core.Tamper;

namespace FocusAgent.Core.Focus;

/// <summary>
/// The topmost return-focus surface that takes focus when an off-list app
/// is minimized and there is no previously-allowed window to fall back to
/// (see issue #33). Concrete impl lives in the App layer (WinUI 3 window).
/// </summary>
public interface IFocusOverlay
{
    /// <summary>
    /// Show the overlay topmost. <paramref name="blockedAppName"/> is the
    /// app that was just blocked (purely informational on the overlay).
    /// <paramref name="blockedWindowHandle"/> is its window, which the overlay
    /// takes down and takes the foreground over from, so that what the student
    /// types goes to the overlay and not to the blocked app (#441, see
    /// <see cref="IFocusEnforcer.HandOverAsync"/>); 0 when there is none.
    /// Idempotent — calling Show while already visible just updates content.
    /// </summary>
    void Show(IReadOnlyList<AllowedAppRule> allowedRules, string? blockedAppName, nint blockedWindowHandle);

    /// <summary>
    /// Show the overlay for an Edge that is paused because its extension is off
    /// (#427): say why, offer <paramref name="prompt"/>'s way to switch the
    /// extension back on, and list <paramref name="allowedRules"/> (the other
    /// allowed apps) to carry on with. <paramref name="pausedWindowHandle"/> is the
    /// Edge window that has the foreground (#450), or an app blocked while Edge is
    /// paused with nothing to fall back to (#452), which the overlay takes down and
    /// takes the foreground over from, like <see cref="Show"/> does with the blocked
    /// window; 0 when there is none. Idempotent like <see cref="Show"/>.
    /// <paramref name="stillPaused"/> says whether the browser is still paused. The
    /// overlay gets to a show a moment after it is asked for, and hands the window
    /// over a moment later still: once it returns false (the extension reconnected,
    /// or the repair window opened, #474) the overlay neither comes up for this call
    /// nor takes the window down. Null when the window goes down regardless: an app
    /// blocked while the browser is paused (#452).
    /// </summary>
    void ShowBrowserPaused(
        IReadOnlyList<AllowedAppRule> allowedRules,
        BrowserPausedPrompt prompt,
        nint pausedWindowHandle,
        Func<bool>? stillPaused = null);

    /// <summary>
    /// Hide the overlay (kept alive for fast re-show). Safe to call when
    /// already hidden.
    /// </summary>
    void Hide();

    /// <summary>
    /// Permanently dispose the overlay. Must clear <c>HWND_TOPMOST</c>
    /// before destroying the window so the OS doesn't leave a phantom
    /// topmost handle (per #33 acceptance criteria).
    /// </summary>
    void Close();
}

/// <summary>
/// The "turn the extension back on" action the paused-Edge overlay offers (#427).
/// </summary>
/// <param name="Browser">The paused browser, brought forward once the repair window is open.</param>
/// <param name="RepairWindow">How long the browser opens for.</param>
/// <param name="TurnBackOn">
/// Opens the repair window. Called before <paramref name="Browser"/> is brought
/// forward, so its foreground change already finds it allowed. Returns false when
/// the browser isn't paused any more (the extension came back meanwhile). For
/// <see cref="ExtensionPauseReason.NoSiteAccess"/> it also has the extension open
/// its own settings page (#524), and for
/// <see cref="ExtensionPauseReason.SchoolProfileWithoutExtension"/> it opens the
/// store listing in the school profile (#536); for
/// <see cref="ExtensionPauseReason.NotConnected"/> it opens the profile the
/// extension was woken in again (#556).
/// </param>
/// <param name="Reason">
/// Why the browser is paused: what the overlay tells the student and what its
/// action is for — the extension switched off, its site access taken away (#524),
/// no Edge profile signed in with the school account, or (for one window) a window
/// outside the school profile (#525), or the school profile without the extension
/// (#536), or the extension installed but never connected (#556). For
/// <see cref="ExtensionPauseReason.OutsideSchoolProfile"/> and
/// <see cref="ExtensionPauseReason.SchoolProfileWithoutExtension"/>,
/// <paramref name="TurnBackOn"/> opens the school profile's window itself, so the
/// overlay brings no Edge window forward after it: that could be another profile's.
/// </param>
/// <param name="Account">
/// The school account Edge must be signed in with, which the pause for
/// <see cref="ExtensionPauseReason.NoSchoolProfile"/> names (#525).
/// </param>
public sealed record BrowserPausedPrompt(
    AllowedAppRule Browser,
    TimeSpan RepairWindow,
    Func<bool> TurnBackOn,
    ExtensionPauseReason Reason = ExtensionPauseReason.ExtensionOff,
    string? Account = null);
