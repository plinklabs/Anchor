using System.Collections.Generic;

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
    /// Edge window that has the foreground, which the overlay takes down and takes
    /// the foreground over from, like <see cref="Show"/> does with the blocked
    /// window (#450); 0 when no Edge window has it. Idempotent like <see cref="Show"/>.
    /// </summary>
    void ShowBrowserPaused(IReadOnlyList<AllowedAppRule> allowedRules, BrowserPausedPrompt prompt, nint pausedWindowHandle);

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
/// the browser isn't paused any more (the extension came back meanwhile).
/// </param>
public sealed record BrowserPausedPrompt(AllowedAppRule Browser, TimeSpan RepairWindow, Func<bool> TurnBackOn);
