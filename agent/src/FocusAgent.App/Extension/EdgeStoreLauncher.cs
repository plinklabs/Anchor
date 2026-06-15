using System.Diagnostics;
using FocusAgent.Core.Extension;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.App.Extension;

/// <summary>
/// The real <see cref="IStoreLauncher"/>: opens the Edge Add-ons store listing in
/// Microsoft Edge so the student can complete a one-click install (#211 guided
/// fallback). Launches <c>msedge.exe</c> explicitly (not the default browser) —
/// the extension is Edge-specific, so the listing must open in Edge regardless of
/// which browser the student set as default. Falls back to the shell's URL handler
/// if Edge can't be launched directly.
/// </summary>
public sealed class EdgeStoreLauncher : IStoreLauncher
{
    private readonly ILogger<EdgeStoreLauncher> _log;

    public EdgeStoreLauncher(ILogger<EdgeStoreLauncher>? log = null)
        => _log = log ?? NullLogger<EdgeStoreLauncher>.Instance;

    public void OpenStoreListing(string storeUrl)
    {
        try
        {
            // `msedge <url>` opens the listing in Edge specifically. UseShellExecute
            // lets Windows resolve msedge.exe off the App Paths / PATH without us
            // hard-coding an install location.
            Process.Start(new ProcessStartInfo("msedge", storeUrl) { UseShellExecute = true });
            _log.LogInformation("Opened the Edge Add-ons listing for guided install: {Url}", storeUrl);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Launching Edge directly failed; falling back to the shell URL handler.");
            try
            {
                Process.Start(new ProcessStartInfo(storeUrl) { UseShellExecute = true });
            }
            catch (Exception inner)
            {
                _log.LogWarning(inner, "Opening the store listing via the shell also failed.");
            }
        }
    }
}
