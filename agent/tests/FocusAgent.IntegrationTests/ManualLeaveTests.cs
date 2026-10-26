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
        try
        {
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
        finally
        {
            // Never leave the session running for the specs that follow.
            await api.EndSessionAsync(session.Id);
        }
    }

    /// <summary>
    /// #448: a leave must reach the teacher as a leave and nothing more. The
    /// agent stops pinging once it has left, and the backend used to go on
    /// tracking its last ping, which aged into a HeartbeatLost some seconds
    /// later, as if the student's laptop had dropped out. The backend runs with
    /// a 4 s heartbeat timeout and a 1 s scan (see <see cref="BackendProcess"/>)
    /// and this agent pings every second, so a few seconds past the timeout the
    /// monitor would have reported it.
    /// </summary>
    [Fact]
    public async Task LeavingASession_RaisesNoHeartbeatLostForTheTeacher()
    {
        var api = new BackendClient(_backend.Url);
        await using var agent = AgentProcess.Launch(
            _backend.Url, TestConfig.StudentOid, autoJoin: true, heartbeatIntervalSeconds: 1);

        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

        var classId = await api.FindClassIdAsync();
        var session = await api.StartSessionAsync(classId);
        try
        {
            var joined = await agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
            Assert.True(
                joined?.JoinedSessionId == session.Id,
                $"Agent did not auto-join within 8s (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");

            // A few pings first, so the backend is tracking the agent when it leaves.
            await Task.Delay(TimeSpan.FromSeconds(3));

            await agent.LeaveSessionAsync();
            var cleared = await agent.WaitForAsync(
                s => s.JoinedSessionId is null, TimeSpan.FromSeconds(5));
            Assert.True(
                cleared is { JoinedSessionId: null },
                $"Agent did not clear its joined session within 5s of leaving " +
                $"(joinedSessionId: {cleared?.JoinedSessionId?.ToString() ?? "<none>"}).");

            // Past the 4 s timeout and the next 1 s scan, with room to spare.
            await Task.Delay(TimeSpan.FromSeconds(8));

            // What the teacher's session page lists: the leave, and no lost agent.
            var kinds = await api.GetSessionEventKindsAsync(session.Id);
            Assert.Contains("ManualLeave", kinds);
            Assert.DoesNotContain("HeartbeatLost", kinds);
        }
        finally
        {
            // Never leave the session running for the specs that follow.
            await api.EndSessionAsync(session.Id);
        }
    }

    [Fact]
    public async Task ClosingTheWindow_KeepsTheAgentRunningAndStillInSession()
    {
        var api = new BackendClient(_backend.Url);
        await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);

        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

        var classId = await api.FindClassIdAsync();
        var session = await api.StartSessionAsync(classId);
        try
        {
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
        finally
        {
            // The student is still in this session when the agent is disposed, so
            // left running it would be rejoined by the next spec's agent (#54) —
            // whose start-of-session sweep then minimizes windows that spec allows.
            await api.EndSessionAsync(session.Id);
        }
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
