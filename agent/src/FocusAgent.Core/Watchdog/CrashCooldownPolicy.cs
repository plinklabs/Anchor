namespace FocusAgent.Core.Watchdog;

/// <summary>
/// Tracks how often the supervised App has died inside a rolling window and
/// decides when the supervisor should pause relaunching. Spec (issue #35):
/// "if the App has crashed 5 times in 60s, stop relaunching for 5 minutes".
/// </summary>
/// <remarks>
/// Pure logic — no clock, no filesystem, no threading. Callers supply the
/// current time and a <see cref="WatchdogPolicy"/>; the type only owns the
/// crash timestamps. This is what makes the cooldown unit-testable.
/// </remarks>
public sealed class CrashCooldownPolicy
{
    private readonly WatchdogPolicy _policy;
    private readonly Queue<DateTimeOffset> _crashes = new();
    private DateTimeOffset? _cooldownUntil;

    public CrashCooldownPolicy(WatchdogPolicy policy)
    {
        _policy = policy;
    }

    /// <summary>
    /// Total crashes recorded (across all time, not just the rolling window).
    /// Useful for logging.
    /// </summary>
    public int TotalCrashes { get; private set; }

    /// <summary>
    /// When the current cooldown elapses, or <c>null</c> if no cooldown is active.
    /// </summary>
    public DateTimeOffset? CooldownUntil => _cooldownUntil;

    /// <summary>
    /// Record one observed crash and re-evaluate whether the relaunch budget
    /// is exhausted. Returns the new state.
    /// </summary>
    public CrashEvaluation RecordCrash(DateTimeOffset at)
    {
        TotalCrashes++;
        _crashes.Enqueue(at);
        EvictOutsideWindow(at);

        if (_crashes.Count >= _policy.CrashWindowLimit)
        {
            _cooldownUntil = at + _policy.CrashCooldown;
            // Clear the window so when the cooldown elapses we start fresh
            // rather than immediately re-tripping on the same old crashes.
            _crashes.Clear();
            return new CrashEvaluation(InCooldown: true, CooldownUntil: _cooldownUntil, CrashesInWindow: 0);
        }

        return new CrashEvaluation(InCooldown: false, CooldownUntil: null, CrashesInWindow: _crashes.Count);
    }

    /// <summary>
    /// Returns <c>true</c> if the supervisor is currently inside a cooldown
    /// and must not relaunch yet. Side-effect: clears <see cref="CooldownUntil"/>
    /// when the cooldown has elapsed, so the next crash starts a fresh window.
    /// </summary>
    public bool IsInCooldown(DateTimeOffset now)
    {
        if (_cooldownUntil is { } until && now < until) return true;
        if (_cooldownUntil is not null && now >= _cooldownUntil)
        {
            _cooldownUntil = null;
        }
        return false;
    }

    private void EvictOutsideWindow(DateTimeOffset now)
    {
        var cutoff = now - _policy.CrashWindow;
        while (_crashes.Count > 0 && _crashes.Peek() < cutoff)
        {
            _crashes.Dequeue();
        }
    }
}

public readonly record struct CrashEvaluation(
    bool InCooldown,
    DateTimeOffset? CooldownUntil,
    int CrashesInWindow);
