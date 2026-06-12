namespace FocusAgent.IntegrationTests;

/// <summary>
/// Session end → state cleared (the lifecycle's closing half, listed under
/// Phase 1 of #131). The agent auto-joins so both activeSessionId and
/// joinedSessionId are populated; when the teacher ends the session the backend
/// pushes SessionEnded and the agent's coordinator must clear both.
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class SessionEndTests
{
    private readonly BackendFixture _backend;
    public SessionEndTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task EndingASession_ClearsTheAgentsSessionState()
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

        await api.EndSessionAsync(session.Id);

        var cleared = await agent.WaitForAsync(
            s => s.ActiveSessionId is null && s.JoinedSessionId is null, TimeSpan.FromSeconds(5));
        Assert.True(
            cleared is { ActiveSessionId: null, JoinedSessionId: null },
            $"Agent did not clear session state within 5s of end " +
            $"(activeSessionId: {cleared?.ActiveSessionId?.ToString() ?? "<none>"}, " +
            $"joinedSessionId: {cleared?.JoinedSessionId?.ToString() ?? "<none>"}).");
    }
}
