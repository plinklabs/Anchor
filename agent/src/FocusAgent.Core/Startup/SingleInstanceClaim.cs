namespace FocusAgent.Core.Startup;

/// <summary>
/// The agent's single-instance key, as the claim sees it (#461): WinAppSDK's
/// <c>AppInstance.FindOrRegisterForKey</c> in the app.
/// </summary>
public interface ISingleInstanceKey
{
    /// <summary>
    /// Register this process for the key. Null when this process holds it now;
    /// otherwise the id of the process that holds it, or 0 when it is held but no
    /// holder could be named.
    /// </summary>
    uint? RegisterOrFindHolder();
}

/// <summary>Where the process holding the single-instance key is in its life (#461).</summary>
public enum KeyHolderState
{
    /// <summary>Running: another agent is up, and it stays the agent.</summary>
    Running,

    /// <summary>
    /// Exiting: ended (killed or quit) and its exit code is set, but Windows is
    /// still tearing it down, so the key is not free yet.
    /// </summary>
    Exiting,

    /// <summary>Gone entirely; the key is free or about to be.</summary>
    Gone,
}

/// <summary>Looks at the process holding the key, and waits for it to go (#461).</summary>
public interface IKeyHolderProcesses
{
    /// <summary>Where process <paramref name="processId"/> is in its life.</summary>
    KeyHolderState StateOf(uint processId);

    /// <summary>
    /// Wait up to <paramref name="timeout"/> for process <paramref name="processId"/>
    /// to be gone entirely. True when it is.
    /// </summary>
    bool WaitUntilGone(uint processId, TimeSpan timeout);
}

/// <summary>
/// Claims the agent's single-instance key at startup (#461), so a launch only steps
/// aside for an agent that is actually running.
///
/// <para>
/// The key is a named mutex owned by the UI thread of the agent that holds it, and
/// Windows releases it only once that thread has exited. A killed agent is not gone
/// when its exit code is: Windows tears the process down after that, and when the UI
/// thread is the last to go it holds the key through the whole teardown (up to about
/// three quarters of a second on a dev laptop, in or out of a session). A launch in
/// that window used to find the key held, take the dying agent for a running one and
/// exit at once with code 0, leaving no agent. So when the holder is exiting, the
/// claim waits for it to be gone and registers again. A holder that is running is a
/// real second launch, which still exits as before.
/// </para>
///
/// <para>
/// A clean exit is not covered: Windows sets its exit code only at the very end, so
/// until then it can't be told from a running agent. The restarts that follow one
/// (Velopack's updater) wait for the process to be gone first.
/// </para>
/// </summary>
public static class SingleInstanceClaim
{
    /// <summary>
    /// Longest a launch waits for an exiting holder to be gone. Teardown takes well
    /// under a second; this leaves room for a slow, busy laptop.
    /// </summary>
    public static readonly TimeSpan ExitingHolderBudget = TimeSpan.FromSeconds(10);

    /// <summary>Pause before registering again when the holder is gone or can't be named.</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// True when this process now holds the key and becomes the agent; false when
    /// another agent is running (or an exiting one is still not gone after
    /// <paramref name="budget"/>), and this launch should exit.
    /// </summary>
    public static bool TryClaim(
        ISingleInstanceKey key,
        IKeyHolderProcesses processes,
        TimeSpan budget,
        TimeProvider? time = null,
        Action<TimeSpan>? pause = null)
    {
        time ??= TimeProvider.System;
        pause ??= Thread.Sleep;
        var started = time.GetTimestamp();

        while (true)
        {
            var holder = key.RegisterOrFindHolder();
            if (holder is null) return true;

            var state = holder.Value == 0 ? KeyHolderState.Gone : processes.StateOf(holder.Value);
            if (state == KeyHolderState.Running) return false;

            var left = budget - time.GetElapsedTime(started);
            if (left <= TimeSpan.Zero) return false;

            if (state == KeyHolderState.Exiting)
                processes.WaitUntilGone(holder.Value, left);
            else
                pause(left < RetryDelay ? left : RetryDelay);
        }
    }
}
