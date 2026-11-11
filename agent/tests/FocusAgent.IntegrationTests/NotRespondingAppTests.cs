using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Asserting e2e for #440: an off-list app that is not responding never holds up
/// the agent. Minimizing a window waits until its app handles messages again, and
/// the agent used to do that on its UI thread when it blocked an app with no
/// allowed window to fall back to: every foreground change behind it waited until
/// the hung app responded, so the next off-list app the student opened stayed on
/// screen meanwhile. The session-start sweep's side of #440 is in
/// <see cref="SessionStartSweepTests"/>.
///
/// The real agent judges the test's own windows (<see cref="OffListWindow"/>), one
/// of which stops responding just as the agent takes it down. Both start out
/// minimized, so nothing comes to the foreground before the spec restores them and
/// the agent has no allowed window to fall back to. Restoring a minimized window
/// raises MINIMIZEEND, which the agent judges like a foreground change; real
/// foreground events on a headless desktop are the flaky part, so this runs in the
/// non-blocking Visual lane, like <see cref="FocusFallbackTests"/>.
///
/// Note: like every spec that starts a session, this minimizes off-list windows on
/// the live desktop while it runs.
/// </summary>
[Trait("Category", "Visual")]
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class NotRespondingAppTests
{
    // Long enough to outlast the spec's checks; the spec ends it itself once it
    // has looked.
    private static readonly TimeSpan NotRespondingFor = TimeSpan.FromSeconds(10);

    private readonly BackendFixture _backend;
    public NotRespondingAppTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task WhileABlockedAppIsNotResponding_TheAgentStillBlocksTheNextOffListApp()
    {
        var api = new BackendClient(_backend.Url);
        await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);
        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

        // No bundles: both apps are off-list.
        var classId = await api.FindClassIdAsync();
        var session = await api.StartSessionAsync(classId);
        try
        {
            var joined = await agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id && s.StartupSweep is not null, TimeSpan.FromSeconds(10));
            Assert.True(
                joined?.JoinedSessionId == session.Id && joined.StartupSweep is not null,
                $"Agent did not join and sweep within 10s (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");

            using var hung = await OffListWindow.OpenAsync(
                "Anchor e2e off-list app (stops responding)", TimeSpan.FromSeconds(10), minimized: true);
            using var next = await OffListWindow.OpenAsync(
                "Anchor e2e off-list app", TimeSpan.FromSeconds(10), minimized: true);

            // The student brings an off-list app back. The agent blocks it, and the
            // app stops responding just as it is taken down.
            hung.HangWhenMinimized(NotRespondingFor);
            hung.Restore();
            Assert.True(
                hung.WaitUntilFrozen(TimeSpan.FromSeconds(3)),
                "The agent did not block the off-list app (it never tried to minimize it).");

            // Meanwhile they open another off-list app, which the agent must block
            // as always.
            next.Restore();
            var nextDown = await WaitUntilAsync(() => next.HasGoneDown, TimeSpan.FromSeconds(2));
            Assert.True(hung.IsFrozen, "The off-list app started responding again before the spec could check the agent.");
            Assert.True(
                nextDown,
                "While a blocked off-list app was not responding, the agent did not block the next off-list app: " +
                "it was waiting for the app that does not respond (#440).");

            hung.Thaw();
            Assert.True(
                await WaitUntilAsync(() => hung.HasGoneDown, TimeSpan.FromSeconds(3)),
                "The off-list app that was not responding did not go down once it responded again.");
        }
        finally
        {
            try { await api.EndSessionAsync(session.Id); } catch { /* best-effort */ }
        }
    }

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
}
