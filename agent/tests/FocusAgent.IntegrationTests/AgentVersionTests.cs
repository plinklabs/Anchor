namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end proof for #208: the agent has a <em>single</em> version source
/// (<c>agent/Directory.Build.props</c>) that flows all the way into the produced
/// exe. The build stamps that one &lt;VersionPrefix&gt; into the assembly's
/// InformationalVersion; the running agent reads it back and reports it on
/// /status (StatusEndpoint.AgentVersion).
///
/// This spec launches the <em>real</em> built agent and asserts the version it
/// reports equals the version declared in Directory.Build.props. Bump the one
/// number in that file and rebuild → this value moves with it; nothing else in
/// the agent declares a version that could drift, which is the whole point of the
/// single source. A pure unit test cannot prove this: only an end-to-end run of
/// the actually-built exe shows the props value survived the build into the
/// shipped binary.
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class AgentVersionTests
{
    private readonly BackendFixture _backend;
    public AgentVersionTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task RunningAgent_ReportsTheVersionFromTheSingleSource()
    {
        var expected = TestConfig.ExpectedAgentVersion;
        Assert.False(
            string.IsNullOrWhiteSpace(expected),
            "Directory.Build.props declared no agent version.");

        await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid);

        // The version is reported regardless of connection state, but waiting for
        // a reachable /status snapshot keeps this robust against launch timing.
        var snap = await agent.WaitForAsync(
            s => !string.IsNullOrEmpty(s.AgentVersion), TimeSpan.FromSeconds(20));

        Assert.NotNull(snap);
        Assert.Equal(expected, snap!.AgentVersion);
    }
}
