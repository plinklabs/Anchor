namespace FocusAgent.IntegrationTests;

/// <summary>
/// Port of <c>scripts/dev/verify-session-start.ps1</c> as an asserting test:
/// a teacher POSTs /sessions and the real agent's coordinator must surface
/// SessionStarted (activeSessionId flips non-null) — the chain #41 originally
/// broke.
///
/// #356: SessionStarted goes to the student's user group and reaches only the
/// connections in it when it is sent. The first spec starts the session as
/// soon as the agent reports Connected, which can be before the backend has
/// added the connection to that group; the second starts it while the agent is
/// cut off. The agent has to find the session by asking once it is connected.
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class SessionStartTests
{
    private readonly BackendFixture _backend;
    public SessionStartTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task TeacherStartingASession_ReachesTheAgentAsActiveSession()
    {
        var api = new BackendClient(_backend.Url);
        await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid);

        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

        var classId = await api.FindClassIdAsync();
        var session = await api.StartSessionAsync(classId);
        try
        {
            var status = await agent.WaitForAsync(
                s => s.ActiveSessionId == session.Id, TimeSpan.FromSeconds(5));

            Assert.True(
                status?.ActiveSessionId == session.Id,
                $"Agent did not see SessionStarted within 5s. " +
                $"Last activeSessionId: {status?.ActiveSessionId?.ToString() ?? "<none>"}.");
        }
        finally
        {
            await api.EndSessionAsync(session.Id);
        }
    }

    [Fact]
    public async Task AnAgentCutOffWhenTheTeacherStartsASession_SeesItOnceItIsBack()
    {
        var api = new BackendClient(_backend.Url);
        await using var network = new NetworkRelay(_backend.Url);
        await using var agent = AgentProcess.Launch(
            network.Url, TestConfig.StudentOid, reconnectMaxBackoff: TimeSpan.FromSeconds(2));
        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

        network.Cut();
        var dropped = await agent.WaitForAsync(
            s => s.ConnectionStatus != "Connected", TimeSpan.FromSeconds(10));
        Assert.True(
            dropped is not null && dropped.ConnectionStatus != "Connected",
            "Agent still reported Connected 10s after losing its connection.");

        var classId = await api.FindClassIdAsync();
        var session = await api.StartSessionAsync(classId);
        try
        {
            // The broadcast had no connection to reach.
            var cutOff = await agent.TryGetStatusAsync();
            Assert.NotNull(cutOff);
            Assert.NotEqual("Connected", cutOff.ConnectionStatus);
            Assert.NotEqual(session.Id, cutOff.ActiveSessionId);

            network.Restore();
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
            var status = await agent.WaitForAsync(
                s => s.ActiveSessionId == session.Id, TimeSpan.FromSeconds(10));

            Assert.True(
                status?.ActiveSessionId == session.Id,
                $"Agent did not pick up the session within 10s of reconnecting. " +
                $"Last activeSessionId: {status?.ActiveSessionId?.ToString() ?? "<none>"}.");
        }
        finally
        {
            await api.EndSessionAsync(session.Id);
        }
    }
}
