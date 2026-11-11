namespace Anchor.Api.Sessions;

public sealed class SessionAutoEndOptions
{
    public const string SectionName = "SessionAutoEnd";

    /// <summary>
    /// A session still running this long after it started is treated as
    /// forgotten and ended by <see cref="SessionAutoEnder"/> (#345). Since a
    /// session ends at its planned end (#447), this is the last safety net, for
    /// a session started without one. It also caps the end a teacher may pick.
    /// The default matches the age at which a join code stops working
    /// (<c>SessionsController.JoinCodeFreshnessWindow</c>): long enough for a
    /// double period or an exam, short enough that a session left running never
    /// reaches the next school day. Zero or negative turns this safety net off;
    /// planned ends still apply.
    /// </summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromHours(4);

    /// <summary>
    /// How often <see cref="SessionAutoEnder"/> looks for forgotten sessions,
    /// and for planned ends (#447) it didn't know of: those of sessions started
    /// before the backend last restarted. A planned end it knows of doesn't wait
    /// for a sweep. It waits one interval before its first look, so a (re)start
    /// doesn't query the database (#344). Floored at one second.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Mirrors <see cref="Events.EventRetentionOptions.EnablePruner"/> and
    /// <see cref="Realtime.HeartbeatOptions.EnableMonitor"/>: when false,
    /// <see cref="SessionAutoEnder"/> is not registered as a hosted service, and
    /// tests drive <see cref="SessionAutoEnder.EndForgottenSessionsAsync"/>
    /// directly.
    /// </summary>
    public bool EnableAutoEnder { get; set; } = true;

    public TimeSpan EffectiveSweepInterval =>
        SweepInterval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : SweepInterval;
}
