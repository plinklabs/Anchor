namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end coverage for the #110 quit-during-session signal, driven through
/// the status endpoint's POST /quit control (the headless stand-in for tray →
/// Quit):
///
///   * Quitting while joined must emit exactly one AgentKilled event the teacher
///     can see immediately — the whole point, vs. waiting out the HeartbeatLost
///     timeout. A hard kill (the crash case) emits nothing; only a clean quit
///     does, which is what /quit exercises.
///   * Quitting after having left the session must emit nothing — the agent only
///     knows to report a deliberate departure while it's actually in a session.
///
/// The agent auto-joins so it's a real active participant (the backend rejects an
/// AgentKilled from anyone who isn't), and we assert against the real backend over
/// the real hub — a wire path a unit test can't see.
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class AgentKilledTests
{
    private readonly BackendFixture _backend;
    public AgentKilledTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task QuittingDuringASession_EmitsExactlyOneAgentKilled()
    {
        var api = new BackendClient(_backend.Url);
        await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);

        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

        var classId = await api.FindClassIdAsync();
        var session = await api.StartSessionAsync(classId);

        var joined = await agent.WaitForAsync(
            s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
        Assert.True(
            joined?.JoinedSessionId == session.Id,
            $"Agent did not auto-join within 8s (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");

        await agent.QuitAsync();

        // Quit is a clean shutdown: the process actually exits (vs. Close, which
        // just hides the window).
        await agent.WaitForExitAsync(TimeSpan.FromSeconds(10));

        // The AgentKilled event reached the backend over the hub before exit —
        // the agent awaits the server ack inside its bounded best-effort window,
        // so by the time it exits the event is already persisted. Exactly one:
        // the agent reports it a single time on quit.
        var kinds = await PollForEventAsync(api, session.Id, "AgentKilled", TimeSpan.FromSeconds(5));
        Assert.Equal(1, kinds.Count(k => k == "AgentKilled"));
    }

    [Fact]
    public async Task QuittingAfterLeaving_EmitsNoAgentKilled()
    {
        var api = new BackendClient(_backend.Url);
        await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);

        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

        var classId = await api.FindClassIdAsync();
        var session = await api.StartSessionAsync(classId);

        var joined = await agent.WaitForAsync(
            s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
        Assert.True(
            joined?.JoinedSessionId == session.Id,
            $"Agent did not auto-join within 8s (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");

        // Leave first — the agent is no longer in a session, so a subsequent quit
        // has nothing deliberate to report.
        await agent.LeaveSessionAsync();
        var cleared = await agent.WaitForAsync(
            s => s.JoinedSessionId is null, TimeSpan.FromSeconds(5));
        Assert.True(
            cleared is { JoinedSessionId: null },
            $"Agent did not clear its joined session within 5s of leaving " +
            $"(joinedSessionId: {cleared?.JoinedSessionId?.ToString() ?? "<none>"}).");

        await agent.QuitAsync();
        await agent.WaitForExitAsync(TimeSpan.FromSeconds(10));

        // ManualLeave is on record from the leave above; AgentKilled must not be —
        // quit outside a session emits nothing. The leave already round-tripped,
        // so its presence confirms we're reading the right session's events.
        var kinds = await api.GetSessionEventKindsAsync(session.Id);
        Assert.Contains("ManualLeave", kinds);
        Assert.DoesNotContain("AgentKilled", kinds);
    }

    /// <summary>
    /// Re-read the session's recent-event kinds until <paramref name="kind"/>
    /// shows up or the timeout elapses. The agent awaits the server ack before
    /// exiting, so this is just slack for the teacher-side read lag.
    /// </summary>
    private static async Task<List<string>> PollForEventAsync(
        BackendClient api, Guid sessionId, string kind, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        List<string> kinds;
        do
        {
            kinds = await api.GetSessionEventKindsAsync(sessionId);
            if (kinds.Contains(kind)) return kinds;
            await Task.Delay(200);
        } while (DateTime.UtcNow < deadline);
        return kinds;
    }
}
