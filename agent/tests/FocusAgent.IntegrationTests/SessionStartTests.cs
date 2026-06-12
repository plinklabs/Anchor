namespace FocusAgent.IntegrationTests;

/// <summary>
/// Port of <c>scripts/dev/verify-session-start.ps1</c> as an asserting test:
/// a teacher POSTs /sessions and the real agent's coordinator must surface
/// SessionStarted (activeSessionId flips non-null) — the chain #41 originally
/// broke.
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
}
