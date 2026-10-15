using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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
    // item (TrayMenu.MagentaSparkIcon, designed as a 7x7 DIP square) — the menu's
    // <5% magenta, deliberately a single small mark rather than a whole button. Its
    // size in a capture follows the display scale, so a fixed count on a coarse
    // sampling grid can't hold across machines: at 125% it renders 12x12 physical
    // px, which a 4px grid hits exactly 3x3 = 9 times (#415). So the spec measures
    // the spark at the capture's own scale: the largest solid patch of spark
    // magenta, found pixel by pixel, must cover at least half the designed spark's
    // physical area (7 DIP x scale, squared). Half leaves room for anti-aliased edge
    // pixels (a 7px square at a half-pixel offset still has a 6x6 solid core, 73% of
    // its area), while a missing/recoloured spark (the brand regression this guards)
    // fails, and stray magenta pixels can't add up to a pass: they aren't one patch.
    //
    // The floor is the designed 7 DIP, not today's render: the MenuFlyoutItem
    // template fits the icon into a 16x16 Viewbox, which scales the PathIcon by
    // 16/11.5 (its geometry ends at 4.5+7), so the spark currently paints ~9.7 DIP
    // (the 12px above) — that only adds margin, and the spec still holds if the
    // spark is brought back to its designed 7 DIP.
    private const double SparkSideDip = 7;
    private const double MinSparkAreaFraction = 0.5;

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
        var scale = WindowCapture.GetWindowScale(hwnd);

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
        var dark = SampleDarkFraction(shown);
        Assert.True(
            dark >= MinDarkFraction,
            $"Tray menu capture is not the ink surface: only {dark:P0} of sampled pixels are dark " +
            $"(need >= {MinDarkFraction:P0}); the capture may be the desktop or a grey OS popup. Saved: {saved}.");

        // Spark: the one magenta mark on the primary action must be present, proving
        // the real DS-tokened composition (the single spark) actually painted. The
        // threshold is the spark's own area at this capture's scale, so it holds the
        // same at 100%, 125%, 150% and 200%.
        var spark = LargestMagentaPatch(shown);
        var sparkSidePx = SparkSideDip * scale;
        var minSparkPixels = (int)Math.Ceiling(MinSparkAreaFraction * sparkSidePx * sparkSidePx);
        Assert.True(
            spark.Area >= minSparkPixels,
            $"Tray menu capture has no magenta spark: the largest solid magenta patch is {spark.Area}px " +
            $"({spark.Width}x{spark.Height}), need >= {minSparkPixels}px (half the {SparkSideDip}x{SparkSideDip} DIP " +
            $"spark at {scale:P0} scaling); the primary-action spark may not have rendered. Saved: {saved}.");
    }

    // Fraction of dark/ink pixels on a coarse grid — a ratio, so the grid step
    // doesn't depend on the display scale. Same "dark" definition as the
    // MainWindow/JoinByCode visual specs, so every ink surface is held to one notion
    // of the ink background.
    private static double SampleDarkFraction(Bitmap bmp, int gridStep = 4)
    {
        int sampled = 0, dark = 0;
        for (var y = 0; y < bmp.Height; y += gridStep)
        {
            for (var x = 0; x < bmp.Width; x += gridStep)
            {
                var c = bmp.GetPixel(x, y);
                sampled++;
                if (c.R < 70 && c.G < 70 && c.B < 80) dark++;
            }
        }
        return sampled == 0 ? 0 : (double)dark / sampled;
    }

    // Saturated magenta ≈ the DS spark (#DB2777). Same definition as the
    // MainWindow/JoinByCode visual specs, so every ink surface is held to one notion
    // of "the spark". A pixel blended mostly into the ink (an anti-aliased edge)
    // falls outside it.
    private static bool IsSparkMagenta(Color c) =>
        c.R > 150 && c.G < 110 && c.B is > 60 and < 170 &&
        c.R - c.G > 80 && c.R - c.B > 40;

    // The largest 4-connected patch of spark-magenta pixels in the capture, read at
    // every pixel (a 1px step, so a 7px spark at 100% is measured in full). Returns
    // its pixel count and bounding size; the bounding size is only for the failure
    // message.
    private static (int Area, int Width, int Height) LargestMagentaPatch(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var magenta = new bool[w * h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[w];
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, w);
                for (var x = 0; x < w; x++)
                    magenta[y * w + x] = IsSparkMagenta(Color.FromArgb(row[x]));
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        (int Area, int Width, int Height) best = (0, 0, 0);
        var pending = new Stack<int>();
        for (var start = 0; start < magenta.Length; start++)
        {
            if (!magenta[start]) continue;
            magenta[start] = false;
            pending.Push(start);
            int area = 0, minX = w, maxX = -1, minY = h, maxY = -1;
            while (pending.Count > 0)
            {
                var i = pending.Pop();
                int x = i % w, y = i / w;
                area++;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                if (x > 0 && magenta[i - 1]) { magenta[i - 1] = false; pending.Push(i - 1); }
                if (x < w - 1 && magenta[i + 1]) { magenta[i + 1] = false; pending.Push(i + 1); }
                if (y > 0 && magenta[i - w]) { magenta[i - w] = false; pending.Push(i - w); }
                if (y < h - 1 && magenta[i + w]) { magenta[i + w] = false; pending.Push(i + w); }
            }
            if (area > best.Area) best = (area, maxX - minX + 1, maxY - minY + 1);
        }
        return best;
    }
}
