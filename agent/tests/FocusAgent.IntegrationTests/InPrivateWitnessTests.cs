using System.Diagnostics;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Asserting e2e for agent-side InPrivate detection (#148): while a student is in
/// a joined session, an Edge InPrivate window left open must be detected by the
/// agent and surface as a <c>TamperDetected{inprivate_opened}</c> event on the
/// teacher's session — the exact signal the extension cannot witness when it has
/// no incognito access. Since #535 only once the window has stayed open for
/// <c>Session:SchoolProfileReportSeconds</c>, and not before.
///
/// A real InPrivate Edge window can't be driven reliably headlessly (it needs
/// Edge installed and an interactive InPrivate launch), so the agent runs with
/// <c>--simulate-inprivate</c>, which swaps a synthetic scanner reporting one
/// InPrivate window that stays open. That exercises the real witness → reporter →
/// hub → backend path end-to-end; the title-parsing heuristic itself is covered by
/// the <c>InPrivateDetection</c> unit tests. This is the headless lever added
/// alongside the feature, the analog of <c>--auto-join</c> for the join toast.
/// No extension ever connects here, so the school-profile rule (#525) is off and
/// nothing is paused: the report still comes, after the same wait. A window
/// closed in time, with the rule on, is <c>SchoolProfileTests</c>'.
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class InPrivateWitnessTests
{
    private const string TamperDetected = "TamperDetected";
    private static readonly TimeSpan Report = TimeSpan.FromSeconds(8);

    private readonly BackendFixture _backend;
    public InPrivateWitnessTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task InPrivateWindowLeftOpenDuringSession_SurfacesAsTamperDetected_AfterTheReportTime()
    {
        var api = new BackendClient(_backend.Url);
        await using var agent = AgentProcess.Launch(
            _backend.Url, TestConfig.StudentOid, autoJoin: true, simulateInPrivate: true,
            extraEnvironment: new Dictionary<string, string>
            {
                ["Session__InPrivateScanIntervalSeconds"] = "1",
                ["Session__SchoolProfileReportSeconds"] = ((int)Report.TotalSeconds).ToString(),
            });
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

            // The agent-side witness sees the simulated window at once...
            var seen = await agent.WaitForAsync(s => s.InPrivateOpen >= 1, TimeSpan.FromSeconds(5));
            Assert.True(
                seen?.InPrivateOpen >= 1,
                $"Agent never saw the InPrivate window within 5s (inPrivateOpen: {seen?.InPrivateOpen.ToString() ?? "<none>"}).");
            // ...but doesn't tell the teacher yet (#535).
            Assert.Equal(0, seen!.InPrivateDetections);
            Assert.Equal(0, await TamperReports.CountAsync(api, session.Id, "inprivate_opened"));

            // Still open after the report time: the witness reports it...
            var detected = await agent.WaitForAsync(
                s => s.InPrivateDetections >= 1, Report + TimeSpan.FromSeconds(10));
            Assert.True(
                detected?.InPrivateDetections >= 1,
                $"Agent never reported the InPrivate window on /status within {(Report + TimeSpan.FromSeconds(10)).TotalSeconds:N0}s " +
                $"(inPrivateDetections: {detected?.InPrivateDetections.ToString() ?? "<none>"}).");

            // …and it must reach the teacher's session as a TamperDetected event, once.
            var surfaced = await PollForEventAsync(api, session.Id, TamperDetected, TimeSpan.FromSeconds(12));
            Assert.True(
                surfaced,
                $"Backend did not record {TamperDetected} within 12s of the InPrivate detection.");
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.Equal(1, await TamperReports.CountAsync(api, session.Id, "inprivate_opened"));
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
