namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end coverage for the #102 agent-UI actions, driven through the status
/// endpoint's POST controls (the headless stand-ins for the buttons):
///
///   * "Leave session" must emit a ManualLeave event the teacher can review,
///     end the session locally, and leave the agent itself running — the whole
///     point of distinguishing it from "Quit".
///   * "Close" must hide the window without ending the session or stopping the
///     agent.
///
/// The agent auto-joins so it's a real active participant (the backend rejects
/// a ManualLeave from anyone who isn't), and we assert against the real backend
/// over the real hub — a wire path a unit test can't see.
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class ManualLeaveTests
{
    private readonly BackendFixture _backend;
    public ManualLeaveTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task LeavingASession_EmitsManualLeave_EndsLocally_AndKeepsTheAgentRunning()
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

        await agent.LeaveSessionAsync();

        // Ended locally: the coordinator cleared both session ids, so enforcement
        // and the heartbeat stop and join-by-code is available again.
        var cleared = await agent.WaitForAsync(
            s => s.ActiveSessionId is null && s.JoinedSessionId is null, TimeSpan.FromSeconds(5));
        Assert.True(
            cleared is { ActiveSessionId: null, JoinedSessionId: null },
            $"Agent did not clear session state within 5s of leaving " +
            $"(activeSessionId: {cleared?.ActiveSessionId?.ToString() ?? "<none>"}, " +
            $"joinedSessionId: {cleared?.JoinedSessionId?.ToString() ?? "<none>"}).");

        // Leave is not Quit: the agent is still reachable and Connected.
        Assert.Equal("Connected", cleared!.ConnectionStatus);

        // The manual_leave event reached the backend over the hub. It's reported
        // before LeaveSession sets LeftAt, so the backend accepts it — if the
        // order were wrong the event would be rejected and never appear here.
        var kinds = await PollForEventAsync(api, session.Id, "ManualLeave", TimeSpan.FromSeconds(5));
        Assert.Contains("ManualLeave", kinds);
    }

    [Fact]
    public async Task ClosingTheWindow_KeepsTheAgentRunningAndStillInSession()
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

        await agent.CloseWindowAsync();

        // Closing only hides the window: after a beat the agent is still
        // reachable (process alive), still Connected, and still in the session.
        await Task.Delay(TimeSpan.FromSeconds(1));
        var after = await agent.TryGetStatusAsync();
        Assert.True(after is not null, "Agent became unreachable after Close — it should keep running in the tray.");
        Assert.Equal("Connected", after!.ConnectionStatus);
        Assert.Equal(session.Id, after.JoinedSessionId);
    }

    /// <summary>
    /// Re-read the session's recent-event kinds until <paramref name="kind"/>
    /// shows up or the timeout elapses. The POST /leave call only returns once
    /// the backend has stored the event, so this is just slack for read lag.
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
