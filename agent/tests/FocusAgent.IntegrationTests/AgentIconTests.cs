using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end coverage for #414: the agent shows the Anchor mark, not the generic
/// Windows "blank app" icon, wherever Windows draws an icon for it.
/// <list type="bullet">
///   <item>The built <c>FocusAgent.App.exe</c> carries the icon as a Win32 resource,
///   which Explorer and Task Manager show.</item>
///   <item>Every agent window, launched through its self-test, answers
///   <c>WM_GETICON</c> with the Anchor mark: the small icon is the one in its title
///   bar, the big one the one on its taskbar button and in Alt+Tab. A window that
///   never set an icon answers with none, and Windows then draws the default.</item>
///   <item>The crash dialog, the surface of last resort, still opens when the icon
///   file is missing from the install.</item>
/// </list>
///
/// It reads the icons from the windows and the exe and checks their pixels for the
/// on-ink indigo of the mark (#7E80D2), rather than screenshotting a title bar, so it
/// doesn't depend on the theme or on what the desktop shows. That holds on a headless
/// runner, so the spec runs in the blocking suite; it shares the serial visual
/// collection because it puts agent windows on the screen.
/// </summary>
[Collection(VisualE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class AgentIconTests
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(15);

    // The icon is set as the window is created, so it is there by the time the window
    // shows; this only absorbs the gap between the HWND appearing and the
    // constructor reaching the icon.
    private static readonly TimeSpan IconTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Each agent window, by the self-test that shows it.</summary>
    public static TheoryData<string> AgentWindows => new()
    {
        AgentSelfTestProcess.ShowTestMainWindowArg,       // MainWindow (the dashboard)
        AgentSelfTestProcess.ShowTestJoinByCodeArg,       // JoinByCodeWindow
        AgentSelfTestProcess.ShowTestToastArg,            // JoinConfirmationWindow
        AgentSelfTestProcess.ShowTestGuidedInstallArg,    // GuidedInstallWindow
        AgentSelfTestProcess.ShowTestExtensionConsentArg, // ExtensionPolicyConsentWindow
        AgentSelfTestProcess.ShowTestOverlayArg,          // FocusOverlayWindow
        AgentSelfTestProcess.ShowTestCrashArg,            // CrashReportWindow
        AgentSelfTestProcess.ShowTestTrayMenuArg,         // the tray menu's host window
    };

    [Fact]
    public void The_agent_exe_carries_the_Anchor_icon()
    {
        var exe = TestConfig.AgentExe;
        Assert.True(File.Exists(exe), $"Agent exe not found at {exe}. Build it first.");

        var count = ExtractIconExW(exe, -1, null, null, 0);
        Assert.True(count > 0, $"{exe} carries no icon resource, so Explorer and Task Manager show the default.");

        var large = new IntPtr[1];
        var small = new IntPtr[1];
        ExtractIconExW(exe, 0, large, small, 1);
        try
        {
            var big = IconPixels.Inspect(large[0]);
            var little = IconPixels.Inspect(small[0]);
            Assert.True(big.IsAnchorMark, $"The exe's large icon is not the Anchor mark: {big}.");
            Assert.True(little.IsAnchorMark, $"The exe's small icon is not the Anchor mark: {little}.");
        }
        finally
        {
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
        }
    }

    [Theory]
    [MemberData(nameof(AgentWindows))]
    public async Task Every_agent_window_shows_the_Anchor_icon_in_its_title_bar_and_on_the_taskbar(string selfTestFlag)
    {
        await using var agent = AgentSelfTestProcess.Launch(selfTestFlag);
        await agent.WaitForWindowAsync(WindowTimeout);

        var (small, big) = await WaitForWindowIconsAsync(agent);

        Assert.True(small.IsAnchorMark, $"{selfTestFlag}: the title-bar (small) icon is not the Anchor mark: {small}.");
        Assert.True(big.IsAnchorMark, $"{selfTestFlag}: the taskbar (big) icon is not the Anchor mark: {big}.");
    }

    [Theory]
    [InlineData(BrokenIcon.Missing)]
    [InlineData(BrokenIcon.Unreadable)]
    public async Task The_crash_dialog_still_opens_when_the_icon_file_is_missing_or_unreadable(BrokenIcon broken)
    {
        using var install = AgentWithBrokenIcon.Create(broken);

        await using var agent = AgentSelfTestProcess.Launch(AgentSelfTestProcess.ShowTestCrashArg, install.Exe);
        var hwnd = await agent.WaitForWindowAsync(WindowTimeout);

        // Shown, not merely created: the crash dialog activates itself once it is
        // sized. Give it the same budget as the icon.
        var deadline = DateTime.UtcNow + IconTimeout;
        while (!WindowCapture.IsVisible(hwnd) && DateTime.UtcNow < deadline && !agent.HasExited)
            await Task.Delay(100);

        Assert.False(agent.HasExited, $"The agent exited instead of showing the crash dialog ({broken} icon).");
        Assert.True(
            WindowCapture.IsVisible(hwnd),
            $"The crash dialog never showed ({broken} icon). Windows seen:\n{WindowCapture.DescribeWindows(agent.Pid)}");
    }

    /// <summary>How the copy of the agent's icon file is broken.</summary>
    public enum BrokenIcon
    {
        /// <summary>No <c>Assets\TrayIcon.ico</c> at all.</summary>
        Missing,

        /// <summary>A file by that name that isn't an icon.</summary>
        Unreadable,
    }

    /// <summary>
    /// Poll the agent's visible window until both its icons are the Anchor mark, or
    /// the budget runs out; returns what it last read either way.
    /// </summary>
    private static async Task<(IconPixels Small, IconPixels Big)> WaitForWindowIconsAsync(AgentSelfTestProcess agent)
    {
        var deadline = DateTime.UtcNow + IconTimeout;
        var small = IconPixels.None;
        var big = IconPixels.None;
        while (true)
        {
            var hwnd = WindowCapture.FindAgentWindow(agent.Pid);
            if (hwnd != IntPtr.Zero && WindowCapture.IsVisible(hwnd))
            {
                small = IconPixels.Inspect(WindowIcon(hwnd, IconSmall));
                big = IconPixels.Inspect(WindowIcon(hwnd, IconBig));
                if (small.IsAnchorMark && big.IsAnchorMark) return (small, big);
            }

            if (agent.HasExited || DateTime.UtcNow >= deadline) return (small, big);
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// The icon <paramref name="hwnd"/> answers <c>WM_GETICON</c> with: the one the
    /// window set for itself, or zero when it set none and Windows draws its default.
    /// </summary>
    private static IntPtr WindowIcon(IntPtr hwnd, int which)
    {
        SendMessageTimeoutW(hwnd, WmGetIcon, new IntPtr(which), IntPtr.Zero, SmtoAbortIfHung, 2000, out var icon);
        return icon;
    }

    /// <summary>
    /// What an icon draws: how many of its pixels are opaque and how many of those are
    /// the mark's on-ink indigo. The Anchor icon is that one colour on a transparent
    /// ground; the Windows default icon has no indigo in it.
    /// </summary>
    private readonly record struct IconPixels(int Width, int Height, int Opaque, int Indigo)
    {
        // #7E80D2 (design/icons/generate.mjs ACCENT_INK), with room for how Windows
        // scales the icon to the size it asks for.
        private static readonly Color MarkIndigo = Color.FromArgb(0x7E, 0x80, 0xD2);
        private const int ChannelTolerance = 40;

        public static IconPixels None => default;

        public bool IsAnchorMark => Opaque > 0 && Indigo * 2 >= Opaque;

        public static IconPixels Inspect(IntPtr hicon)
        {
            if (hicon == IntPtr.Zero) return None;

            using var icon = Icon.FromHandle(hicon);
            using var bitmap = icon.ToBitmap();
            int opaque = 0, indigo = 0;
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    if (c.A < 128) continue;
                    opaque++;
                    if (Math.Abs(c.R - MarkIndigo.R) <= ChannelTolerance &&
                        Math.Abs(c.G - MarkIndigo.G) <= ChannelTolerance &&
                        Math.Abs(c.B - MarkIndigo.B) <= ChannelTolerance)
                        indigo++;
                }
            }
            return new IconPixels(bitmap.Width, bitmap.Height, opaque, indigo);
        }

        public override string ToString() =>
            Width == 0
                ? "no icon (Windows draws its default)"
                : $"{Width}x{Height}px, {Opaque} opaque pixels of which {Indigo} are the mark's indigo";
    }

    /// <summary>
    /// A copy of the built agent whose <c>Assets\TrayIcon.ico</c> is missing or isn't
    /// an icon: the state of an install whose icon file went missing or got damaged.
    /// Hard links where the volume allows (the build output is a couple of hundred
    /// MB), a copy otherwise. It sits next to the test assembly, which lives on the
    /// same volume as the build output.
    /// </summary>
    private sealed class AgentWithBrokenIcon : IDisposable
    {
        private static readonly string IconRelativePath = Path.Combine("Assets", "TrayIcon.ico");

        private readonly string _root;

        private AgentWithBrokenIcon(string root) => _root = root;

        public string Exe => Path.Combine(_root, Path.GetFileName(TestConfig.AgentExe));

        public static AgentWithBrokenIcon Create(BrokenIcon broken)
        {
            var source = Path.GetDirectoryName(TestConfig.AgentExe)!;
            if (!File.Exists(TestConfig.AgentExe))
                throw new FileNotFoundException(
                    $"Agent exe not found at {TestConfig.AgentExe}. Build it first: " +
                    "dotnet build agent/src/FocusAgent.App/FocusAgent.App.csproj -p:Platform=x64 -c Debug",
                    TestConfig.AgentExe);

            var root = Path.Combine(AppContext.BaseDirectory, $"agent-broken-icon-{Guid.NewGuid():N}");
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                if (string.Equals(relative, IconRelativePath, StringComparison.OrdinalIgnoreCase)) continue;

                var target = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!CreateHardLinkW(target, file, IntPtr.Zero))
                    File.Copy(file, target);
            }

            var icon = Path.Combine(root, IconRelativePath);
            if (broken == BrokenIcon.Unreadable)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
                File.WriteAllText(icon, "not an icon");
            }
            Assert.Equal(broken == BrokenIcon.Unreadable, File.Exists(icon));

            return new AgentWithBrokenIcon(root);
        }

        public void Dispose()
        {
            // The agent has been killed by now, but Windows can hold its files for a
            // moment after; the copy lives under the test's bin folder, so a leftover
            // is harmless.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(300);
                }
            }
        }
    }

    private const uint WmGetIcon = 0x007F;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(
        string file, int iconIndex, IntPtr[]? largeIcons, IntPtr[]? smallIcons, uint icons);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);
}
