using System.Diagnostics;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Boots the backend for the e2e run via <c>dotnet run</c>, the agent-side
/// analog of the extension harness's <c>run-backend.ts</c> + Playwright
/// <c>webServer</c>. Kestrel binds the port only after EnsureCreated + the dev
/// seeder finish, so "reachable" already means "seeded and ready".
///
/// Each boot starts from a deleted SQLite file so the schema is rebuilt from
/// the current model every time (EnsureCreatedAsync does not migrate — a stale
/// e2e DB would silently drift). Heartbeat timings are sped up via env vars so
/// the heartbeat spec resolves in seconds instead of the ~30s production cadence
/// would need.
/// </summary>
internal sealed class BackendProcess : IAsyncDisposable
{
    private Process? _process;

    public string Url => TestConfig.BackendUrl;

    public async Task StartAsync(CancellationToken ct = default)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = TestConfig.BackendDbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = TestConfig.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(TestConfig.BackendProject);
        psi.ArgumentList.Add("--no-launch-profile");
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add(TestConfig.BackendUrl);

        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ConnectionStrings__DefaultConnection"] = $"Data Source={TestConfig.BackendDbPath}";
        // Speed up stale-agent detection so the heartbeat spec is quick:
        // timeout = Interval * Multiplier = 4s, scan every 1s.
        psi.Environment["Heartbeat__IntervalSeconds"] = "2";
        psi.Environment["Heartbeat__TimeoutMultiplier"] = "2";
        psi.Environment["Heartbeat__ScanIntervalSeconds"] = "1";
        // Keep the captured CI log readable — the EF command logger is otherwise
        // hundreds of SQL lines per run.
        psi.Environment["Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command"] = "Warning";

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // Drain the pipes so the child never blocks on a full buffer; echo to
        // the test output so a boot failure is diagnosable in CI.
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"[backend] {e.Data}"); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine($"[backend] {e.Data}"); };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitUntilReachableAsync(TimeSpan.FromSeconds(180), ct);
    }

    private async Task WaitUntilReachableAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"Backend process exited early with code {_process.ExitCode} before becoming reachable.");
            }

            try
            {
                // Any HTTP response (even 404) means Kestrel is listening, which
                // — because the port binds only post-seed — means it's ready.
                await http.GetAsync(TestConfig.BackendUrl, ct);
                return;
            }
            catch
            {
                await Task.Delay(500, ct);
            }
        }

        throw new TimeoutException(
            $"Backend did not become reachable at {TestConfig.BackendUrl} within {timeout.TotalSeconds:N0}s.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                // Kill the whole tree: `dotnet run` spawns the Kestrel host as a
                // child, which would otherwise outlive the launcher.
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch
        {
            // best-effort teardown
        }
        finally
        {
            _process.Dispose();
        }
    }
}

/// <summary>
/// xUnit collection fixture: boots one backend for the whole suite (mirrors
/// Playwright booting a single webServer for the run). All specs share it via
/// <c>[Collection(AgentE2ECollection.Name)]</c>, which also forces the specs to
/// run serially — they share one backend and one seeded student identity, so
/// overlapping sessions would cross-talk.
/// </summary>
public sealed class BackendFixture : IAsyncLifetime
{
    private readonly BackendProcess _backend = new();

    public string Url => _backend.Url;

    public async Task InitializeAsync() => await _backend.StartAsync();

    public async Task DisposeAsync() => await _backend.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class AgentE2ECollection : ICollectionFixture<BackendFixture>
{
    public const string Name = "agent-e2e";
}
