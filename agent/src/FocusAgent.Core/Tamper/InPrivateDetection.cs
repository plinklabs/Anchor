namespace FocusAgent.Core.Tamper;

/// <summary>
/// Pure agent-side InPrivate classification (#148), kept free of Win32 so it can
/// be unit-tested without a live desktop — the same enumeration-vs-decision split
/// the focus stack uses (<c>WindowEnumerator.IsCandidate</c>) and the extension
/// uses (<c>classifyCreatedWindow</c> in extension/src/shared/tamper.ts).
///
/// The robust agent signal (design §5.4): the extension can only witness an
/// InPrivate window once it has been *allowed* in InPrivate, so a student who
/// leaves that toggle off escapes the in-browser witness entirely. The agent
/// closes that gap from outside the browser by recognising an Edge InPrivate
/// window from its title.
/// </summary>
public static class InPrivateDetection
{
    // Edge's executable, sans extension — what IBrowserWindowScanner reports as a
    // window's owning process name. Matched case-insensitively.
    private const string EdgeProcessName = "msedge";

    // "InPrivate" is a Microsoft brand term Edge keeps in English across locales
    // (unlike Chrome's localised "Incognito"), and it appears in every InPrivate
    // window's title — e.g. "<page> - [InPrivate] - Microsoft Edge" — but never in
    // an ordinary window's ("<page> - Personal - Microsoft Edge"). So a substring
    // match on the title, scoped to the Edge process, is the reliable signal.
    private const string InPrivateMarker = "InPrivate";

    /// <summary>
    /// True when <paramref name="windowTitle"/> belongs to an Edge InPrivate
    /// window. Scoped to the Edge process so an ordinary page that merely has
    /// "InPrivate" in its own title/URL (e.g. a docs page about the feature) on a
    /// non-Edge window can't trip it; the in-Edge case is accepted because the
    /// student would have to be reading about InPrivate *inside* Edge for that
    /// false positive, which is vanishingly rare and still a benign over-report.
    /// <paramref name="edgeProcessName"/> is Edge's process name, the configured
    /// browser (<c>Witness:BrowserProcessName</c>), which an e2e points at a
    /// stand-in of its own.
    /// </summary>
    public static bool IsInPrivateEdgeWindow(
        string? processName, string? windowTitle, string edgeProcessName = EdgeProcessName)
    {
        if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(windowTitle))
            return false;
        if (!string.Equals(processName, edgeProcessName, StringComparison.OrdinalIgnoreCase))
            return false;
        return windowTitle.Contains(InPrivateMarker, StringComparison.OrdinalIgnoreCase);
    }

    // How Edge marks an InPrivate browser window in its title, between the page and
    // "Microsoft Edge": "<page> - [InPrivate] - Microsoft Edge" (measured on Edge
    // 157, in English and Dutch alike).
    private const string InPrivateTitleMarker = "[InPrivate]";

    /// <summary>
    /// True when <paramref name="windowTitle"/> is that of an Edge InPrivate browser
    /// window (#525). An InPrivate window carries its profile's AppUserModelID, so
    /// only its title tells it from that profile's ordinary windows. Stricter than
    /// <see cref="IsInPrivateEdgeWindow"/>: what this says pauses the window rather
    /// than only reporting it, so a page that merely mentions InPrivate in its own
    /// title must not count.
    /// </summary>
    public static bool HasInPrivateTitle(string? windowTitle) =>
        windowTitle?.Contains(InPrivateTitleMarker, StringComparison.OrdinalIgnoreCase) == true;
}
