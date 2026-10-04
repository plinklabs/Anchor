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
///
/// <see cref="StopAsync"/> + <see cref="RestartAsync"/> take the backend down and
/// bring it back on the <em>same</em> database, the way a deploy or an App
/// Service restart does in production: every in-memory structure (heartbeat
/// tracking, the active-participant cache, hub groups) is gone, the rows are not.
/// A restart can also override backend configuration for the spec that needs it
/// (e.g. a seconds-long session limit for the auto-end spec, #345); the next
/// restart without overrides goes back to the defaults.
/// </summary>
internal sealed class BackendProcess : IAsyncDisposable
{
    private Process? _process;

    public string Url => TestConfig.BackendUrl;

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(CancellationToken ct = default)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = TestConfig.BackendDbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }

        await LaunchAsync(build: true, configuration: null, ct);
    }

    /// <summary>
    /// Start the backend again after <see cref="StopAsync"/>, keeping the
    /// database (including its -wal journal, which holds the most recent
    /// commits). Skips the build: the first start already built this tree.
    /// <paramref name="configuration"/> adds environment variables (config keys
    /// in <c>Section__Key</c> form) on top of the usual e2e ones, for this run
    /// of the backend only.
    /// </summary>
    public Task RestartAsync(
        IReadOnlyDictionary<string, string>? configuration = null, CancellationToken ct = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("Backend is still running; stop it before restarting.");
        return LaunchAsync(build: false, configuration, ct);
    }

    /// <summary>Kill the backend process tree, as a crash or redeploy would.</summary>
    public async Task StopAsync()
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
            _process = null;
        }
    }

    private async Task LaunchAsync(bool build, IReadOnlyDictionary<string, string>? configuration, CancellationToken ct)
    {
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
        if (!build) psi.ArgumentList.Add("--no-build");
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
        foreach (var (key, value) in configuration ?? new Dictionary<string, string>())
            psi.Environment[key] = value;

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

    public async ValueTask DisposeAsync() => await StopAsync();
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

    public bool IsRunning => _backend.IsRunning;

    public async Task InitializeAsync() => await _backend.StartAsync();

    /// <summary>Take the shared backend down. Pair with <see cref="RestartAsync"/>.</summary>
    public Task StopAsync() => _backend.StopAsync();

    /// <summary>
    /// Bring the shared backend back up on the same database, optionally with
    /// configuration overrides that last until the next restart.
    /// </summary>
    public Task RestartAsync(IReadOnlyDictionary<string, string>? configuration = null) =>
        _backend.RestartAsync(configuration);

    public async Task DisposeAsync() => await _backend.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class AgentE2ECollection : ICollectionFixture<BackendFixture>
{
    public const string Name = "agent-e2e";
}
