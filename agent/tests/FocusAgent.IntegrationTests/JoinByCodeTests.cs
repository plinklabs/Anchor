using System.Net;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Port of <c>scripts/dev/verify-join-by-code.ps1</c> as an asserting test
/// (#34): the seeded outsider is in no class, so the roster-based SessionStarted
/// push skips them — the only way in is the code. Also exercises the error paths
/// (unknown code → 404, repeated wrong codes → 429) headlessly.
///
/// Success and error paths live in one test on purpose: the limiter is keyed by
/// the outsider's id over a 1-minute window, and a successful join resets that
/// bucket (JoinByCodeRateLimiter.Reset). Doing the success first guarantees a
/// clean bucket before the rate-limit assertion — splitting them would couple
/// the two tests through shared limiter state.
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class JoinByCodeTests
{
    private readonly BackendFixture _backend;
    public JoinByCodeTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task OutsiderJoinsByCode_ThenUnknownCode404s_AndRepeatedFailures429()
    {
        var api = new BackendClient(_backend.Url);
        await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.OutsiderOid);

        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

        var classId = await api.FindClassIdAsync();
        var session = await api.StartSessionAsync(classId);
        try
        {
            // The outsider isn't rostered, so the roster push must NOT have
            // reached them — activeSessionId should still be unset.
            var before = await agent.TryGetStatusAsync();
            Assert.NotEqual(session.Id, before?.ActiveSessionId);

            // --- happy path: join by code, agent receives SessionStarted ----
            var joinedId = await api.JoinByCodeAsync(session.JoinCode, TestConfig.OutsiderOid);
            Assert.Equal(session.Id, joinedId);

            var status = await agent.WaitForAsync(
                s => s.ActiveSessionId == session.Id, TimeSpan.FromSeconds(5));
            Assert.True(
                status?.ActiveSessionId == session.Id,
                $"Agent did not see SessionStarted via the manual join path within 5s. " +
                $"activeSessionId: {status?.ActiveSessionId?.ToString() ?? "<none>"}.");

            // The successful join reset the limiter, so the bucket is clean here.

            // --- error path: unknown code is a 404 --------------------------
            using (var notFound = await api.JoinByCodeRawAsync("000000", TestConfig.OutsiderOid))
            {
                Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
            }

            // --- error path: repeated wrong codes trip the 429 limiter ------
            // 5 failures/window; the 404 above was #1. Poll defensively rather
            // than assume the exact remaining budget.
            HttpStatusCode last = HttpStatusCode.NotFound;
            for (var i = 0; i < 12 && last != HttpStatusCode.TooManyRequests; i++)
            {
                using var res = await api.JoinByCodeRawAsync("999999", TestConfig.OutsiderOid);
                last = res.StatusCode;
                Assert.True(
                    last is HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests,
                    $"Unexpected status {(int)last} {last} from a wrong join code " +
                    "(want 404 until rate-limited, then 429).");
            }

            Assert.Equal(HttpStatusCode.TooManyRequests, last);
        }
        finally
        {
            await api.EndSessionAsync(session.Id);
        }
    }
}
