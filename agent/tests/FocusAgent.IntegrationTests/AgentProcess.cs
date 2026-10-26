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
    [property: JsonPropertyName("agentVersion")] string? AgentVersion,
    [property: JsonPropertyName("connectionStatus")] string? ConnectionStatus,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("activeSessionId")] Guid? ActiveSessionId,
    [property: JsonPropertyName("joinedSessionId")] Guid? JoinedSessionId,
    [property: JsonPropertyName("allowedApps")] string[]? AllowedApps,
    [property: JsonPropertyName("startupSweep")] StartupSweepSnapshot? StartupSweep,
    [property: JsonPropertyName("inPrivateDetections")] int InPrivateDetections,
    [property: JsonPropertyName("extensionPolicy")] ExtensionPolicySnapshot? ExtensionPolicy = null,
    [property: JsonPropertyName("extensionGuard")] ExtensionGuardSnapshot? ExtensionGuard = null,
    // #452: what the focus overlay shows while it is up (Blocked / BrowserPaused),
    // null while it is not.
    [property: JsonPropertyName("overlay")] string? Overlay = null,
    // #449: the downloaded update the next agent start applies, null while none is.
    [property: JsonPropertyName("pendingUpdate")] string? PendingUpdate = null,
    // #456: when the last update check started, by the agent's update clock.
    [property: JsonPropertyName("lastUpdateCheck")] DateTimeOffset? LastUpdateCheck = null,
    // #446: what the log uploads did in this agent process.
    [property: JsonPropertyName("logUpload")] LogUploadSnapshot? LogUpload = null);

/// <summary>
/// The log uploads as exposed on /status (#446): how many passes ran in this agent
/// process (one per connect), how many session windows they uploaded, when the
/// last pass ended and its error, if any.
/// </summary>
internal sealed record LogUploadSnapshot(
    [property: JsonPropertyName("passes")] int Passes,
    [property: JsonPropertyName("uploaded")] int Uploaded,
    [property: JsonPropertyName("lastPassAt")] DateTimeOffset? LastPassAt,
    [property: JsonPropertyName("lastError")] string? LastError);

/// <summary>
/// The Edge pause as exposed on /status (#427): whether the extension's witness
/// pipe is connected, and the guard's state (<c>Off</c> / <c>Present</c> /
/// <c>Waiting</c> / <c>Dormant</c> / <c>Paused</c> / <c>Repairing</c>).
/// </summary>
internal sealed record ExtensionGuardSnapshot(
    [property: JsonPropertyName("witnessConnected")] bool WitnessConnected,
    [property: JsonPropertyName("state")] string? State);

/// <summary>
/// The Edge extension setup as exposed on /status (#405): where an Anchor
/// force-install entry was found (<c>None</c> / <c>Machine</c> / <c>User</c>) and
/// whether the setup window (the explanation before the UAC prompt) is open.
/// </summary>
internal sealed record ExtensionPolicySnapshot(
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("consentOpen")] bool ConsentOpen);

