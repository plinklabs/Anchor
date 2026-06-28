namespace FocusAgent.Core.Updates;

/// <summary>
/// A thin, testable seam over Velopack's <c>UpdateManager</c> (#224). Core stays
/// free of the Velopack dependency (which lives only in the WinUI app project),
/// so the cadence/gating policy in <see cref="AgentUpdateService"/> can be
/// unit-tested against a fake instead of a real install + GitHub feed.
///
/// <para>
/// The three methods mirror Velopack's own three-phase flow — check, download,
/// apply — so the production implementation is a near-passthrough and the policy
/// (when to check, how often, whether the agent is even updatable) stays here.
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
    /// discovered by <see cref="CheckForUpdateAsync"/>. Best-effort: a failure
    /// leaves the agent on its current version.
    /// </summary>
    Task DownloadUpdateAsync(AgentUpdateCheckResult update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stage the downloaded update to be applied on the next ordinary restart,
    /// WITHOUT relaunching now. Students keep working; the new version takes effect
    /// the next time the agent starts (single-instance gating and an in-session
    /// student are never interrupted mid-flight — #224 scope: "don't fight the
    /// single-instance gating").
    /// </summary>
    void StageUpdateForNextRestart(AgentUpdateCheckResult update);
}

/// <summary>
/// The outcome of a feed check. <see cref="IsUpdateAvailable"/> is the only thing
/// the policy needs to branch on; <see cref="TargetVersion"/> is carried for
/// logging and so the result can be threaded back into download/apply. The opaque
/// <see cref="Payload"/> lets the production implementation round-trip Velopack's
/// own <c>UpdateInfo</c> without leaking the type into Core.
/// </summary>
public sealed record AgentUpdateCheckResult(bool IsUpdateAvailable, string? TargetVersion, object? Payload)
{
    /// <summary>The "already current / not an install" result.</summary>
    public static readonly AgentUpdateCheckResult None = new(false, null, null);
}
