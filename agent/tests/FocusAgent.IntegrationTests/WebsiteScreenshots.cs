using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Curated screenshot generator for the Anchor website (#251). This is the agent
/// counterpart to the dashboard's <c>generate-screenshots.mjs</c>: it drives the
/// real student-facing WinUI surfaces — the join toast, the app-block overlay,
/// the agent main window, and the tray menu — through the same
/// <see cref="AgentSelfTestProcess"/> + <see cref="WindowCapture"/> path the
/// visual-enforcement specs use, and writes a fixed, named PNG set straight into
/// <c>website/assets/</c> (not the timestamped <c>TestResults/</c> triage dump
/// the <see cref="VisualArtifacts"/> helper produces).
///
/// The surfaces render presentable demo content (a real teacher "Ms Rivera",
/// class code <c>PLINK-3B</c>, a readable allowlist — see
/// <c>FocusAgent.App.SelfTestDemoContent</c>), matched to the dashboard's demo
/// data so the agent and dashboard shots tell one coherent story. No backend, no
/// real auth, no secrets.
///
/// It is <em>not</em> part of the routine Visual suite — it's an opt-in generator
/// gated on <see cref="EnableEnvVar"/> so a normal <c>--filter Category=Visual</c>
/// run never overwrites the committed PNGs. Run it via
/// <c>scripts/dev/generate-website-screenshots.ps1</c>, which builds the agent
/// and sets the gate. See <c>website/assets/README.md</c>.
/// </summary>
[Trait("Category", "WebsiteScreenshots")]
[Collection(VisualE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class WebsiteScreenshots
{
    /// <summary>
    /// Set to <c>1</c> to actually run the generator. Off by default so the
    /// committed website PNGs are only ever (re)written deliberately, never as a
    /// side effect of the visual e2e suite.
    /// </summary>
    public const string EnableEnvVar = "ANCHOR_WEBSITE_SHOTS";

    // The WinUI surfaces need a beat after their HWND appears to paint and raise
    // to topmost before the screen DC reads real pixels — same dwell the visual
    // specs and verify scripts use.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    // Where the curated shots land: the website's co-located assets dir (already
    // holds the dashboard-*.png set). Deterministic, repo-relative, not buried in
    // TestResults/.
    private static string AssetsDir => Path.Combine(TestConfig.RepoRoot, "website", "assets");

    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnableEnvVar), "1", StringComparison.Ordinal);

    [Fact]
    public async Task JoinToast() =>
        await CaptureSurfaceAsync(AgentSelfTestProcess.ShowTestToastArg, "agent-join-toast");

    [Fact]
    public async Task BlockOverlay() =>
        await CaptureSurfaceAsync(AgentSelfTestProcess.ShowTestOverlayArg, "agent-block-overlay");

    [Fact]
    public async Task MainWindow() =>
        await CaptureSurfaceAsync(AgentSelfTestProcess.ShowTestMainWindowArg, "agent-main-window");

    [Fact]
    public async Task TrayMenu() =>
        await CaptureSurfaceAsync(AgentSelfTestProcess.ShowTestTrayMenuArg, "agent-tray-menu");

    /// <summary>
    /// Launch the agent in the given self-test mode, wait for its surface to paint
    /// and settle, capture its window rect, and write a single named PNG into
    /// <see cref="AssetsDir"/>. The capture path is identical to the visual specs
    /// (find HWND for our PID, BitBlt+CAPTUREBLT the rect under per-monitor DPI
    /// awareness) — so it grabs the same real DirectComposition pixels, just into a
    /// curated file instead of a timestamped triage dump.
    /// </summary>
    private static async Task CaptureSurfaceAsync(string selfTestFlag, string fileName)
    {
        // Opt-in only: a routine run (which never sets the gate) is a no-op, so the
        // committed PNGs are only ever (re)written deliberately — never overwritten
        // as a side effect of another test pass. The Category=WebsiteScreenshots
        // trait already keeps it out of the Category=Visual filter; this is the
        // belt-and-braces guard for an unfiltered run.
        if (!Enabled) return;

        Directory.CreateDirectory(AssetsDir);
        var outPath = Path.Combine(AssetsDir, $"{fileName}.png");

        await using var agent = AgentSelfTestProcess.Launch(selfTestFlag);

        var hwnd = await agent.WaitForWindowAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(SettleTime);

        // Park the pointer out of frame so no stray hover tooltip (e.g. the title
        // bar's "Close" tip) bleeds into a shipped image; gives the surface a beat
        // to drop the tooltip before the read.
        WindowCapture.ParkCursor();
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        var rect = WindowCapture.GetWindowScreenRect(hwnd);

        using var shot = WindowCapture.CaptureRect(rect);

        // Guard against a blank/desktop capture so a broken render never silently
        // ships as a website asset — the same "not blank" lever the visual specs use.
        var colors = WindowCapture.DistinctColorCount(shot);
        Assert.True(
            colors >= 8,
            $"{fileName} capture looks blank: only {colors} distinct colours over " +
            $"{shot.Width}x{shot.Height}px — refusing to write a broken website asset.");

        shot.Save(outPath, ImageFormat.Png);
    }
}
