using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace FocusAgent.App.Updates;

/// <summary>
/// Tells the agent when the laptop resumes from sleep or hibernation, or the student
/// unlocks the screen (#456), so it can check for an update the interval timer,
/// which leaves out time asleep, would only get to days later.
///
/// <para>
/// Built on <see cref="SystemEvents"/>: <c>PowerModeChanged</c> (resume) and
/// <c>SessionSwitch</c> (unlock). The unlock matters as much as the resume: on a
/// laptop with modern standby the resume notice may not come at all, but a student
/// opening the lid unlocks the screen. Subscribed from a thread-pool thread, so
/// SystemEvents runs its hidden window on a thread of its own rather than relying on
/// the WinUI message loop.
/// </para>
/// </summary>
internal sealed class WakeWatcher : IDisposable
{
    /// <summary>The reason passed on a resume from sleep or hibernation.</summary>
    public const string ResumeReason = "resumed from sleep";

    /// <summary>The reason passed on an unlock of the screen.</summary>
    public const string UnlockReason = "was unlocked";

    private readonly Action<string> _onWake;
    private readonly ILogger _log;
    private int _subscribed;

    public WakeWatcher(Action<string> onWake, ILogger log)
    {
        _onWake = onWake;
        _log = log;
    }

    public void Start() => _ = Task.Run(() =>
    {
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            Volatile.Write(ref _subscribed, 1);
        }
        catch (Exception ex)
        {
            // Without these the interval timer still checks, just later.
            _log.LogWarning(ex, "Could not watch for wake and unlock; update checks follow the interval only.");
        }
    });

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Raise(ResumeReason);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock) Raise(UnlockReason);
    }

    private void Raise(string reason)
    {
        try
        {
            _onWake(reason);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Handling the agent's wake ({Reason}) failed.", reason);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _subscribed, 0) == 0) return;
        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
        catch
        {
            // best-effort, on the way out
        }
    }
}
