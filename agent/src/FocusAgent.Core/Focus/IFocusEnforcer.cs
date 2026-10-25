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
    /// Blocks the off-list window. Returns true if a previously remembered allowed
    /// window is there to fall back to: the blocked window (a dialog together with
    /// the app window that owns it, like <see cref="Minimize"/>) then minimizes
    /// itself, so Windows hands the foreground to the window below it, and a
    /// minimized fallback is restored first (#438). Returns false if there was
    /// nothing valid to fall back to: the window is left as it is, for the caller
    /// to surface the overlay and hand the window over to it with
    /// <see cref="HandOverAsync"/> (#441). Never held up by a blocked app that is not
    /// responding, like <see cref="Minimize"/> (#440).
    /// </summary>
    bool Block(nint offendingWindowHandle);

    /// <summary>
    /// Takes a blocked window down so that <paramref name="successorWindowHandle"/>,
    /// the focus overlay, gets the foreground and with it the keyboard (#441). The
    /// agent may not give its own window the foreground, so the successor is put
    /// directly below the blocked window, and the blocked window minimizes itself:
    /// Windows then activates the window below it. A blocked window that is already
    /// minimized but still has the foreground is restored first, because a window
    /// that is minimized already hands nothing on when it is minimized again; one
    /// that is minimized without the foreground is left as it is. When the
    /// successor has the foreground already (the same window blocked twice), the
    /// window only goes down. The successor loses its topmost place for the
    /// hand-over; the caller puts it back once the task completes. Never held up
    /// by the blocked app. The task completes with true once the successor has the
    /// foreground, or with false when it did not get it within a short time (an
    /// app that is not responding).
    /// </summary>
    Task<bool> HandOverAsync(nint offendingWindowHandle, nint successorWindowHandle);

    /// <summary>
    /// The foreground window, the one the student's keystrokes go to, when it is
    /// <paramref name="windowHandle"/> or a dialog that window owns (#428); 0 when
    /// the foreground is elsewhere. Pausing the browser hands that window over to
    /// the overlay with <see cref="HandOverAsync"/> rather than minimizing it with
    /// <see cref="Minimize"/> (#450).
    /// </summary>
    nint ForegroundWindowOf(nint windowHandle);

    void Reset();
}
