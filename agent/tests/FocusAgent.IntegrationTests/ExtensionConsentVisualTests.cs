using System.Drawing;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Visual e2e for the #405 Edge extension setup window — the agent's explanation
/// before the one UAC prompt. Drives <c>--show-test-extension-consent</c>, which
/// shows the real <c>ExtensionPolicyConsentWindow</c> with nothing wired to its
/// answer (no elevation can happen), then asserts on the on-screen pixels: the DS
/// ink surface, and the one magenta "Allow" spark in the bottom row — which also
/// proves the longer explanation (the UAC step plus Edge's "managed by your
/// organization" notice) fits without pushing the buttons out of the window, in
/// both English and the longer Dutch copy. The flow behind the window is covered by
/// <see cref="ExtensionPolicySetupTests"/>.
/// </summary>
[Trait("Category", "Visual")]
[Collection(VisualE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ExtensionConsentVisualTests
{
    // Same thresholds as the guided-install spec, the window this one sits next to.
    private const int MinDistinctColors = 8;
    private const double MinDarkFraction = 0.60;
    private const int MinMagentaPixels = 20;

    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    [Theory]
    [InlineData("en-US", "Anchor — Set up the extension")]
    [InlineData("nl-NL", "Anchor — De extensie instellen")]
    public async Task ExtensionConsentSelfTest_RendersTheInkDialogWithItsAllowButton(string language, string title)
    {
        await using var agent = AgentSelfTestProcess.Launch(
            AgentSelfTestProcess.ShowTestExtensionConsentArg, extraArgs: ["--ui-language", language]);

        // By its title, so no other ink window (the guided install, say) can stand in.
        var hwnd = await WaitForTitledWindowAsync(agent, title, TimeSpan.FromSeconds(15));
        await Task.Delay(SettleTime);
        var rect = WindowCapture.GetWindowScreenRect(hwnd);

        using var shown = WindowCapture.CaptureRect(rect);
        var saved = VisualArtifacts.Save(shown, $"extension-consent-{language}");

        var colors = WindowCapture.DistinctColorCount(shown);
        Assert.True(
            colors >= MinDistinctColors,
            $"Setup window capture looks blank: only {colors} distinct colours " +
            $"(need >= {MinDistinctColors}) over {shown.Width}x{shown.Height}px. Saved: {saved}.");

        var (dark, magenta) = SampleSurface(shown);
        Assert.True(
            dark >= MinDarkFraction,
            $"Setup window capture is not the ink surface: only {dark:P0} of sampled pixels are dark " +
            $"(need >= {MinDarkFraction:P0}); the capture may be the desktop. Saved: {saved}.");

        Assert.True(
            magenta >= MinMagentaPixels,
            $"Setup window capture has no magenta spark: only {magenta} magenta pixels sampled " +
            $"(need >= {MinMagentaPixels}); the ALLOW button may not have rendered or been clipped. Saved: {saved}.");
    }

    private static async Task<IntPtr> WaitForTitledWindowAsync(AgentSelfTestProcess agent, string title, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hwnd = WindowCapture.FindVisibleWindow(agent.Pid, "WinUIDesktop", title);
            if (hwnd != IntPtr.Zero) return hwnd;
            if (agent.HasExited)
                throw new InvalidOperationException($"The agent exited before showing '{title}'.");
            await Task.Delay(100);
        }
        throw new TimeoutException(
            $"No '{title}' window within {timeout.TotalSeconds:N0}s. Windows:\n{WindowCapture.DescribeWindows(agent.Pid)}");
    }

    // Same grid sampling as the other ink-surface visual specs (#DB2777 = the spark).
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
