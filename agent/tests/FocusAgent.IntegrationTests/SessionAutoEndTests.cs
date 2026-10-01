namespace FocusAgent.IntegrationTests;

/// <summary>
/// Forgotten sessions end on their own (#345). A teacher who closes the
/// dashboard without clicking End used to leave the session running for good,
/// and every student whose agent restarted later — next lesson, next day — was
/// silently put back into its focus mode by rehydration (#54). The backend now
/// ends a session that is still running a set time after it started, through
/// the same path as the teacher's End.
///
/// The shared backend runs with the production limit (four hours), so this spec
/// restarts it with a limit of <see cref="MaxDuration"/> and a one-second sweep,
/// and restarts it with the defaults again afterwards. The limit leaves room for
/// the control step — kill, relaunch and rehydrate the agent — on a slow runner.
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class SessionAutoEndTests
{
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(45);

    private readonly BackendFixture _backend;
    public SessionAutoEndTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task AForgottenSession_EndsOnItsOwn_AndARestartedAgentDoesNotRejoinIt()
    {
        var api = new BackendClient(_backend.Url);
        await _backend.StopAsync();
        await _backend.RestartAsync(new Dictionary<string, string>
        {
            ["SessionAutoEnd__MaxDuration"] = MaxDuration.ToString("c"),
            ["SessionAutoEnd__SweepInterval"] = "00:00:01",
        });

        AgentProcess? agent = null;
        try
        {
            agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

            var classId = await api.FindClassIdAsync();
            var session = await api.StartSessionAsync(classId);
            var joined = await agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
            Assert.True(
                joined?.JoinedSessionId == session.Id,
                $"Agent did not auto-join within 8s (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");

            // --- control: while the session runs, a restarted agent rejoins it --
            // A hard kill is a crash or a shut laptop: no LeaveSession, so the
            // student is still a joined participant, which is what rehydration
            // acts on. This proves the negative at the end isn't vacuous.
            await agent.DisposeAsync();
            agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
            var rejoined = await agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
            Assert.True(
                rejoined?.JoinedSessionId == session.Id,
                $"Restarted agent did not rejoin the running session within 8s " +
                $"(joinedSessionId: {rejoined?.JoinedSessionId?.ToString() ?? "<none>"}). " +
                $"If the session had already passed its {MaxDuration.TotalSeconds:N0}s limit, raise MaxDuration.");
            Assert.Null(await api.GetSessionEndedAtAsync(session.Id));

            // --- the teacher never ends it: the backend does ------------------
            // The joined agent is told, exactly as for the teacher's End.
            var cleared = await agent.WaitForAsync(
                s => s.ActiveSessionId is null && s.JoinedSessionId is null,
                MaxDuration + TimeSpan.FromSeconds(15));
            Assert.True(
                cleared is { ActiveSessionId: null, JoinedSessionId: null },
                $"Agent was still in the session {MaxDuration.TotalSeconds + 15:N0}s after it started " +
                $"(activeSessionId: {cleared?.ActiveSessionId?.ToString() ?? "<none>"}, " +
                $"joinedSessionId: {cleared?.JoinedSessionId?.ToString() ?? "<none>"}).");
            Assert.NotNull(await api.GetSessionEndedAtAsync(session.Id));

            // --- next lesson: the restarted agent stays out of it -------------
            await agent.DisposeAsync();
            agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
            // Rehydration runs on the first Connected; the control above shows it
            // rejoins within a second or two, so this window is generous.
            var back = await agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
            Assert.True(
                back?.JoinedSessionId != session.Id,
                "Restarted agent rejoined the session after it was ended automatically.");
        }
        finally
        {
            if (agent is not null)
                await agent.DisposeAsync();
            // Never leave the shared backend on the short limit for the specs
            // that follow: their sessions would end under them.
            await _backend.StopAsync();
            await _backend.RestartAsync();
        }
    }
}
