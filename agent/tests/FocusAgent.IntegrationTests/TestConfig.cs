using System.Net.Sockets;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Shared constants + path resolution for the headless agent e2e harness
/// (#131). The harness drives the <em>real</em> agent exe against a
/// <em>real</em> backend with session lifecycle pushed over REST + SignalR — no
/// mocks, no stubbed hub. These values pin the seeded dev identities / class /
/// bundles the backend creates (see
/// <c>backend/src/Anchor.Infrastructure/Persistence/DevDataSeeder.cs</c>) and
/// the build outputs the harness launches.
/// </summary>
internal static class TestConfig
{
    // --- Seeded dev identities (mirror DevDataSeeder) ---------------------
    public const string TeacherOid = "11111111-1111-1111-1111-111111111111";
    public const string StudentOid = "22222222-2222-2222-2222-222222222222";

    /// <summary>Seeded student enrolled in NO class — only reachable by code.</summary>
    public const string OutsiderOid = "33333333-3333-3333-3333-333333333333";

    public const string ClassName = "3A";

    /// <summary>
    /// App-bearing bundle (see DevDataSeeder). Its expansion grants the
    /// <see cref="AppProcessName"/> app, which the agent's matcher reports via
    /// /status.allowedApps — the lever the bundle-switch spec pulls.
    /// </summary>
    public const string AppBundleName = "Notepad (dev)";
    public const string AppProcessName = "notepad";

    /// <summary>
    /// Dedicated e2e backend port — deliberately NOT the dev default (5276) so a
    /// running dev backend (and its real anchor.dev.db) is never reused or
    /// polluted. Also one above the extension harness's 5281 so both suites can
    /// run side by side. Override with ANCHOR_AGENT_E2E_BACKEND_PORT.
    /// </summary>
    public static int BackendPort =>
        int.TryParse(Environment.GetEnvironmentVariable("ANCHOR_AGENT_E2E_BACKEND_PORT"), out var p)
            ? p
            : 5282;

    public static string BackendUrl => $"http://127.0.0.1:{BackendPort}";

    /// <summary>
    /// Throwaway SQLite file for the e2e backend, under the OS temp dir so it
    /// never lands in the repo. <see cref="BackendProcess"/> deletes it (+
    /// -wal/-shm) before each boot so every run starts from a freshly-seeded
    /// schema — sidesteps the dev-DB schema-drift footgun
    /// (EnsureCreatedAsync doesn't migrate).
    /// </summary>
    public static string BackendDbPath =>
        Path.Combine(Path.GetTempPath(), "anchor-agent-e2e.db");

    /// <summary>Repository root, walked up from the test assembly location.</summary>
    public static string RepoRoot { get; } = ResolveRepoRoot();

    /// <summary>The backend project the harness boots.</summary>
    public static string BackendProject =>
        Path.Combine(RepoRoot, "backend", "src", "Anchor.Api");

    /// <summary>
    /// The built agent exe the harness launches. The CI workflow and the dev
    /// loop build it (x64 Debug) before the suite runs.
    /// </summary>
    public static string AgentExe =>
        Path.Combine(
            RepoRoot, "agent", "src", "FocusAgent.App", "bin", "x64", "Debug",
            "net10.0-windows10.0.19041.0", "FocusAgent.App.exe");

    private static string ResolveRepoRoot()
    {
        // Walk up from the test binary until we see the repo's top-level
        // marker dirs (agent/ + backend/ side by side).
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "agent")) &&
                Directory.Exists(Path.Combine(dir, "backend")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "Could not locate the repo root (no ancestor dir contains both 'agent' and 'backend').");
    }

    /// <summary>
    /// Grab a free loopback TCP port by binding to :0 and reading what the OS
    /// assigned, then releasing it. Used to give each test its own agent
    /// /status port so concurrent-ish launches never collide.
    /// </summary>
    public static int FreeLoopbackPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
