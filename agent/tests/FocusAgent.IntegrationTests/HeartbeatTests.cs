using System.Diagnostics;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Heartbeat liveness, exercising the <em>agent's</em> own
/// <c>SessionHeartbeatService</c> end-to-end (the Phase-1 analog of
/// <c>scripts/dev/verify-heartbeat.ps1</c>, which used a standalone SignalR
/// client). The real agent auto-joins and pings; while it runs the backend must
/// see it as live (no HeartbeatLost), and once it's killed the backend's
/// HeartbeatMonitor must record exactly that loss.
///
/// The backend is booted with a fast heartbeat config (timeout 4s, scan 1s — see
/// <see cref="BackendProcess"/>) and the agent pings every 1s, so the whole flow
/// resolves in seconds.
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class HeartbeatTests
{
    private const string HeartbeatLost = "HeartbeatLost";

    private readonly BackendFixture _backend;
    public HeartbeatTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task AgentKeepsSessionAlive_AndLossIsDetectedWhenItStops()
    {
        var api = new BackendClient(_backend.Url);
        var agent = AgentProcess.Launch(
            _backend.Url, TestConfig.StudentOid, autoJoin: true, heartbeatIntervalSeconds: 1);

        Guid sessionId = Guid.Empty;
        try
        {
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

            var classId = await api.FindClassIdAsync();
            var session = await api.StartSessionAsync(classId);
            sessionId = session.Id;

            var joined = await agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
            Assert.True(
                joined?.JoinedSessionId == session.Id,
                $"Agent did not auto-join within 8s (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");

            // --- alive: ping cadence (1s) keeps it under the 4s timeout ------
            // Wait past one full timeout window, then assert the backend has NOT
            // declared the agent lost.
            await Task.Delay(TimeSpan.FromSeconds(6));
            var whileAlive = await api.GetSessionEventKindsAsync(session.Id);
            Assert.False(
                whileAlive.Contains(HeartbeatLost),
                $"Backend reported HeartbeatLost while the agent was running and pinging. Events: {Join(whileAlive)}.");

            // --- gone: kill the agent, the monitor must emit HeartbeatLost ---
            agent.Kill();
            var lost = await PollForEventAsync(api, session.Id, HeartbeatLost, TimeSpan.FromSeconds(12));
            Assert.True(
                lost,
                "Backend did not record HeartbeatLost within 12s of the agent being killed.");
        }
        finally
        {
            await agent.DisposeAsync();
            if (sessionId != Guid.Empty)
                await api.EndSessionAsync(sessionId);
        }
    }

    /// <summary>
    /// #342: the backend validates heartbeats from memory, and a restart (a
    /// deploy, an App Service recycle) empties that memory while the session and
    /// the student's participant row live on in the database. The agent
    /// reconnects and keeps pinging <em>without</em> rejoining — its rehydration
    /// runs once per agent process — so the restarted backend must accept those
    /// pings by falling back to the database. If it rejected them, nothing would
    /// track the agent any more and the teacher would never see it go missing:
    /// the kill at the end would raise no HeartbeatLost.
    /// </summary>
    [Fact]
    public async Task AgentHeartbeats_StillCount_AfterTheBackendRestarts()
    {
        var api = new BackendClient(_backend.Url);
        var agent = AgentProcess.Launch(
            _backend.Url, TestConfig.StudentOid, autoJoin: true, heartbeatIntervalSeconds: 1,
            reconnectMaxBackoff: TimeSpan.FromSeconds(2));

        Guid sessionId = Guid.Empty;
        try
        {
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

            var classId = await api.FindClassIdAsync();
            var session = await api.StartSessionAsync(classId);
            sessionId = session.Id;

            var joined = await agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
            Assert.True(
                joined?.JoinedSessionId == session.Id,
                $"Agent did not auto-join within 8s (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");

            // --- restart: same database, empty memory ------------------------
            await _backend.StopAsync();
            var dropped = await agent.WaitForAsync(
                s => s.ConnectionStatus != "Connected", TimeSpan.FromSeconds(10));
            Assert.True(
                dropped is not null && dropped.ConnectionStatus != "Connected",
                "Agent still reported Connected 10s after the backend went down.");

            await _backend.RestartAsync();
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(30));
            // Still the same session: the agent reconnected rather than rejoined.
            var reconnected = await agent.TryGetStatusAsync();
            Assert.Equal(session.Id, reconnected?.JoinedSessionId);

            // --- alive: the restart itself must not read as a lost agent ------
            await Task.Delay(TimeSpan.FromSeconds(6));
            var whileAlive = await api.GetSessionEventKindsAsync(session.Id);
            Assert.False(
                whileAlive.Contains(HeartbeatLost),
                $"Backend reported HeartbeatLost while the reconnected agent was pinging. Events: {Join(whileAlive)}.");

            // --- gone: only a tracked agent can be reported lost --------------
            agent.Kill();
            var lost = await PollForEventAsync(api, session.Id, HeartbeatLost, TimeSpan.FromSeconds(12));
            Assert.True(
                lost,
                "Restarted backend did not record HeartbeatLost within 12s of the agent being killed — " +
                "its post-restart heartbeats were not accepted.");
        }
        finally
        {
            await agent.DisposeAsync();
            // Never leave the shared backend down for the specs that follow.
            if (!_backend.IsRunning)
                await _backend.RestartAsync();
            if (sessionId != Guid.Empty)
                await api.EndSessionAsync(sessionId);
        }
    }

    private static async Task<bool> PollForEventAsync(
        BackendClient api, Guid sessionId, string kind, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var kinds = await api.GetSessionEventKindsAsync(sessionId);
            if (kinds.Contains(kind)) return true;
            await Task.Delay(500);
        }
        return false;
    }

    private static string Join(IReadOnlyCollection<string> kinds) =>
        kinds.Count > 0 ? string.Join(", ", kinds) : "<none>";
}
