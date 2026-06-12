using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// The agent's /status snapshot (see
/// <c>agent/src/FocusAgent.App/Connectivity/StatusEndpoint.cs</c>). Polling this
/// is how the harness observes the agent's <em>real</em> connection + session +
/// matcher state instead of guessing from screenshots or logs.
/// </summary>
internal sealed record StatusSnapshot(
    [property: JsonPropertyName("connectionStatus")] string? ConnectionStatus,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("activeSessionId")] Guid? ActiveSessionId,
    [property: JsonPropertyName("joinedSessionId")] Guid? JoinedSessionId,
    [property: JsonPropertyName("allowedApps")] string[]? AllowedApps);

/// <summary>
/// Launches the real agent exe headless and exposes its /status state to the
/// specs. Mirrors how <c>scripts/dev/verify-*.ps1</c> drive the agent, but as
/// an asserting harness:
///
///   * <c>--inject-token</c> bypasses WAM; the agent authenticates to the
///     backend via X-Dev-Impersonate-Oid alone.
///   * <c>--status-endpoint &lt;port&gt;</c> exposes JSON state on loopback.
///   * <c>--auto-join</c> (opt-in) makes the agent an active participant so it
///     receives mid-session pushes and sends heartbeats.
///
/// Config is overridden per launch via environment variables, which the agent
/// honours only under <c>--inject-token</c> (see App.BuildHost): the backend URL
/// (so the agent talks to the throwaway e2e backend, not the dev default), which
/// seeded student to impersonate, and the heartbeat cadence.
/// </summary>
internal sealed class AgentProcess : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Process _process;
    private readonly HttpClient _http;
    private readonly string _statusUrl;

    private AgentProcess(Process process, int statusPort)
    {
        _process = process;
        _statusUrl = $"http://127.0.0.1:{statusPort}/status";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    }

    public int StatusPort { get; private init; }

    /// <summary>
    /// Launch the agent impersonating <paramref name="impersonateOid"/>, pointed
    /// at the e2e backend. Set <paramref name="autoJoin"/> for flows that need
    /// the agent to actually join (bundle pushes, heartbeats).
    /// </summary>
    public static AgentProcess Launch(
        string backendUrl,
        string impersonateOid,
        bool autoJoin = false,
        int? heartbeatIntervalSeconds = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("The agent exe is Windows-only.");
        if (!File.Exists(TestConfig.AgentExe))
            throw new FileNotFoundException(
                $"Agent exe not found at {TestConfig.AgentExe}. Build it first: " +
                "dotnet build agent/FocusAgent.sln -p:Platform=x64", TestConfig.AgentExe);

        var statusPort = TestConfig.FreeLoopbackPort();

        var psi = new ProcessStartInfo(TestConfig.AgentExe) { UseShellExecute = false };
        psi.ArgumentList.Add("--inject-token");
        psi.ArgumentList.Add("--status-endpoint");
        psi.ArgumentList.Add(statusPort.ToString());
        if (autoJoin) psi.ArgumentList.Add("--auto-join");

        // Honoured only under --inject-token (App.BuildHost layers env vars last
        // in that mode). __ is the config-section delimiter.
        psi.Environment["Backend__BaseUrl"] = backendUrl;
        psi.Environment["Dev__ImpersonateOid"] = impersonateOid;
        if (heartbeatIntervalSeconds is { } hb)
            psi.Environment["Session__HeartbeatIntervalSeconds"] = hb.ToString();

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the agent process.");
        return new AgentProcess(process, statusPort) { StatusPort = statusPort };
    }

    public async Task<StatusSnapshot?> TryGetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<StatusSnapshot>(_statusUrl, Json, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Poll until the agent reports Connected, or throw with the last error.</summary>
    public async Task WaitForConnectedAsync(TimeSpan timeout)
    {
        var last = await WaitForAsync(s => s.ConnectionStatus == "Connected", timeout);
        if (last?.ConnectionStatus != "Connected")
        {
            throw new TimeoutException(
                $"Agent did not reach Connected within {timeout.TotalSeconds:N0}s " +
                $"(last status: {last?.ConnectionStatus ?? "<unreachable>"}, error: {last?.LastError ?? "<none>"}).");
        }
    }

    /// <summary>
    /// Poll /status until <paramref name="predicate"/> holds or the timeout
    /// elapses. Returns the last snapshot seen (null only if never reachable).
    /// </summary>
    public async Task<StatusSnapshot?> WaitForAsync(Func<StatusSnapshot, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        StatusSnapshot? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await TryGetStatusAsync();
            if (last is not null && predicate(last)) return last;

            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Agent process exited early with code {_process.ExitCode} while waiting on /status. " +
                    "If exit code is 0 a prior agent instance may still own the single-instance lock.");
            }
            await Task.Delay(200);
        }
        return last;
    }

    /// <summary>Kill the agent (e.g. the heartbeat spec's "agent goes away" trigger).</summary>
    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best-effort
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        try { await _process.WaitForExitAsync(); } catch { }
        _process.Dispose();
        _http.Dispose();
    }
}
