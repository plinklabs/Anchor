namespace FocusAgent.IntegrationTests;

/// <summary>
/// Port of <c>scripts/dev/verify-bundle-switch.ps1</c> as an asserting test
/// (#93): with the agent actively joined, the teacher amends the session's
/// bundle set and the agent must rebuild its app matcher — observed through the
/// real /status.allowedApps, so this clears the "seen in a running agent" bar,
/// not just a green unit test.
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class BundleSwitchTests
{
    private readonly BackendFixture _backend;
    public BundleSwitchTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task AmendingBundlesMidSession_RebuildsTheAgentsAppMatcher()
    {
        var api = new BackendClient(_backend.Url);
        await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);

        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

        var classId = await api.FindClassIdAsync();
        var appBundleId = await api.FindBundleIdAsync(TestConfig.AppBundleName);
        var session = await api.StartSessionAsync(classId);
        try
        {
            // Auto-join: only an active participant receives mid-session pushes.
            var joined = await agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
            Assert.True(
                joined?.JoinedSessionId == session.Id,
                $"Agent did not auto-join within 8s (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");

            // Baseline: no bundles, so the test app is NOT allowed yet.
            var baseline = joined!.AllowedApps ?? Array.Empty<string>();
            Assert.DoesNotContain(TestConfig.AppProcessName, baseline);

            // Add the app bundle → matcher must pick up the new app.
            await api.UpdateBundlesAsync(session.Id, new[] { appBundleId });
            var added = await agent.WaitForAsync(
                s => (s.AllowedApps ?? Array.Empty<string>()).Contains(TestConfig.AppProcessName),
                TimeSpan.FromSeconds(5));
            Assert.True(
                (added?.AllowedApps ?? Array.Empty<string>()).Contains(TestConfig.AppProcessName),
                $"Agent did not pick up '{TestConfig.AppProcessName}' within 5s of the bundle add. " +
                $"allowedApps: {Join(added?.AllowedApps)}.");

            // Remove all bundles → matcher must drop the app again.
            await api.UpdateBundlesAsync(session.Id, Array.Empty<Guid>());
            var removed = await agent.WaitForAsync(
                s => !(s.AllowedApps ?? Array.Empty<string>()).Contains(TestConfig.AppProcessName),
                TimeSpan.FromSeconds(5));
            Assert.True(
                !(removed?.AllowedApps ?? Array.Empty<string>()).Contains(TestConfig.AppProcessName),
                $"Agent still allows '{TestConfig.AppProcessName}' 5s after removing the bundle. " +
                $"allowedApps: {Join(removed?.AllowedApps)}.");
        }
        finally
        {
            await api.EndSessionAsync(session.Id);
        }
    }

    private static string Join(string[]? apps) =>
        apps is { Length: > 0 } ? string.Join(", ", apps) : "<none>";
}
