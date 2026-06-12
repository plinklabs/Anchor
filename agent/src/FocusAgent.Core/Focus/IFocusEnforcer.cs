namespace FocusAgent.Core.Focus;

public interface IFocusEnforcer
{
    void RememberAllowed(nint windowHandle);

    /// <summary>
    /// Minimizes a single window without touching foreground. Used by the
    /// session-start sweep (#104) to bring down off-list windows that were
    /// already open: unlike <see cref="Block"/> it deliberately does not try to
    /// restore a previously-allowed window, because a bulk sweep has no single
    /// window to hand focus back to.
    /// </summary>
    void Minimize(nint windowHandle);

    /// <summary>
    /// Minimizes the off-list window. Returns true if focus was returned to a
    /// previously remembered allowed window; false if there was nothing valid
    /// to fall back to (caller should surface the overlay).
    /// </summary>
    bool Block(nint offendingWindowHandle);

    void Reset();
}
