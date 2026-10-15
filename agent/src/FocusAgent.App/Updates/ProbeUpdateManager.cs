using FocusAgent.Core.Updates;

namespace FocusAgent.App.Updates;

/// <summary>
/// Dev-only stand-in for <see cref="VelopackUpdateManager"/> (#407), selected by
/// <c>--test-update-probe</c>. It claims to be an installed agent with a newer
/// release (<see cref="ProbeVersion"/>) on the feed, and appends each phase the
/// update path reaches (<c>check</c>, <c>download</c>, <c>stage 9.9.9</c>) as a line
/// to the file named by <see cref="Program.UpdateProbePathEnvVar"/>. That lets the
/// startup-crash e2e prove the real <see cref="AgentUpdateService"/> ran, and
/// finished, its pass on a launch that crashes, without GitHub, a real install or
/// Update.exe. The real Velopack check path has its own e2e (UpdateCheckTests).
///
/// <para>
/// The download is held open until the harness creates
/// <c>&lt;probe file&gt;.release</c> (bounded by <see cref="ReleaseTimeout"/>), so
/// the e2e can observe that a crash path keeps the process alive for an in-flight
/// update instead of exiting under it.
/// </para>
/// </summary>
internal sealed class ProbeUpdateManager : IAgentUpdateManager
{
    /// <summary>The "newer release" the probe reports, recorded on the stage line.</summary>
    public const string ProbeVersion = "9.9.9";

    // Bounded so a run that never releases the download (a human trying the flag)
    // still finishes. Shorter than CrashReporter.PendingUpdateGrace, so the crash
    // path's own bound is never what ends the e2e.
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(30);

    private static readonly object WriteLock = new();
    private static readonly TaskCompletionSource FirstCheck = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string? ProbePath =>
        Environment.GetEnvironmentVariable(Program.UpdateProbePathEnvVar) is { Length: > 0 } path ? path : null;

    public bool IsInstalled => true;

    public Task<AgentUpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        Record("check");
        FirstCheck.TrySetResult();
        return Task.FromResult(new AgentUpdateCheckResult(IsUpdateAvailable: true, ProbeVersion, Payload: null));
    }

    public async Task DownloadUpdateAsync(AgentUpdateCheckResult update, CancellationToken cancellationToken = default)
    {
        Record("download");
        if (ProbePath is not { } path) return;

        var release = path + ".release";
        var deadline = DateTime.UtcNow + ReleaseTimeout;
        while (!File.Exists(release) && DateTime.UtcNow < deadline)
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
    }

    public void StageUpdateForNextRestart(AgentUpdateCheckResult update) =>
        Record($"stage {update.TargetVersion}");

    /// <summary>
    /// Block until the probe's first check has started, or <paramref name="timeout"/>
    /// elapses. The <c>--test-crash-at</c> hook calls this before it crashes, so the
    /// "the update check started before the crash" ordering the e2e asserts doesn't
    /// depend on how fast the thread pool picks up the startup check.
    /// </summary>
    public static bool WaitForFirstCheck(TimeSpan timeout) => FirstCheck.Task.Wait(timeout);

    /// <summary>Append one line to the probe file. No-op without a probe path; never throws.</summary>
    public static void Record(string line)
    {
        if (ProbePath is not { } path) return;
        try
        {
            lock (WriteLock)
                File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // The e2e reads the file; a write failure shows up there as a missing line.
        }
    }
}
