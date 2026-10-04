namespace Anchor.Api.Sessions;

public sealed class SessionAutoEndOptions
{
    public const string SectionName = "SessionAutoEnd";

    /// <summary>
    /// A session still running this long after it started is treated as
    /// forgotten and ended by <see cref="SessionAutoEnder"/> (#345). The default
    /// matches the age at which a join code stops working
    /// (<c>SessionsController.JoinCodeFreshnessWindow</c>): long enough for a
    /// double period or an exam, short enough that a session left running never
    /// reaches the next school day. Zero or negative turns auto-ending off.
    /// </summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromHours(4);

    /// <summary>
    /// How often <see cref="SessionAutoEnder"/> looks for forgotten sessions. It
    /// waits one interval before its first look, so a (re)start doesn't query the
    /// database (#344). Floored at one second.
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
