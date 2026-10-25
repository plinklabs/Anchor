using System.Diagnostics;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Asserting e2e for the session-start sweep (#104): an off-list app already
/// open when a session begins must be minimized at join, even though it never
/// fires a foreground <em>change</em> event. Drives the real agent against the
/// real backend and observes the sweep through <c>/status.startupSweep</c> — the
/// headless lever added alongside the feature, so this clears the "seen in a
/// running agent" bar rather than only a green unit test.
///
/// Note: exercising the real feature minimizes <em>every</em> off-list top-level
/// window on the desktop while it runs (that is the feature). Like the visual
/// specs, this drives the live desktop — expect other windows to be minimized
/// during the run.
/// </summary>
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class SessionStartSweepTests
{
    // Process names the Development backend always allows (backend
    // SessionAllowlist.DevelopmentApps): VS Code (#125) and Claude — the desktop
    // app and the Claude Code binary it runs are both claude.exe (#372).
    private static readonly string[] DevelopmentCarveoutApps = { "Code", "claude" };

    private readonly BackendFixture _backend;
    public SessionStartSweepTests(BackendFixture backend) => _backend = backend;

    /// <summary>
    /// The e2e backend boots under <c>Development</c> (see BackendProcess), so
    /// every session it starts carries the dev carve-out apps. They must reach
    /// the agent's live matcher and be spared by the session-start sweep —
    /// otherwise every e2e run (and every local session) minimizes the window
    /// the developer is working in while the tests run. On a dev box with Claude
    /// or VS Code open, the sweep assertion checks those real windows; on a
    /// runner with neither open it holds trivially and the allowedApps assertion
    /// carries the spec.
    /// </summary>
    [Fact]
    public async Task DevelopmentCarveoutApps_ReachTheAgent_AndAreSparedBySweep()
    {
        var api = new BackendClient(_backend.Url);
        await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);
        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

        var classId = await api.FindClassIdAsync();
        // No bundles → only the baseline + the dev carve-outs are allowed.
        var session = await api.StartSessionAsync(classId);
        try
        {
            var joined = await agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id && s.StartupSweep is not null, TimeSpan.FromSeconds(8));
            Assert.True(
                joined?.JoinedSessionId == session.Id && joined.StartupSweep is not null,
                $"Agent did not auto-join and sweep within 8s (joinedSessionId: " +
                $"{joined?.JoinedSessionId?.ToString() ?? "<none>"}, sweep: {joined?.StartupSweep is not null}).");

            var allowed = joined!.AllowedApps ?? Array.Empty<string>();
            var minimized = joined.StartupSweep!.MinimizedProcesses ?? Array.Empty<string>();
            foreach (var app in DevelopmentCarveoutApps)
            {
                Assert.True(
                    allowed.Contains(app, StringComparer.OrdinalIgnoreCase),
                    $"The Development carve-out '{app}' never reached the agent's matcher. " +
                    $"allowedApps: [{string.Join(", ", allowed)}].");
                Assert.False(
                    minimized.Contains(app, StringComparer.OrdinalIgnoreCase),
                    $"The session-start sweep minimized the Development carve-out '{app}'. " +
                    $"minimized: [{string.Join(", ", minimized)}].");
            }
        }
        finally
        {
            await api.EndSessionAsync(session.Id);
        }
    }

    [Fact]
    public async Task OffListWindowOpenAtSessionStart_IsMinimizedBySweep()
    {
        var api = new BackendClient(_backend.Url);

        // A fresh off-list window must be up *before* the session starts — the
        // whole point of #104 is the app that predates enforcement. Win11 notepad
        // is a tabbed launcher, so kill leftovers first to guarantee a new window.
        KillNotepad();
        using var notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
            ?? throw new InvalidOperationException("Failed to launch notepad.");
        try
        {
            Assert.True(
                await WaitForNotepadWindowAsync(TimeSpan.FromSeconds(10)),
                "Notepad did not present a top-level window within 10s.");

            await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

            var classId = await api.FindClassIdAsync();
            // No bundles → notepad is off-list (only the baseline survives).
            var session = await api.StartSessionAsync(classId);
            try
            {
                var joined = await agent.WaitForAsync(
                    s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
                Assert.True(
                    joined?.JoinedSessionId == session.Id,
                    $"Agent did not auto-join within 8s (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");

                var swept = await agent.WaitForAsync(
                    s => s.StartupSweep is not null, TimeSpan.FromSeconds(5));
                var sweep = swept?.StartupSweep;
                Assert.True(sweep is not null, "Agent never reported a startup sweep on /status within 5s.");

                var minimized = sweep!.MinimizedProcesses ?? Array.Empty<string>();
                Assert.True(
                    minimized.Any(p => p.Equals("notepad", StringComparison.OrdinalIgnoreCase)),
                    $"Session-start sweep did not minimize notepad. examined={sweep.WindowsExamined}, " +
                    $"minimized=[{string.Join(", ", minimized)}].");
            }
            finally
            {
                await api.EndSessionAsync(session.Id);
            }
        }
        finally
        {
            KillNotepad();
        }
    }

    /// <summary>
    /// #440: an off-list app that is not responding when the session begins must
    /// not hold up the sweep. Minimizing a window waits until its app handles
    /// messages again: done in line, the sweep stalled on the hung app, and every
    /// off-list window after it stayed on screen until that app responded. The
    /// hung window itself goes down the moment its app responds. Observes the
    /// test's own windows, which only the agent's sweep minimizes.
    /// </summary>
    [Fact]
    public async Task OffListAppNotRespondingAtSessionStart_DoesNotHoldUpTheSweep()
    {
        var api = new BackendClient(_backend.Url);
        // Opened last, the hung window is the one above in the Z-order, which is the
        // order the sweep takes the windows in.
        using var responsive = await OffListWindow.OpenAsync("Anchor e2e off-list app", TimeSpan.FromSeconds(10));
        using var hung = await OffListWindow.OpenAsync("Anchor e2e off-list app (not responding)", TimeSpan.FromSeconds(10));

        await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);
        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
        var classId = await api.FindClassIdAsync();

        // No bundles: both windows are off-list.
        hung.Freeze(NotRespondingFor);
        var session = await api.StartSessionAsync(classId);
        try
        {
            var swept = await agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id && s.StartupSweep is not null, TimeSpan.FromSeconds(5));
            var responsiveDown = await WaitUntilAsync(() => responsive.HasGoneDown, TimeSpan.FromSeconds(2));
            Assert.True(hung.IsFrozen, "The off-list app started responding again before the spec could check the sweep.");
            Assert.True(
                swept?.JoinedSessionId == session.Id && swept.StartupSweep is not null,
                "The session-start sweep did not finish while an off-list app was not responding (#440) " +
                $"(joinedSessionId: {swept?.JoinedSessionId?.ToString() ?? "<none>"}).");
            Assert.True(
                responsiveDown,
                "While an off-list app was not responding, the session-start sweep left another off-list app " +
                "on screen (#440).");
            Assert.False(hung.HasGoneDown, "A window whose app is not responding went down.");

            hung.Thaw();
            Assert.True(
                await WaitUntilAsync(() => hung.HasGoneDown, TimeSpan.FromSeconds(3)),
                "The off-list app that was not responding did not go down once it responded again.");
        }
        finally
        {
            hung.Thaw();
            await api.EndSessionAsync(session.Id);
        }
    }

    // Long enough to outlast the join and the spec's checks; the spec ends it
    // itself once it has looked.
    private static readonly TimeSpan NotRespondingFor = TimeSpan.FromSeconds(10);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    private static async Task<bool> WaitForNotepadWindowAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            // GetProcessesByName returns fresh handles each call, so MainWindowHandle
            // isn't stale-cached. Win11 may spawn the visible window under a pid that
            // differs from the launcher we started — match by name, not by pid.
            foreach (var p in Process.GetProcessesByName("notepad"))
            {
                using (p)
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                        return true;
                }
            }
            await Task.Delay(200);
        }
        return false;
    }

    private static void KillNotepad()
    {
        foreach (var p in Process.GetProcessesByName("notepad"))
        {
            using (p)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            }
        }
    }
}
