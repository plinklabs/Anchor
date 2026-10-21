namespace FocusAgent.Core.Focus;

/// <summary>A window that came to the foreground (or back from minimized).</summary>
/// <param name="App">The process that shows the window.</param>
/// <param name="Owner">
/// The app window that owns this window, when that window belongs to another
/// process (#428): a dialog one process shows for another app's window, like the
/// file picker <c>PickerHost</c> opens when a page in Edge asks for an upload.
/// <c>null</c> for an app's own windows.
/// </param>
public sealed record ForegroundChange(
    AppInfo App,
    string? WindowTitle,
    int ProcessId,
    nint WindowHandle,
    ForegroundOwner? Owner = null)
{
    /// <summary>
    /// The app this window belongs to: the owner's app for a dialog another
    /// process shows for it, otherwise the window's own process. The session
    /// judges, enforces and reports a window as this app.
    /// </summary>
    public AppInfo OwningApp => Owner?.App ?? App;
}

/// <summary>
/// The top-level app window at the root of a foreground window's owner chain,
/// resolved only when it belongs to another process than the window itself.
/// </summary>
public sealed record ForegroundOwner(AppInfo App, int ProcessId, nint WindowHandle);
