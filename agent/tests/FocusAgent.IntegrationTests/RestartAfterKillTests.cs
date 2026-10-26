namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end coverage for #461: an agent started right after one that was ended
/// from Task Manager (a hard kill) starts, every time.
///
/// A killed agent is not gone when its exit code is: Windows tears it down after
/// that, and until its UI thread has exited it still holds the single-instance key.
/// The harness's kill returns as soon as the exit code is set, so a launch straight
/// after it lands in that teardown, as a restart by hand on a slow laptop or by
/// Velopack's updater (which kills without waiting) can. Such a launch used to take
/// the dying agent for a running one and exit at once with code 0, about one restart
/// in five while in a session; now it waits for the dying agent to be gone. The
/// restarts are repeated so a regression can't slip through on luck.
///
/// A second launch while an agent is running still steps aside at once.
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class RestartAfterKillTests
{
    // Enough that the old behaviour (about one restart in five exiting at once) can't
    // pass by chance: at that rate, 20 restarts all get through about one run in seventy.
    private const int Restarts = 20;

    private readonly BackendFixture _backend;
    public RestartAfterKillTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task An_agent_started_right_after_one_killed_in_a_session_starts_and_rejoins_every_time()
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

            for (var restart = 1; restart <= Restarts; restart++)
            {
                // Ended from Task Manager, then started again straight away, as the
                // student does: no auto-join and a long join confirmation, so only the
                // silent rejoin (#54) can put it back in the session within the wait.
                await agent.DisposeAsync();
                agent = AgentProcess.Launch(
                    _backend.Url,
                    TestConfig.StudentOid,
                    extraEnvironment: new Dictionary<string, string> { ["Realtime__JoinConfirmationDuration"] = "00:01:00" });

                // Throws "exited early with code 0" when the launch stepped aside for
                // the killed agent.
                try
                {
                    await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
                }
                catch (InvalidOperationException ex)
                {
                    Assert.Fail($"Restart {restart} of {Restarts} did not start: {ex.Message}");
                }
                var rejoined = await agent.WaitForAsync(
                    s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
                Assert.True(
                    rejoined?.JoinedSessionId == session.Id,
                    $"Restart {restart} of {Restarts} did not rejoin the session within 8s " +
                    $"(joinedSessionId: {rejoined?.JoinedSessionId?.ToString() ?? "<none>"}).");
            }
        }
        finally
        {
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

    [Fact]
    public async Task A_second_launch_while_an_agent_is_running_exits_at_once_and_leaves_it_running()
    {
        await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid);
        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

        await using var second = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid);
        // Well inside the 10 s an exiting agent may be waited for: a running one
        // isn't waited for at all.
        await second.WaitForExitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, second.ExitCode);

        Assert.False(agent.HasExited, "The running agent exited after a second launch.");
        var status = await agent.TryGetStatusAsync();
        Assert.Equal("Connected", status?.ConnectionStatus);
    }
}
