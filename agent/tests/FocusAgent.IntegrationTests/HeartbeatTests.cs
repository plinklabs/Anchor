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
