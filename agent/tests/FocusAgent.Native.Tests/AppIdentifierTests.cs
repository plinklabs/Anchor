using System.Diagnostics;
using System.Runtime.Versioning;
using FocusAgent.Core.Focus;
using FocusAgent.Native;
using FocusAgent.Native.Win32;

namespace FocusAgent.Native.Tests;

[SupportedOSPlatform("windows")]
public class AppIdentifierTests
{
    [Fact]
    public void Identifies_current_process_with_path_and_name()
    {
        var identifier = new AppIdentifier();
        using var current = Process.GetCurrentProcess();

        var info = identifier.Identify(IntPtr.Zero, current.Id);

        Assert.NotNull(info);
        Assert.False(string.IsNullOrEmpty(info!.ProcessName));
        Assert.False(string.IsNullOrEmpty(info.ExecutablePath));
    }

    [Fact]
    public void Cache_returns_same_instance_for_repeated_pid()
    {
        var identifier = new AppIdentifier();
        using var current = Process.GetCurrentProcess();

        var first = identifier.Identify(IntPtr.Zero, current.Id);
        var second = identifier.Identify(IntPtr.Zero, current.Id);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void Returns_null_for_nonexistent_pid()
    {
        var identifier = new AppIdentifier();
        var info = identifier.Identify(IntPtr.Zero, processId: 0);
        Assert.Null(info);
    }

    [Theory]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    [InlineData(@"C:\Program Files\Microsoft\Edge\Application\msedge.exe")]
    public void Reads_publisher_from_well_known_embedded_signed_microsoft_binary(string path)
    {
        // System binaries like notepad.exe rely on *catalog* signing and have
        // no embedded signer — they are intentionally not covered here.
        if (!File.Exists(path))
            return; // host doesn't have this binary; skip silently.

        var publisher = AuthenticodeReader.ReadPublisher(path);

        Assert.False(string.IsNullOrWhiteSpace(publisher));
        Assert.Contains("Microsoft", publisher!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Returns_null_publisher_for_missing_file()
    {
        var publisher = AuthenticodeReader.ReadPublisher(@"C:\__does_not_exist__\nope.exe");
        Assert.Null(publisher);
    }

    [Fact]
    public void LaunchOrActivate_publisher_rule_is_not_actionable()
    {
        var identifier = new AppIdentifier();
        var rule = new AllowedAppRule
        {
            MatchKind = AllowedAppMatchKind.Publisher,
            Value = "International GeoGebra Institute",
        };

        Assert.False(identifier.LaunchOrActivate(rule));
    }

    [Fact]
    public void LaunchOrActivate_empty_value_returns_false()
    {
        var identifier = new AppIdentifier();
        var rule = new AllowedAppRule { MatchKind = AllowedAppMatchKind.ProcessName, Value = "" };

        Assert.False(identifier.LaunchOrActivate(rule));
    }

    [Fact]
    public void LaunchOrActivate_brings_a_running_apps_window_forward_through_the_enforcer()
    {
        // The overlay calls this on its UI thread when the student picks an allowed
        // app. A ShowWindow of its own on an app that is not responding held that
        // thread up, and every foreground change behind it, until the app responded
        // (#453): the enforcer restores the window from a thread of its own.
        using var window = TestWindow.Open("Anchor test allowed app");
        using var current = Process.GetCurrentProcess();
        var enforcer = new RecordingEnforcer();
        var identifier = new AppIdentifier(enforcer);
        var rule = new AllowedAppRule { MatchKind = AllowedAppMatchKind.ProcessName, Value = current.ProcessName };

        Assert.True(identifier.LaunchOrActivate(rule));

        var broughtForward = Assert.Single(enforcer.BroughtForward);
        NativeMethods.GetWindowThreadProcessId(broughtForward, out var pid);
        Assert.Equal((uint)current.Id, pid);
    }

    [Fact]
    public void LaunchOrActivate_unknown_process_name_returns_false()
    {
        var identifier = new AppIdentifier();
        var rule = new AllowedAppRule
        {
            MatchKind = AllowedAppMatchKind.ProcessName,
            Value = "this_process_definitely_does_not_exist_12345",
        };

        Assert.False(identifier.LaunchOrActivate(rule));
    }

    /// <summary>Records the windows it is asked to bring forward, and touches none.</summary>
    private sealed class RecordingEnforcer : IFocusEnforcer
    {
        public List<nint> BroughtForward { get; } = new();
        public void BringForward(nint windowHandle) => BroughtForward.Add(windowHandle);
        public bool RememberAllowed(nint windowHandle) => true;
        public void Minimize(nint windowHandle) { }
        public bool Block(nint offendingWindowHandle) => false;
        public Task<bool> HandOverAsync(
            nint offendingWindowHandle, nint successorWindowHandle, Func<bool>? stillToGoDown = null) =>
            Task.FromResult(false);
        public nint ForegroundWindowOf(nint windowHandle) => IntPtr.Zero;
        public void Reset() { }
    }
}
