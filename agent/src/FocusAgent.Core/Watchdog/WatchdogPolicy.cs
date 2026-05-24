namespace FocusAgent.Core.Watchdog;

/// <summary>
/// Tunable knobs for the supervisor loop. Defaults match the spec in
/// issue #35 — a 5s poll, a 5-crash / 60s crash-window, a 5-minute cooldown,
/// and a 10s window during which a fresh <c>quit.flag</c> suppresses
/// relaunch.
/// </summary>
public sealed record WatchdogPolicy(
    TimeSpan PollInterval,
    int CrashWindowLimit,
    TimeSpan CrashWindow,
    TimeSpan CrashCooldown,
    TimeSpan QuitFlagFreshness)
{
    public static WatchdogPolicy Default { get; } = new(
        PollInterval: TimeSpan.FromSeconds(5),
        CrashWindowLimit: 5,
        CrashWindow: TimeSpan.FromSeconds(60),
        CrashCooldown: TimeSpan.FromMinutes(5),
        QuitFlagFreshness: TimeSpan.FromSeconds(10));
}
