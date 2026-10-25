namespace FocusAgent.Core.Focus;

public interface IFocusEnforcer
{
    /// <summary>
    /// Remembers an allowed window as the one <see cref="Block"/> hands focus back
    /// to. Windows that are no place to return a student to, such as explorer's
    /// taskbar, its flyouts and the desktop, or a window already hidden, are
    /// ignored and leave the previous one in place (#437).
    /// </summary>
    void RememberAllowed(nint windowHandle);

    /// <summary>
    /// Minimizes a single window without touching foreground. Used by the
    /// session-start sweep (#104) to bring down off-list windows that were
    /// already open: unlike <see cref="Block"/> it deliberately does not try to
    /// restore a previously-allowed window, because a bulk sweep has no single
    /// window to hand focus back to. A dialog goes down with the app window that
    /// owns it (#428): minimizing the owner hides its dialogs too, and they come
    /// back with the app. Never held up by an app that is not responding (#440):
    /// the caller carries on after a moment, and the window goes down the moment its
    /// app responds.
    /// </summary>
    void Minimize(nint windowHandle);

    /// <summary>
    /// Minimizes the off-list window (a dialog together with the app window that
    /// owns it, like <see cref="Minimize"/>). Returns true if a previously
    /// remembered allowed window is there to fall back to: the blocked window then
    /// minimizes itself, so Windows hands the foreground to the window below it,
    /// and a minimized fallback is restored first (#438). Returns false if there
    /// was nothing valid to fall back to (caller should surface the overlay).
    /// Never held up by a blocked app that is not responding, like
    /// <see cref="Minimize"/> (#440).
    /// </summary>
    bool Block(nint offendingWindowHandle);

    void Reset();
}
