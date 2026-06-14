using System.Drawing;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Visual e2e for the redesigned agent tray context menu (AA4, #176). Drives the
/// agent's <c>--show-test-traymenu</c> self-test, which builds the real tray menu
/// via the shared <c>TrayMenu</c> factory (the same one <c>TrayIconHost</c> uses)
/// and shows it open over a small ink host window with no backend, then asserts on
/// the actual on-screen surface via screenshot capture — the layer a unit test
/// structurally can't reach: that the real WinUI composition (DS ink treatment,
/// bundled Space Mono status eyebrow + Hanken actions, hairline rules, the one
/// magenta spark on "Open Anchor") paints as the calm ink surface, not the OS
/// dark-theme grey popup the issue replaces.
///
/// A tray <c>MenuFlyout</c> is a popup, not a window, and a headless run can't click
/// the tray to open it — so the self-test (added in this PR) is how the menu is
/// reached end-to-end. The menu's commands/enablement are pure C# covered by the
/// TrayIconHost wiring; this spec covers the brand rendering those tests stub out —
/// the composition + font + token bugs that only show in a full-app run.
/// </summary>
[Trait("Category", "Visual")]
[Collection(VisualE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class TrayMenuVisualTests
{
    // A rendered ink menu carries real colour variety: the ink surface, the on-ink
    // Hanken actions, the muted Space Mono status eyebrow, the magenta spark, the
    // hairline separators. A blank/failed render is a flat fill (~1 colour). 8
    // separates the two with a wide margin — matches the other visual specs.
    private const int MinDistinctColors = 8;

    // The menu (and its ink host window) is the DS ink surface (#FF1B1B23). Most of
    // the captured rect is that ink, so it must be dominated by dark pixels — a
    // capture of a typical (light) desktop behind a window that never showed would
    // not be. >=60% leaves slack for the menu chrome + on-ink text while still
    // failing hard on a non-ink/background capture. Matches MainWindow/JoinByCode.
    private const double MinDarkFraction = 0.60;

    // The magenta spark is the small filled square marking the primary "Open Anchor"
    // item — the menu's <5% magenta, deliberately a single small mark rather than a
    // whole button. A locally-captured render samples ~20 such pixels on the 4px
    // grid; require >=10 so a missing/recoloured spark (the brand regression this
    // guards) fails, with margin for sub-pixel jitter. Lower than the window specs'
    // 20 because the tray spark is a dot, not a full magenta button.
    private const int MinMagentaPixels = 10;

    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task TrayMenuSelfTest_RendersTheInkMenu()
    {
        await using var agent = AgentSelfTestProcess.Launch(AgentSelfTestProcess.ShowTestTrayMenuArg);

        var hwnd = await agent.WaitForWindowAsync(TimeSpan.FromSeconds(15));
        // The flyout opens ~400ms after the host window appears (and re-shows on a
        // timer); the settle covers the first open landing over the captured rect.
        await Task.Delay(SettleTime);
        var rect = WindowCapture.GetWindowScreenRect(hwnd);

        using var shown = WindowCapture.CaptureRect(rect);
        var saved = VisualArtifacts.Save(shown, "traymenu");

        // Render: the redesigned menu must paint real content (not blank).
        var colors = WindowCapture.DistinctColorCount(shown);
        Assert.True(
            colors >= MinDistinctColors,
            $"Tray menu capture looks blank: only {colors} distinct colours " +
            $"(need >= {MinDistinctColors}) over {shown.Width}x{shown.Height}px. Saved: {saved}.");

        // Ink treatment: the surface must be dominated by the DS ink background,
        // proving we captured the actual ink menu — not the (light) desktop behind a
        // window that never showed (AF4 / ANCHOR_BRAND.md §3) and not the OS
        // dark-theme grey popup this issue replaces.
        var (dark, magenta) = SampleSurface(shown);
        Assert.True(
            dark >= MinDarkFraction,
            $"Tray menu capture is not the ink surface: only {dark:P0} of sampled pixels are dark " +
            $"(need >= {MinDarkFraction:P0}); the capture may be the desktop or a grey OS popup. Saved: {saved}.");

        // Spark: the one magenta mark on the primary action must be present, proving
        // the real DS-tokened composition (the single spark) actually painted.
        Assert.True(
            magenta >= MinMagentaPixels,
            $"Tray menu capture has no magenta spark: only {magenta} magenta pixels sampled " +
            $"(need >= {MinMagentaPixels}); the primary-action spark may not have rendered. Saved: {saved}.");
    }

    // Sample the capture on a coarse grid for (fraction of dark/ink pixels, count of
    // saturated-magenta pixels). Dark ≈ the ink background; magenta ≈ the DS spark
    // (#DB2777). Same thresholds/definition as MainWindow/JoinByCode visual specs,
    // so every ink surface is held to one notion of "dark" and "the spark".
    private static (double DarkFraction, int MagentaCount) SampleSurface(Bitmap bmp, int gridStep = 4)
    {
        int sampled = 0, dark = 0, magenta = 0;
        for (var y = 0; y < bmp.Height; y += gridStep)
        {
            for (var x = 0; x < bmp.Width; x += gridStep)
            {
                var c = bmp.GetPixel(x, y);
                sampled++;
                if (c.R < 70 && c.G < 70 && c.B < 80) dark++;
                if (c.R > 150 && c.G < 110 && c.B is > 60 and < 170 &&
                    c.R - c.G > 80 && c.R - c.B > 40) magenta++;
            }
        }
        return (sampled == 0 ? 0 : (double)dark / sampled, magenta);
    }
}
