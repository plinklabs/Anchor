namespace FocusAgent.Core.Updates;

/// <summary>
/// A thin, testable seam over Velopack's <c>UpdateManager</c> (#224). Core stays
/// free of the Velopack dependency (which lives only in the WinUI app project),
/// so the cadence/gating policy in <see cref="AgentUpdateService"/> can be
/// unit-tested against a fake instead of a real install + GitHub feed.
///
/// <para>
/// The methods cover the two phases the running agent does itself, check and
/// download, plus a read-back of what the next start will apply. Applying is
/// deliberately not on this seam (#449): Velopack's updater gives a running agent
/// 60 s to exit and then kills it, so the agent never starts it. A downloaded
/// package is applied by <c>VelopackApp</c>'s auto-apply when the agent next
/// starts (Program.Main). The production implementation is a near-passthrough and
/// the policy (when to check, how often, whether the agent is even updatable)
/// stays in <see cref="AgentUpdateService"/>.
/// </para>
/// </summary>
public interface IAgentUpdateManager
{
    /// <summary>
    /// True only when this agent is a real Velopack install (it was set up via the
    /// Setup.exe / update feed). A <c>dotnet run</c> or any self-test launch is
    /// <em>not</em> installed, so the auto-update check must
    /// no-op there rather than fight the dev/build environment (#224 scope:
    /// "respect the unpackaged (no-admin) install").
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Ask the configured feed whether a newer release exists. Returns the target
    /// version string when an update is available, or <c>null</c> when the agent is
    /// already current (or isn't an install). Must not throw for the ordinary
    /// "feed unreachable" / "no update" cases — those are normal and surface as
    /// <c>null</c> so a flaky network never crashes the agent.
    /// </summary>
    Task<AgentUpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Download the delta (or full, if no usable delta) package for the update
    /// discovered by <see cref="CheckForUpdateAsync"/> into the install's packages
    /// folder, and nothing more: it must not start the updater or touch the running
    /// agent (#449). The package then waits there for the next agent start, which
    /// applies it. Students keep working; an in-session student is never interrupted
    /// and single-instance gating is never fought (#224 scope). Best-effort: a
    /// failure leaves the agent on its current version.
    /// </summary>
    Task DownloadUpdateAsync(AgentUpdateCheckResult update, CancellationToken cancellationToken = default);

    /// <summary>
    /// The version a downloaded package will bring the next time the agent starts,
    /// or <c>null</c> when no newer package is waiting. This is Velopack's own
    /// "update pending restart" test (a full package in the packages folder newer
    /// than the installed version), the same one its startup auto-apply uses, so a
    /// non-null answer means the next start applies that version.
    /// </summary>
    string? GetPendingRestartVersion();
}

/// <summary>
/// The outcome of a feed check. <see cref="IsUpdateAvailable"/> is the only thing
/// the policy needs to branch on; <see cref="TargetVersion"/> is carried for
/// logging and so the result can be threaded back into the download. The opaque
/// <see cref="Payload"/> lets the production implementation round-trip Velopack's
/// own <c>UpdateInfo</c> without leaking the type into Core.
/// </summary>
public sealed record AgentUpdateCheckResult(bool IsUpdateAvailable, string? TargetVersion, object? Payload)
{
    /// <summary>The "already current / not an install" result.</summary>
    public static readonly AgentUpdateCheckResult None = new(false, null, null);
}
