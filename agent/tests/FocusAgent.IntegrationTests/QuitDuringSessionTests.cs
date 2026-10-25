namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end coverage for #445: while the agent has a session going on, the tray
/// offers no "Exit Anchor", so "Leave session" in the main window (which asks first
/// and shows on the teacher's roster) is the way out. Driven through the status
/// endpoint's POST /quit, the headless stand-in for the tray's Exit, which follows
/// the same rule: refused (409) while a session is going on, and the agent keeps
/// running and stays in it. Asserted against the real backend over the real hub, in
/// every state that counts as a session going on:
///
///   * joined;
///   * being asked to join (the join confirmation is up);
///   * rejoined after a restart (#54), the classroom case from the issue.
///
/// Outside a session Exit works as before: <see cref="AgentKilledTests"/> quits
/// after leaving, and the asked-to-join spec quits once the teacher has ended it.
/// The tray menu's own rendering of the rule is <see cref="TrayMenuExitTests"/>.
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class QuitDuringSessionTests
{
    // How long a refused agent must stay up. An exit that got through would be
    // within ShutdownCleanly's half-second report window plus process teardown.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    private readonly BackendFixture _backend;
    public QuitDuringSessionTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task While_joined_quitting_is_refused_and_the_agent_stays_in_the_session()
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

            await AssertQuitRefusedAsync(agent, session.Id, s => s.JoinedSessionId == session.Id, "joined");

            // Nothing reached the teacher as a departure.
            var kinds = await api.GetSessionEventKindsAsync(session.Id);
            Assert.DoesNotContain("AgentKilled", kinds);
        }
        finally
        {
            await api.EndSessionAsync(session.Id);
        }
    }

    [Fact]
    public async Task While_the_student_is_asked_to_join_quitting_is_refused_and_works_again_once_the_session_ended()
    {
        var api = new BackendClient(_backend.Url);
        // No auto-join: the real join confirmation shows. Its countdown is
        // stretched so it is still up when the quit arrives (it joins at the end).
        await using var agent = AgentProcess.Launch(
            _backend.Url,
            TestConfig.StudentOid,
            extraEnvironment: new Dictionary<string, string> { ["Realtime__JoinConfirmationDuration"] = "00:01:00" });
        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

        var classId = await api.FindClassIdAsync();
        var session = await api.StartSessionAsync(classId);
        var ended = false;
        try
        {
            var asking = await agent.WaitForAsync(
                s => s.ActiveSessionId == session.Id, TimeSpan.FromSeconds(8));
            Assert.True(
                asking is not null && asking.ActiveSessionId == session.Id && asking.JoinedSessionId is null,
                $"Agent was not asking the student to join within 8s (activeSessionId: " +
                $"{asking?.ActiveSessionId?.ToString() ?? "<none>"}, joinedSessionId: " +
                $"{asking?.JoinedSessionId?.ToString() ?? "<none>"}).");

            await AssertQuitRefusedAsync(
                agent, session.Id, s => s.ActiveSessionId == session.Id && s.JoinedSessionId is null, "asked to join");

            // The teacher ends it: no session going on, so Exit works as before.
            await api.EndSessionAsync(session.Id);
            ended = true;
            var cleared = await agent.WaitForAsync(
                s => s.ActiveSessionId is null && s.JoinedSessionId is null, TimeSpan.FromSeconds(8));
            Assert.True(
                cleared is { ActiveSessionId: null, JoinedSessionId: null },
                $"Agent still had the session 8s after it ended (activeSessionId: " +
                $"{cleared?.ActiveSessionId?.ToString() ?? "<none>"}).");

            Assert.True(await agent.TryQuitAsync(), "Quit was refused after the session ended.");
            await agent.WaitForExitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!ended) await api.EndSessionAsync(session.Id);
        }
    }

    [Fact]
    public async Task After_a_restart_a_rejoined_session_still_refuses_quitting()
    {
        var api = new BackendClient(_backend.Url);
        var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);
        var classId = await api.FindClassIdAsync();
        Guid? sessionId = null;
        try
        {
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
            var session = await api.StartSessionAsync(classId);
            sessionId = session.Id;
            var joined = await agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
            Assert.True(
                joined?.JoinedSessionId == session.Id,
                $"Agent did not auto-join within 8s (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");

            // Ended from Task Manager (a hard kill): the student is still a joined
            // participant, so the restarted agent rejoins without asking (#54). No
            // auto-join and a long join confirmation this time, so within the wait
            // below only that silent rejoin can put it back in the session.
            await agent.DisposeAsync();
            agent = await AgentProcess.LaunchAfterKillAsync(
                () => AgentProcess.Launch(
                    _backend.Url,
                    TestConfig.StudentOid,
                    extraEnvironment: new Dictionary<string, string> { ["Realtime__JoinConfirmationDuration"] = "00:01:00" }),
                TimeSpan.FromSeconds(15));
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
            var rejoined = await agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
            Assert.True(
                rejoined?.JoinedSessionId == session.Id,
                $"Restarted agent did not rejoin the session within 8s " +
                $"(joinedSessionId: {rejoined?.JoinedSessionId?.ToString() ?? "<none>"}).");

            await AssertQuitRefusedAsync(agent, session.Id, s => s.JoinedSessionId == session.Id, "rejoined");
        }
        finally
        {
            // End the session before the agent goes, as the other specs do, so the
            // next spec's agent doesn't start right after one killed in a session.
            try
            {
                if (sessionId is Guid id) await api.EndSessionAsync(id);
            }
            finally
            {
                await agent.DisposeAsync();
            }
        }
    }

    // Quit is refused, the agent is still running a moment later, and it is still
    // in the state the spec put it in.
    private static async Task AssertQuitRefusedAsync(
        AgentProcess agent, Guid sessionId, Func<StatusSnapshot, bool> stillInState, string state)
    {
        Assert.False(await agent.TryQuitAsync(), $"Quit was accepted while {state} in session {sessionId}.");

        await Task.Delay(SettleTime);
        Assert.False(agent.HasExited, $"The agent exited after a refused quit while {state}.");
        var after = await agent.TryGetStatusAsync();
        Assert.True(
            after is not null && stillInState(after),
            $"The agent left the {state} state after a refused quit (activeSessionId: " +
            $"{after?.ActiveSessionId?.ToString() ?? "<none>"}, joinedSessionId: " +
            $"{after?.JoinedSessionId?.ToString() ?? "<none>"}).");
    }
}
