namespace FocusAgent.IntegrationTests;

/// <summary>
/// #354: an agent whose hub connection came back mid-session must still leave
/// the session when it ends. SignalR keeps no group membership across a
/// reconnect, and SessionEnded used to go only to the session group, which the
/// agent joins once, in JoinSession. After a network drop, a backend restart or
/// a laptop waking from sleep the agent stayed in focus mode once the session
/// ended, until the student left by hand or the agent restarted.
///
/// Two cases, for the teacher's End and for the automatic end of a forgotten
/// session (#345), which both go through the backend's SessionEnder:
/// <list type="bullet">
///   <item>the session ends after the agent reconnected — SessionEnded must
///   reach the new connection;</item>
///   <item>the session ends while the agent is cut off, so no broadcast can
///   reach it — the agent must find out once it is back.</item>
/// </list>
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class SessionEndAfterReconnectTests
{
    /// <summary>
    /// Session limit for the auto-end specs. It leaves room to join and, in
    /// the reconnect case, to drop and restore the connection before the
    /// backend ends the session, on a slow runner.
    /// </summary>
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(20);

    /// <summary>How long a reconnected agent may take to leave an ended session.</summary>
    private static readonly TimeSpan LeaveWithin = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(2);

    private readonly BackendFixture _backend;
    private readonly List<Guid> _started = new();
    public SessionEndAfterReconnectTests(BackendFixture backend) => _backend = backend;

    /// <summary>The issue's reproduction: restart the backend, then end the session.</summary>
    [Fact]
    public async Task AnAgentThatReconnected_LeavesTheSession_WhenTheTeacherEndsIt()
    {
        var api = new BackendClient(_backend.Url);
        await using var agent = AgentProcess.Launch(
            _backend.Url, TestConfig.StudentOid, autoJoin: true, reconnectMaxBackoff: ReconnectBackoff);
        try
        {
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
            var sessionId = await StartAndJoinAsync(api, agent);

            // --- reconnect: a deploy or an App Service restart ----------------
            await _backend.StopAsync();
            await WaitForDisconnectedAsync(agent);
            await _backend.RestartAsync();
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(sessionId, (await agent.TryGetStatusAsync())?.JoinedSessionId);
            // The teacher ends it a moment later, not in the same instant the
            // agent reconnects (see #356 for that window).
            await Task.Delay(TimeSpan.FromSeconds(1));

            // --- the teacher ends the session ---------------------------------
            await api.EndSessionAsync(sessionId);
            await AssertLeftAsync(agent, sessionId, "after the teacher ended it");
        }
        finally
        {
            // Never leave the shared backend down for the specs that follow.
            if (!_backend.IsRunning)
                await _backend.RestartAsync();
            await EndStartedSessionsAsync(api);
        }
    }

    /// <summary>
    /// The teacher ends the session while the agent's network is down, so the
    /// SessionEnded broadcast has no connection to reach; the agent must find
    /// out when its network comes back.
    /// </summary>
    [Fact]
    public async Task AnAgentCutOffWhenTheTeacherEndsTheSession_LeavesItOnceItIsBack()
    {
        var api = new BackendClient(_backend.Url);
        await using var network = new NetworkRelay(_backend.Url);
        await using var agent = AgentProcess.Launch(
            network.Url, TestConfig.StudentOid, autoJoin: true, reconnectMaxBackoff: ReconnectBackoff);
        try
        {
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
            var sessionId = await StartAndJoinAsync(api, agent);

            network.Cut();
            await WaitForDisconnectedAsync(agent);

            await api.EndSessionAsync(sessionId);
            await AssertStillJoinedWhileCutOffAsync(agent, sessionId);

            network.Restore();
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
            await AssertLeftAsync(agent, sessionId, "after it came back from a network drop during which the session ended");
        }
        finally
        {
            await EndStartedSessionsAsync(api);
        }
    }

    /// <summary>The backend ends a forgotten session after the agent reconnected.</summary>
    [Fact]
    public async Task AnAgentThatReconnected_LeavesAForgottenSession_WhenItEndsOnItsOwn()
    {
        var api = new BackendClient(_backend.Url);
        await WithShortSessionLimitAsync(api, async () =>
        {
            await using var network = new NetworkRelay(_backend.Url);
            await using var agent = AgentProcess.Launch(
                network.Url, TestConfig.StudentOid, autoJoin: true, reconnectMaxBackoff: ReconnectBackoff);
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
            var sessionId = await StartAndJoinAsync(api, agent);

            // --- a network blip --------------------------------------------
            network.Cut();
            await WaitForDisconnectedAsync(agent);
            network.Restore();
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(sessionId, (await agent.TryGetStatusAsync())?.JoinedSessionId);
            Assert.True(
                await api.GetSessionEndedAtAsync(sessionId) is null,
                $"The session ended before the agent reconnected; raise MaxDuration ({MaxDuration.TotalSeconds:N0}s).");

            // --- the teacher never ends it: the backend does ------------------
            await AssertLeftAsync(
                agent, sessionId, "after the backend ended it", MaxDuration + LeaveWithin);
            Assert.NotNull(await api.GetSessionEndedAtAsync(sessionId));
        });
    }

    /// <summary>
    /// The case the issue singles out: a forgotten session is ended hours after
    /// the lesson, while the laptop is asleep; when it wakes, its agent
    /// reconnects to a session that is already over.
    /// </summary>
    [Fact]
    public async Task AnAgentCutOffWhenAForgottenSessionEnds_LeavesItOnceItIsBack()
    {
        var api = new BackendClient(_backend.Url);
        await WithShortSessionLimitAsync(api, async () =>
        {
            await using var network = new NetworkRelay(_backend.Url);
            await using var agent = AgentProcess.Launch(
                network.Url, TestConfig.StudentOid, autoJoin: true, reconnectMaxBackoff: ReconnectBackoff);
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
            var sessionId = await StartAndJoinAsync(api, agent);

            // --- asleep through the automatic end ----------------------------
            network.Cut();
            await WaitForDisconnectedAsync(agent);
            var endedAt = await PollAsync(
                () => api.GetSessionEndedAtAsync(sessionId), MaxDuration + TimeSpan.FromSeconds(15));
            Assert.True(endedAt is not null, "The backend did not end the forgotten session.");
            await AssertStillJoinedWhileCutOffAsync(agent, sessionId);

            // --- the laptop wakes up -------------------------------------------
            network.Restore();
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
            await AssertLeftAsync(agent, sessionId, "after it came back from a network drop during which the session ended");
        });
    }

    private async Task<Guid> StartAndJoinAsync(BackendClient api, AgentProcess agent)
    {
        var classId = await api.FindClassIdAsync();
        var session = await api.StartSessionAsync(classId);
        _started.Add(session.Id);
        var joined = await agent.WaitForAsync(
            s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
        Assert.True(
            joined?.JoinedSessionId == session.Id,
            $"Agent did not auto-join within 8s (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");
        return session.Id;
    }

    private static async Task WaitForDisconnectedAsync(AgentProcess agent)
    {
        var dropped = await agent.WaitForAsync(
            s => s.ConnectionStatus != "Connected", TimeSpan.FromSeconds(10));
        Assert.True(
            dropped is not null && dropped.ConnectionStatus != "Connected",
            "Agent still reported Connected 10s after losing its connection.");
    }

    /// <summary>
    /// While it is cut off the agent can't know the session ended. Asserting it
    /// proves the spec drives the reconnect path, not a broadcast that got
    /// through before the cut.
    /// </summary>
    private static async Task AssertStillJoinedWhileCutOffAsync(AgentProcess agent, Guid sessionId)
    {
        var status = await agent.TryGetStatusAsync();
        Assert.NotNull(status);
        Assert.NotEqual("Connected", status.ConnectionStatus);
        Assert.Equal(sessionId, status.JoinedSessionId);
    }

    private static async Task AssertLeftAsync(
        AgentProcess agent, Guid sessionId, string when, TimeSpan? within = null)
    {
        var timeout = within ?? LeaveWithin;
        var cleared = await agent.WaitForAsync(
            s => s.ActiveSessionId is null && s.JoinedSessionId is null, timeout);
        Assert.True(
            cleared is { ActiveSessionId: null, JoinedSessionId: null },
            $"Agent was still in session {sessionId} {timeout.TotalSeconds:N0}s {when} " +
            $"(activeSessionId: {cleared?.ActiveSessionId?.ToString() ?? "<none>"}, " +
            $"joinedSessionId: {cleared?.JoinedSessionId?.ToString() ?? "<none>"}).");
    }

    /// <summary>
    /// Runs <paramref name="body"/> against a backend that ends sessions
    /// <see cref="MaxDuration"/> after they start, then puts the shared backend
    /// back on its defaults so the specs that follow aren't ended under them.
    /// </summary>
    private async Task WithShortSessionLimitAsync(BackendClient api, Func<Task> body)
    {
        await _backend.StopAsync();
        await _backend.RestartAsync(new Dictionary<string, string>
        {
            ["SessionAutoEnd__MaxDuration"] = MaxDuration.ToString("c"),
            ["SessionAutoEnd__SweepInterval"] = "00:00:01",
        });
        try
        {
            await body();
        }
        finally
        {
            await EndStartedSessionsAsync(api);
            await _backend.StopAsync();
            await _backend.RestartAsync();
        }
    }

    /// <summary>
    /// Ends whatever a failed spec left running (ending an ended session is a
    /// no-op), so a later spec's agent can't rejoin it through rehydration.
    /// </summary>
    private async Task EndStartedSessionsAsync(BackendClient api)
    {
        foreach (var id in _started)
            await api.EndSessionAsync(id);
    }

    private static async Task<T?> PollAsync<T>(Func<Task<T?>> read, TimeSpan timeout)
        where T : struct
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await read() is { } value)
                return value;
            await Task.Delay(500);
        }
        return null;
    }
}
