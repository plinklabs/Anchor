namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end coverage for the #110 quit signal, driven through the status
/// endpoint's POST /quit control (the headless stand-in for tray → Exit Anchor):
/// quitting after having left the session must emit nothing — the agent only
/// reports a deliberate departure (an AgentKilled event) while it's actually in a
/// session.
///
/// Quitting while still in the session used to emit that AgentKilled. Since #445 the
/// tray offers no Exit while a session is going on, and /quit is refused then too;
/// <see cref="QuitDuringSessionTests"/> covers that, including that no AgentKilled
/// reaches the teacher.
///
/// The agent auto-joins so it's a real active participant, and we assert against the
/// real backend over the real hub — a wire path a unit test can't see.
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class AgentKilledTests
{
    private readonly BackendFixture _backend;
    public AgentKilledTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task QuittingAfterLeaving_EmitsNoAgentKilled()
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
        finally
        {
            // Never leave the session running for the specs that follow.
            await api.EndSessionAsync(session.Id);
        }
    }
}
