using System.Diagnostics;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// The agent on a teacher's own laptop (#410). Starting a session used to send
/// <c>SessionStarted</c> to the whole class, its teachers included, so the
/// teacher's agent joined the session they had just started and its sweep
/// minimized every off-list window before they could pick a bundle. Teachers
/// aren't participants by default now: the start reaches the class's members
/// only, and a teacher takes part by choice, with the join code the live page
/// shows. Drives the real agent, signed in as the seeded teacher of the class,
/// against the real backend.
///
/// Note: once the teacher takes part, the agent enforces the session like a
/// student's, so the run minimizes off-list windows on the desktop, as the
/// other sweep specs do.
/// </summary>
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class TeacherTakingPartTests
{
    private const string HeartbeatLost = "HeartbeatLost";

    private readonly BackendFixture _backend;
    public TeacherTakingPartTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task TheTeachersOwnAgent_IsNotSweptWhenTheyStartASession_AndTakesPartOnlyByCode()
    {
        var api = new BackendClient(_backend.Url);
        await using var agent = AgentProcess.Launch(
            _backend.Url, TestConfig.TeacherOid, autoJoin: true, heartbeatIntervalSeconds: 1);
        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
        // Connected is reported on the handshake, a moment before the backend
        // adds the connection to the teacher's user group (#356). Give it that
        // moment, so a SessionStarted sent to the teacher would reach the
        // agent and the check below can't pass for want of a group.
        await Task.Delay(TimeSpan.FromSeconds(1));

        var classId = await api.FindClassIdAsync();
        var session = await api.StartSessionAsync(classId);
        try
        {
            // --- the teacher starts it: their agent stays out of it ----------
            // --auto-join confirms a start at once, so a start that reached the
            // agent would show here within the second (the student specs give
            // it 8).
            var during = await agent.WaitForAsync(
                s => s.ActiveSessionId == session.Id || s.JoinedSessionId == session.Id || s.StartupSweep is not null,
                TimeSpan.FromSeconds(5));
            Assert.NotNull(during);
            Assert.True(
                during.ActiveSessionId != session.Id && during.JoinedSessionId != session.Id && during.StartupSweep is null,
                $"The teacher's own agent took part in the session they started (activeSessionId: " +
                $"{during.ActiveSessionId?.ToString() ?? "<none>"}, joinedSessionId: " +
                $"{during.JoinedSessionId?.ToString() ?? "<none>"}, swept: {during.StartupSweep is not null}).");

            // --- taking part is their choice: the code from the live page ----
            // The REST call the agent's "Join by code" dialog makes.
            var joinedId = await api.JoinByCodeAsync(session.JoinCode, TestConfig.TeacherOid);
            Assert.Equal(session.Id, joinedId);

            var joined = await agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id && s.StartupSweep is not null, TimeSpan.FromSeconds(8));
            Assert.True(
                joined?.JoinedSessionId == session.Id && joined.StartupSweep is not null,
                $"The teacher's agent did not join and sweep within 8s of joining by code (joinedSessionId: " +
                $"{joined?.JoinedSessionId?.ToString() ?? "<none>"}, swept: {joined?.StartupSweep is not null}).");

            // --- and the backend then holds them to it like a student --------
            // Only an agent whose heartbeats the backend accepted can be
            // reported lost: let it ping, then take it away.
            await Task.Delay(TimeSpan.FromSeconds(2));
            agent.Kill();
            Assert.True(
                await PollForEventAsync(api, session.Id, HeartbeatLost, TimeSpan.FromSeconds(12)),
                "Backend did not record HeartbeatLost within 12s of the teacher's agent being killed: " +
                "the heartbeats of a teacher taking part in their own session were not accepted.");
        }
        finally
        {
            await api.EndSessionAsync(session.Id);
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
}
