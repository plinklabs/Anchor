using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Asserting e2e for #526 (test phase only, #515): during a session the agent
/// reads the address bar of the school profile's foreground Edge window and tells
/// the teacher about an off-list site the extension let through, by its host
/// only. The real agent, backend and Edge: the agent reads Edge 157's own address
/// bar through UI Automation.
///
/// How the extension is kept from filtering, honestly: this Edge runs in a
/// profile of its own where no Anchor extension can act. The agent's witness link
/// is up (a fake extension on a private pipe says it is there and fine), and Edge
/// runs with its native-messaging pipe pointed nowhere, so an Anchor extension an
/// Edge policy installs on the box (a developer's test-ring build) never gets a
/// session and filters nothing; CI's runner has none. That is the silent gap the
/// second witness is for: the agent believes the extension is filtering, and Edge
/// shows an off-list site. Taking "Site access" away doesn't make one in Edge 157:
/// with it on "On click" the extension still redirected an off-list page (#524),
/// and a policy install can't change it.
///
/// The agent is told the Edge profile is the school profile through a
/// <c>User Data</c> folder of the spec's own (<c>Local State</c> naming the
/// student's account, as in <see cref="SchoolProfileRun"/>), with the same folder
/// name as the one Edge runs in, so Edge's windows carry the AppUserModelID of its
/// Default profile. A folder name of its own keeps the developer's own Edge
/// profiles out of the rule.
///
/// Which window has the foreground is what the agent reads, so this runs in the
/// non-blocking Visual lane, like <see cref="SchoolProfileOverlayTests"/>.
/// </summary>
[Trait("Category", "Visual")]
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class AddressBarWitnessTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

    private readonly BackendFixture _backend;
    public AddressBarWitnessTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task An_off_list_site_Edge_shows_unfiltered_reaches_the_teacher_by_its_host_and_nothing_else_does()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await AddressBarRun.StartAsync(_backend.Url, api, Grace);

        // 1. An allowed page (127.0.0.1 is on every Development session's list).
        await run.ShowAsync(run.Site.Url("127.0.0.1", "/allowed?q=allowed+search"));
        var reading = await run.WaitForAsync(
            s => s.AddressBar is { Watching: true, Reads: > 0 }, TimeSpan.FromSeconds(10),
            "the agent to read the address bar of the school profile's Edge window");
        await run.HoldAsync(Grace + TimeSpan.FromSeconds(3));
        Assert.Equal(0, await run.CountAsync("127.0.0.1"));

        // 2. The block page, what the student sees when the extension does block.
        await run.ShowAsync(
            "chrome-extension://dnkimhodjfogjibnbbfdjdapgmmiojio/block-page.html?url=http%3A%2F%2Fofflist.test%2F");
        await run.HoldAsync(Grace + TimeSpan.FromSeconds(3));

        // 3. An off-list page that is gone within the grace, as one the extension
        //    redirects: it moves on to the allowed page after 300 ms.
        await run.ShowAsync(run.Site.Url("flash.test", "/flash"), waitForPage: false);
        await run.HoldAsync(Grace + TimeSpan.FromSeconds(3));
        Assert.Equal(0, await run.CountAsync("flash.test"));

        // 4. An off-list page left open: the teacher hears of it once, by its host.
        //    No URL, path, query string or port leaves the device.
        await run.ShowAsync(run.Site.Url("offlist.test", "/secret/path?q=private+search"));
        Assert.True(
            await run.WaitForReportsAsync("offlist.test", 1, Grace + TimeSpan.FromSeconds(10)),
            "An off-list page stayed open in the school profile's Edge window past the grace period, " +
            "but the teacher was not told (once).");
        var report = (await run.ReportsAsync()).Single(r => HostOf(r) == "offlist.test");
        Assert.Equal("""{"host":"offlist.test"}""", report.PayloadJson);
        await run.HoldAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(1, await run.CountAsync("offlist.test"));
        // Nothing for the allowed page, the block page or the page that moved on.
        var hosts = (await run.ReportsAsync()).Select(HostOf).ToList();
        Assert.DoesNotContain("127.0.0.1", hosts);
        Assert.DoesNotContain("flash.test", hosts);
        Assert.DoesNotContain(hosts, h => h is null || h.Contains("dnkimhodjfogjibnbbfdjdapgmmiojio"));
        Assert.Contains("offlist.test", (await run.Agent.TryGetStatusAsync())?.AddressBar?.Reported ?? []);

        // 5. The session ends: nothing is read any more, with Edge still in front
        //    on an off-list page.
        await run.EndSessionAsync();
        var ended = await run.WaitForAsync(
            s => s.JoinedSessionId is null && s.AddressBar is { Watching: false }, TimeSpan.FromSeconds(10),
            "the session to end on the agent");
        await run.ShowAsync(run.Site.Url("after.test", "/after"));
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.Equal(ended!.AddressBar!.Reads, (await run.Agent.TryGetStatusAsync())?.AddressBar?.Reads);
        Assert.True(reading!.AddressBar!.Reads > 0);
    }

    private static string? HostOf(BackendClient.SessionEvent report)
    {
        using var doc = JsonDocument.Parse(report.PayloadJson);
        return doc.RootElement.TryGetProperty("host", out var host) ? host.GetString() : null;
    }
}

