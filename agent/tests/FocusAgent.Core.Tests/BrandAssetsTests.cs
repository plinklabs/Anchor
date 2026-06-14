namespace FocusAgent.Core.Tests;

/// <summary>
/// Issue #163 (AF2): the agent ships the Anchor brand assets — the WinUI
/// tile/splash/store logos and the tray icon that replaced the programmatic
/// "F". These guard that the asset set the manifest + <c>TrayIconHost</c>
/// reference actually exists on disk and (for the tray) is a well-formed
/// multi-size .ico, so the generator output can't silently regress to a missing
/// or single-size icon. Assets are produced from the mark by
/// <c>design/icons/generate.mjs</c>.
/// </summary>
public class BrandAssetsTests
{
    private static string AssetsDir =>
        Path.Combine(RepoRoot(), "agent", "src", "FocusAgent.App", "Assets");

    [Theory]
    [InlineData("Square44x44Logo.png")]
    [InlineData("Square150x150Logo.png")]
    [InlineData("Square310x310Logo.png")]
    [InlineData("Wide310x150Logo.png")]
    [InlineData("StoreLogo.png")]
    [InlineData("SplashScreen.png")]
    [InlineData("TrayIcon.ico")]
    public void Brand_asset_exists_and_is_non_empty(string fileName)
    {
        var path = Path.Combine(AssetsDir, fileName);
        Assert.True(File.Exists(path), $"Missing brand asset: {path}");
        Assert.True(new FileInfo(path).Length > 0, $"Empty brand asset: {path}");
    }

    [Fact]
    public void TrayIcon_is_a_multi_size_ico_covering_small_taskbar_sizes()
    {
        // TrayIconHost loads ms-appx:///Assets/TrayIcon.ico; a multi-size .ico
        // keeps it crisp at the 16/32 px the tray actually renders at.
        var sizes = ReadIcoSizes(Path.Combine(AssetsDir, "TrayIcon.ico"));
        Assert.Contains(16, sizes);
        Assert.Contains(32, sizes);
    }

    /// <summary>Parse the icon-directory of an .ico into its declared widths.</summary>
    private static IReadOnlyList<int> ReadIcoSizes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // ICONDIR: reserved(2)=0, type(2)=1 (icon), count(2).
        Assert.True(bytes.Length >= 6, "Truncated .ico header");
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
        int count = BitConverter.ToUInt16(bytes, 4);
        Assert.True(count > 0, ".ico declares no images");

        var sizes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            // ICONDIRENTRY width is the first byte; 0 encodes 256.
            int width = bytes[6 + i * 16];
            sizes.Add(width == 0 ? 256 : width);
        }
        return sizes;
    }

    private static string RepoRoot()
    {
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
}
