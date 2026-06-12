using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Saves the captured overlay/toast screenshots so a human can eyeball what the
/// visual specs actually saw (#133). Writes under the suite's
/// <c>TestResults/visual-artifacts/</c>, which the CI workflow already uploads
/// as the run's test-results artifact — so a flaky visual run leaves the exact
/// pixels behind for triage instead of just a stack trace.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class VisualArtifacts
{
    private static readonly string Dir = Path.Combine(
        TestConfig.RepoRoot, "agent", "tests", "FocusAgent.IntegrationTests",
        "TestResults", "visual-artifacts");

    public static string Save(Bitmap bmp, string name)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        bmp.Save(path, ImageFormat.Png);
        return path;
    }
}

/// <summary>
/// Collection for the visual-enforcement specs. They each launch an agent and
/// screenshot the screen, so they must run serially (never parallel with one
/// another) — parallel captures would fight over which surface is on top.
/// Distinct from <see cref="AgentE2ECollection"/>: the visual specs need no
/// backend, and CI runs them in a separate, non-blocking job.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VisualE2ECollection
{
    public const string Name = "visual-e2e";
}