/// <summary>
/// One run of the address-bar witness (#526): the real agent with a fake extension
/// connected, a <c>User Data</c> folder of the spec's own in which the Default
/// profile is the school profile, a joined session, a local web server and a real
/// Edge in a profile of its own whose windows are that Default profile's.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AddressBarRun : IAsyncDisposable
{
    public const string SchoolAddress = "student@school.test";
    // The same folder name for the agent's stand-in and for Edge's own: Edge names
    // its Default profile's windows after it (MSEdge.AnchorE2EUserData.Default).
    private const string UserDataName = "AnchorE2E User Data";
    private const string EdgeAppId = "MSEdge.AnchorE2EUserData.Default";
    private const string StoreExtensionId = "dnkimhodjfogjibnbbfdjdapgmmiojio";

    private readonly BackendClient _api;
    private readonly string _root;
    private Guid? _sessionId;
    private AgentProcess? _agent;
    private FakeExtensionWitness? _extension;
    private string? _edgeExe;
    private Process? _edgeLauncher;
    private Process? _edgeBrowser;

    private AddressBarRun(BackendClient api, string root, LoopbackSite site)
    {
        _api = api;
        _root = root;
        Site = site;
    }

    public AgentProcess Agent => _agent ?? throw new InvalidOperationException("Agent not launched.");
    public LoopbackSite Site { get; }
    private string AgentUserData => Path.Combine(_root, "agent", UserDataName);
    private string EdgeUserData => Path.Combine(_root, "edge", UserDataName);

    public static async Task<AddressBarRun> StartAsync(string backendUrl, BackendClient api, TimeSpan grace)
    {
        var stale = await api.GetRejoinableSessionIdsAsync();
        Assert.True(
            stale.Count == 0,
            $"The student is still in running session(s) {string.Join(", ", stale)} that an earlier spec left " +
            "behind; this agent would rejoin them on connect. End every session a spec starts.");
        var edgeExe = FindEdge();
        Assert.True(edgeExe is not null, "Microsoft Edge is not installed; this spec reads its address bar.");

        var run = new AddressBarRun(
            api, Path.Combine(Path.GetTempPath(), "anchor-e2e-addressbar-" + Guid.NewGuid().ToString("N")), LoopbackSite.Start());
        try
        {
            run.WriteSchoolProfile();

            var pipeName = "anchor-witness-e2e-" + Guid.NewGuid().ToString("N");
            run._agent = AgentProcess.Launch(
                backendUrl,
                TestConfig.StudentOid,
                autoJoin: true,
                extraEnvironment: new Dictionary<string, string>
                {
                    ["Witness__PipeName"] = pipeName,
                    ["Witness__BrowserProcessName"] = "msedge",
                    ["Witness__BrowserUserDataFolder"] = run.AgentUserData,
                    ["Dev__ImpersonateUsername"] = SchoolAddress,
                    ["Session__SchoolProfileScanSeconds"] = "1",
                    ["Session__AddressBarScanSeconds"] = "1",
                    ["Session__AddressBarGraceSeconds"] = ((int)grace.TotalSeconds).ToString(),
                });
            await run.Agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

            // The agent hears from "the extension" and counts it as there and fine.
            run._extension = new FakeExtensionWitness(pipeName);
            await run._extension.ConnectAsync();
            await run.WaitForAsync(s => s.ExtensionGuard?.WitnessConnected == true, TimeSpan.FromSeconds(10), "the witness to connect");

            await run.LaunchEdgeAsync(edgeExe!, run.Site.Url("127.0.0.1", "/allowed"));

            var classId = await api.FindClassIdAsync();
            var session = await api.StartSessionAsync(classId);
            run._sessionId = session.Id;
            await run.WaitForAsync(
                s => s.JoinedSessionId == session.Id && s.SchoolProfile is { Status: "Found", Profile: "Default" },
                TimeSpan.FromSeconds(15), "the session to be joined with the school profile found");
            return run;
        }
        catch
        {
            await run.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Opens <paramref name="url"/> in a new tab of the spec's Edge, as typing it
    /// would, and gives that Edge window the foreground; with
    /// <paramref name="waitForPage"/>, once its title says the page is up (the
    /// spec's pages are titled after their host).
    /// </summary>
    public async Task ShowAsync(string url, bool waitForPage = true)
    {
        if (_edgeExe is null || _edgeBrowser is null || _edgeBrowser.HasExited)
            throw new InvalidOperationException("The spec's Edge is not running.");
        // Hands the address to the running Edge, which opens it in a new tab.
        using (var opener = StartEdge(_edgeExe, url))
            await opener.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        var expected = waitForPage && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.Host
            : null;
        while (DateTime.UtcNow < deadline)
        {
            var window = FindEdgeWindow();
            if (window != IntPtr.Zero && (expected is null || TitleOf(window).StartsWith(expected, StringComparison.OrdinalIgnoreCase)))
            {
                if (WindowCapture.ForegroundWindow() == window || WindowCapture.BringToForeground(window))
                    return;
            }
            await Task.Delay(200);
        }
        var last = FindEdgeWindow();
        Assert.Fail(
            $"Edge did not show {url} in front (window: " +
            $"{(last == IntPtr.Zero ? "<none>" : $"'{TitleOf(last)}', {WindowCapture.DescribePlacement(last)}")}, " +
            $"foreground: {WindowCapture.GetProcessName(WindowCapture.ForegroundWindow())}). " +
            "Run again without touching the desktop.");
    }

    /// <summary>Keeps the spec's Edge window in front for <paramref name="duration"/>.</summary>
    public async Task HoldAsync(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            var window = FindEdgeWindow();
            if (window != IntPtr.Zero && WindowCapture.ForegroundWindow() != window)
                WindowCapture.BringToForeground(window);
            await Task.Delay(250);
        }
    }

    /// <summary>The session's UnfilteredSite events, as the teacher's dashboard gets them.</summary>
    public async Task<List<BackendClient.SessionEvent>> ReportsAsync() =>
        (await _api.GetSessionEventsAsync(_sessionId ?? throw new InvalidOperationException("No session.")))
        .Where(e => e.Kind == "UnfilteredSite")
        .ToList();

    public async Task<int> CountAsync(string host) =>
        (await ReportsAsync()).Count(e => e.PayloadJson.Contains($"\"{host}\"", StringComparison.Ordinal));

    public async Task<bool> WaitForReportsAsync(string host, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await CountAsync(host) == expected) return true;
            await HoldAsync(TimeSpan.FromMilliseconds(500));
        }
        return await CountAsync(host) == expected;
    }

    /// <summary>Poll /status until it matches, or fail naming what was awaited.</summary>
    public async Task<StatusSnapshot?> WaitForAsync(Func<StatusSnapshot, bool> predicate, TimeSpan timeout, string what)
    {
        var last = await Agent.WaitForAsync(predicate, timeout);
        Assert.True(
            last is not null && predicate(last),
            $"Timed out after {timeout.TotalSeconds:N0}s waiting for {what} " +
            $"(joined: {last?.JoinedSessionId?.ToString() ?? "<none>"}, " +
            $"school profile: {last?.SchoolProfile?.Status ?? "<none>"} ({last?.SchoolProfile?.Profile ?? "none"}), " +
            $"guard: {last?.ExtensionGuard?.State ?? "<none>"}, " +
            $"address bar: watching {last?.AddressBar?.Watching}, {last?.AddressBar?.Reads} reads, " +
            $"foreground: {WindowCapture.GetProcessName(WindowCapture.ForegroundWindow())}).");
        return last;
    }

    public async Task EndSessionAsync()
    {
        if (_sessionId is { } id)
            await _api.EndSessionAsync(id);
    }

    private async Task LaunchEdgeAsync(string edgeExe, string url)
    {
        Directory.CreateDirectory(EdgeUserData);
        _edgeExe = edgeExe;
        _edgeLauncher = StartEdge(edgeExe, url, PrimaryMonitorWindowArguments());
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (FindEdgeWindow() == IntPtr.Zero && DateTime.UtcNow < deadline)
            await Task.Delay(200);
        var window = FindEdgeWindow();
        Assert.True(window != IntPtr.Zero, "Edge opened no window of its Default profile within 30 s.");
        // Edge may start over in a process of its own (it relaunched itself from
        // the test host, --edge-skip-compat-layer-relaunch): the browser is the
        // process that shows the window.
        _edgeBrowser = Process.GetProcessById(WindowCapture.ProcessIdOf(window));
        Assert.True(
            await WindowCapture.PutOnPrimaryMonitorAsync(window),
            $"The spec's Edge window could not be put on screen on the primary monitor ({WindowCapture.DescribePlacement(window)}).");
    }

    /// <summary>
    /// Where Edge opens its window: inside the primary monitor's work area (#542).
    /// Left to itself it opens where Windows puts it, which can be a second screen
    /// someone works on while the suite runs, where their clicks cover or minimize
    /// it. Edge reads these in DIPs, and the primary monitor's top left is (0, 0)
    /// in DIPs and pixels alike.
    /// </summary>
    private static string[] PrimaryMonitorWindowArguments()
    {
        const int margin = 50;
        var (work, scale) = WindowCapture.GetPrimaryWorkArea();
        var left = (int)(work.Left / scale) + margin;
        var top = (int)(work.Top / scale) + margin;
        var width = Math.Min(1280, (int)(work.Width / scale) - 2 * margin);
        var height = Math.Min(900, (int)(work.Height / scale) - 2 * margin);
        return [$"--window-position={left},{top}", $"--window-size={width},{height}"];
    }

    private Process StartEdge(string edgeExe, string url, IEnumerable<string>? windowArguments = null)
    {
        var psi = new ProcessStartInfo(edgeExe) { UseShellExecute = false };
        psi.ArgumentList.Add($"--user-data-dir={EdgeUserData}");
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");
        psi.ArgumentList.Add("--disable-sync");
        psi.ArgumentList.Add("--lang=en-US");
        psi.ArgumentList.Add($"--host-resolver-rules={string.Join(",", LoopbackSite.Hosts.Select(h => $"MAP {h} 127.0.0.1"))}");
        foreach (var argument in windowArguments ?? [])
            psi.ArgumentList.Add(argument);
        psi.ArgumentList.Add(url);
        // An Anchor extension Edge policy installs here connects its native host to
        // this pipe, where no agent listens: it never gets a session (#489).
        psi.Environment["ANCHOR_WITNESS_PIPE"] = "anchor-witness-e2e-nobody-" + Guid.NewGuid().ToString("N");
        return Process.Start(psi) ?? throw new InvalidOperationException("Edge did not start.");
    }

    /// <summary>
    /// The spec's Edge's browser window that was in front last, or 0: a window
    /// with the AppUserModelID Edge gives its Default profile's windows in this
    /// <c>User Data</c> folder, which no other Edge on the box has.
    /// </summary>
    private static IntPtr FindEdgeWindow()
    {
        // EnumWindows lists top-level windows from the top of the z-order down.
        foreach (var window in WindowCapture.TopLevelWindows())
        {
            if (!WindowCapture.IsVisible(window)) continue;
            if (!WindowCapture.GetClassName(window).StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal)) continue;
            if (string.Equals(WindowAppId.Get(window), EdgeAppId, StringComparison.OrdinalIgnoreCase))
                return window;
        }
        return IntPtr.Zero;
    }

    private static string TitleOf(IntPtr window) => WindowCapture.GetWindowText(window);

    private void WriteSchoolProfile()
    {
        var cache = new JsonObject
        {
            ["Default"] = new JsonObject
            {
                ["name"] = "School",
                ["user_name"] = SchoolAddress,
                ["edge_account_oid"] = TestConfig.StudentOid,
                ["edge_account_type"] = 2,
            },
        };
        Directory.CreateDirectory(Path.Combine(AgentUserData, "Default"));
        File.WriteAllText(
            Path.Combine(AgentUserData, "Local State"),
            new JsonObject { ["profile"] = new JsonObject { ["info_cache"] = cache } }.ToJsonString());
        var entry = new JsonObject
        {
            ["location"] = 1,
            ["path"] = $"{StoreExtensionId}\\0.5.0_0",
            ["disable_reasons"] = new JsonArray(),
        };
        File.WriteAllText(
            Path.Combine(AgentUserData, "Default", "Secure Preferences"),
            new JsonObject
            {
                ["extensions"] = new JsonObject { ["settings"] = new JsonObject { [StoreExtensionId] = entry } },
            }.ToJsonString());
    }

    private static string? FindEdge()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
        if (key?.GetValue(null) is string registered && File.Exists(registered)) return registered;
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                 })
        {
            var path = Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sessionId is { } id)
        {
            try { await _api.EndSessionAsync(id); } catch { /* best-effort; may have ended already */ }
        }
        if (_extension is not null)
            await _extension.DisposeAsync();
        if (_agent is not null)
            await _agent.DisposeAsync();
        foreach (var edge in new[] { _edgeBrowser, _edgeLauncher })
        {
            if (edge is null) continue;
            try { edge.Kill(entireProcessTree: true); } catch { /* gone already */ }
            try { await edge.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* best-effort */ }
            edge.Dispose();
        }
        await Site.DisposeAsync();
        for (var attempt = 0; attempt < 5 && Directory.Exists(_root); attempt++)
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { await Task.Delay(500); /* Edge lets go of its profile a moment after it exits */ }
        }
    }
}