/// <summary>
/// The agent's session-start sweep result (#104) as exposed on /status: how many
/// top-level windows it examined at join and which off-list processes it
/// minimized. Null until the first session of the agent process has started.
/// </summary>
internal sealed record StartupSweepSnapshot(
    [property: JsonPropertyName("windowsExamined")] int WindowsExamined,
    [property: JsonPropertyName("minimizedProcesses")] string[]? MinimizedProcesses);

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
/// seeded student to impersonate, and the heartbeat cadence. The agent logs to the
/// run's temporary folder, <see cref="AgentTestLogs.Folder"/>, which it inherits
/// from the test process (#463); a spec can pass its own folder through
/// <c>extraEnvironment</c> (<c>Diagnostics__LogDirectory</c>).
/// </summary>
internal sealed class AgentProcess : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Process _process;
    private readonly HttpClient _http;
    private readonly string _statusUrl;
    private readonly string _controlBase;

    private AgentProcess(Process process, int statusPort, IReadOnlyList<string> arguments)
    {
        _process = process;
        Arguments = arguments;
        _controlBase = $"http://127.0.0.1:{statusPort}";
        _statusUrl = $"{_controlBase}/status";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    }

    public int StatusPort { get; private init; }

    /// <summary>The agent's process id, for matching its windows.</summary>
    public int Pid => _process.Id;

    /// <summary>True once the agent process has exited.</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>The exit code, once the agent process has exited.</summary>
    public int ExitCode => _process.ExitCode;

    /// <summary>The command-line arguments the agent was launched with (#456: it restarts with the same).</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// Launch the agent impersonating <paramref name="impersonateOid"/>, pointed
    /// at the e2e backend. Set <paramref name="autoJoin"/> for flows that need
    /// the agent to actually join (bundle pushes, heartbeats). <paramref name="agentExe"/>
    /// launches a copy of the agent elsewhere (#449: one inside an install the spec
    /// assembled) instead of the x64 Debug build.
    /// </summary>
    public static AgentProcess Launch(
        string backendUrl,
        string impersonateOid,
        bool autoJoin = false,
        int? heartbeatIntervalSeconds = null,
        bool simulateInPrivate = false,
        string? environmentName = null,
        bool pointBackendViaEnv = true,
        TimeSpan? reconnectMaxBackoff = null,
        IEnumerable<string>? extraArgs = null,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        string? agentExe = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("The agent exe is Windows-only.");
        agentExe ??= TestConfig.AgentExe;
        if (!File.Exists(agentExe))
            throw new FileNotFoundException(
                $"Agent exe not found at {agentExe}. Build it first: " +
                "dotnet build agent/FocusAgent.sln -p:Platform=x64", agentExe);

        var statusPort = TestConfig.FreeLoopbackPort();

        var psi = new ProcessStartInfo(agentExe) { UseShellExecute = false };
        psi.ArgumentList.Add("--inject-token");
        psi.ArgumentList.Add("--status-endpoint");
        psi.ArgumentList.Add(statusPort.ToString());
        if (autoJoin) psi.ArgumentList.Add("--auto-join");
        // #148: swap the real Edge-window scanner for a synthetic one that always
        // reports an InPrivate window, so the witness path can be driven without a
        // real InPrivate browser window in the headless run.
        if (simulateInPrivate) psi.ArgumentList.Add("--simulate-inprivate");

        // #203: let a spec pin the hosting environment so it can exercise the
        // Production config layer (appsettings.Production.json) rather than the
        // Debug-build default of Development. App.ResolveEnvironmentName honours
        // DOTNET_ENVIRONMENT first.
        if (environmentName is not null)
            psi.Environment["DOTNET_ENVIRONMENT"] = environmentName;

        // Honoured only under --inject-token (App.BuildHost layers env vars last
        // in that mode). __ is the config-section delimiter. The config-substitution
        // spec (#203) leaves this OFF so the *only* thing repointing the agent at the
        // e2e backend is the substituted appsettings.Production.json — proving the
        // per-deployment file alone controls the backend URL.
        if (pointBackendViaEnv)
            psi.Environment["Backend__BaseUrl"] = backendUrl;
        psi.Environment["Dev__ImpersonateOid"] = impersonateOid;
        if (heartbeatIntervalSeconds is { } hb)
            psi.Environment["Session__HeartbeatIntervalSeconds"] = hb.ToString();
        // Caps the hub's reconnect backoff (default 30s) so a spec that restarts
        // the backend doesn't wait out a long retry delay.
        if (reconnectMaxBackoff is { } backoff)
            psi.Environment["Realtime__ReconnectMaxBackoff"] = backoff.ToString("c");
        // Any other per-spec flags / config (e.g. the #405 Extension__* test knobs).
        foreach (var arg in extraArgs ?? [])
            psi.ArgumentList.Add(arg);
        foreach (var (name, value) in extraEnvironment ?? new Dictionary<string, string>())
            psi.Environment[name] = value;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the agent process.");
        return new AgentProcess(process, statusPort, psi.ArgumentList.ToList()) { StatusPort = statusPort };
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

    /// <summary>
    /// Drive the agent's "Leave session" action (#102) via the status endpoint's
    /// POST /leave control — the headless stand-in for clicking the button. The
    /// agent emits ManualLeave, leaves the session, and keeps running.
    /// </summary>
    public async Task LeaveSessionAsync()
    {
        using var res = await _http.PostAsync($"{_controlBase}/leave", content: null);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Drive the agent's window "Close" action (#102) via POST /close — hides the
    /// window to the tray. The agent must keep running and stay in any session.
    /// </summary>
    public async Task CloseWindowAsync()
    {
        using var res = await _http.PostAsync($"{_controlBase}/close", content: null);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Drive the agent's "Quit" action (#110) via POST /quit — the headless
    /// stand-in for tray → Exit Anchor. Distinct from <see cref="Kill"/>: this is a
    /// clean shutdown. A hard <see cref="Kill"/> is the crash case. Throws when the
    /// agent refuses, which it does while a session is going on (#445); see
    /// <see cref="TryQuitAsync"/>.
    /// </summary>
    public async Task QuitAsync()
    {
        using var res = await _http.PostAsync($"{_controlBase}/quit", content: null);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Pick the tray's "Exit Anchor" (#110) via POST /quit, where it may be refused:
    /// true when the agent is on its way out, false when it answered 409 because a
    /// session is going on and the tray offers no Exit then (#445).
    /// </summary>
    public async Task<bool> TryQuitAsync()
    {
        using var res = await _http.PostAsync($"{_controlBase}/quit", content: null);
        if (res.StatusCode == System.Net.HttpStatusCode.Conflict) return false;
        res.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// Answer the Edge extension setup window (#405) via POST /extension-consent —
    /// the headless stand-in for clicking Allow / Not now. Returns false when no
    /// setup window was open.
    /// </summary>
    public async Task<bool> AnswerExtensionConsentAsync(bool allow)
    {
        var answer = allow ? "allow" : "not-now";
        using var res = await _http.PostAsync($"{_controlBase}/extension-consent?answer={answer}", content: null);
        return res.StatusCode == System.Net.HttpStatusCode.NoContent;
    }

    /// <summary>
    /// Pick the tray's "Set up the Edge extension…" item (#405) via POST
    /// /extension-setup — a tray flyout can't be opened headlessly.
    /// </summary>
    public async Task OfferExtensionSetupAsync()
    {
        using var res = await _http.PostAsync($"{_controlBase}/extension-setup", content: null);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Take the paused-Edge overlay's "switch the extension back on" action (#427)
    /// via POST /extension-repair. Returns false when Edge wasn't paused.
    /// </summary>
    public async Task<bool> TurnExtensionBackOnAsync()
    {
        using var res = await _http.PostAsync($"{_controlBase}/extension-repair", content: null);
        return res.StatusCode == System.Net.HttpStatusCode.NoContent;
    }

    /// <summary>
    /// Click the focus overlay's row for the allowed app <paramref name="app"/> (a
    /// rule's value, such as <c>notepad</c>) via POST /overlay-app (#453). The click
    /// runs on the agent's UI thread, as the student's does, and this does not wait
    /// for it. Returns false when the overlay wasn't up.
    /// </summary>
    public async Task<bool> ClickOverlayAppAsync(string app)
    {
        using var res = await _http.PostAsync(
            $"{_controlBase}/overlay-app?app={Uri.EscapeDataString(app)}", content: null);
        return res.StatusCode == System.Net.HttpStatusCode.NoContent;
    }

    /// <summary>
    /// Run an update check now (#456) via POST /check-update, as the interval timer
    /// would. Does not wait for the check.
    /// </summary>
    public async Task CheckUpdateAsync()
    {
        using var res = await _http.PostAsync($"{_controlBase}/check-update", content: null);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Play time asleep (#456) via POST /simulate-sleep: the agent's update clock moves
    /// on by <paramref name="slept"/> while its timers stay put, as across real sleep.
    /// The wake that follows is sent like Windows sends it; see
    /// <see cref="WindowCapture.PostToSystemEventsWindowAsync"/>.
    /// </summary>
    public async Task SimulateSleepAsync(TimeSpan slept)
    {
        var query = $"slept={Uri.EscapeDataString(slept.ToString("c"))}";
        using var res = await _http.PostAsync($"{_controlBase}/simulate-sleep?{query}", content: null);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>Wait for the agent process to exit (e.g. after <see cref="QuitAsync"/>).</summary>
    public async Task WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try { await _process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Agent did not exit within {timeout.TotalSeconds:N0}s of quit.");
        }
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
