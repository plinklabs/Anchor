namespace FocusAgent.Core.Extension;

/// <summary>
/// Opens the Edge Add-ons store at Anchor's listing for the guided-install
/// fallback (#211). Abstracted so the side effect (shell-launching Edge) is
/// swappable — a no-op in headless verify/tests, the real launch in production.
/// </summary>
public interface IStoreLauncher
{
    /// <summary>Open <paramref name="storeUrl"/> in Edge at the store listing.</summary>
    void OpenStoreListing(string storeUrl);
}