/// <summary>
/// A web server on loopback that answers for any host name (Edge maps the spec's
/// hosts to it with <c>--host-resolver-rules</c>): <c>/flash</c> moves on to the
/// allowed page after 300 ms, as a page the extension redirects; anything else is
/// a plain page. Each is titled after its host and path.
/// </summary>
internal sealed class LoopbackSite : IAsyncDisposable
{
    public static readonly string[] Hosts = ["offlist.test", "flash.test", "after.test"];

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private LoopbackSite(TcpListener listener)
    {
        _listener = listener;
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Url(string host, string pathAndQuery) => $"http://{host}:{Port}{pathAndQuery}";

    public static LoopbackSite Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackSite(listener);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(_stop.Token) ?? "";
                var host = "";
                for (var line = await reader.ReadLineAsync(_stop.Token); !string.IsNullOrEmpty(line);
                     line = await reader.ReadLineAsync(_stop.Token))
                {
                    if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                        host = line[5..].Trim().Split(':')[0];
                }
                var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
                // Titled after the host, which Edge puts in front of its window title.
                var title = WebUtility.HtmlEncode($"{host} {path.Split('?')[0].Trim('/')}");
                var body = path.StartsWith("/flash", StringComparison.Ordinal)
                    ? $"<html><head><title>{title}</title><script>setTimeout(function () {{ location.replace('{Url("127.0.0.1", "/allowed?from=flash")}'); }}, 300);</script></head><body>Moving on</body></html>"
                    : $"<html><head><title>{title}</title></head><body>Anchor e2e page</body></html>";
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n" +
                    "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, _stop.Token);
                await stream.WriteAsync(bytes, _stop.Token);
            }
            catch
            {
                // A browser that hangs up early (or speaks TLS first) gets nothing.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop; } catch { /* stopping */ }
        _stop.Dispose();
    }
}
