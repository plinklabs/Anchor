using System.Threading;
using FocusAgent.Core.Watchdog;

namespace FocusAgent.Watchdog;

/// <summary>
/// Probes App presence via <see cref="Mutex.TryOpenExisting(string, out Mutex)"/>.
/// The App holds <see cref="AnchorWatchdogPaths.AppPresenceMutexName"/> for the
/// lifetime of its process; opening succeeds while it's alive and fails
/// (kernel object gone) the moment the process dies — whether cleanly or
/// from <c>Stop-Process -Force</c>. We immediately dispose the handle so we
/// don't accidentally extend the object's lifetime past the App's exit.
/// </summary>
internal sealed class MutexAppPresenceProbe : IAppPresenceProbe
{
    public bool IsAppAlive()
    {
        if (Mutex.TryOpenExisting(AnchorWatchdogPaths.AppPresenceMutexName, out var handle))
        {
            handle.Dispose();
            return true;
        }
        return false;
    }
}
