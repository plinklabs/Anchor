using System.Diagnostics;
using System.Runtime.Versioning;
using FocusAgent.Core.Startup;
using Microsoft.Win32;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end proof for #225: the <em>real</em> built agent writes (and, on
/// uninstall, removes) the per-user "start at login" entry under
/// <c>HKCU\...\Run</c> — the unpackaged-friendly equivalent of a packaged app's
/// <c>windows.startupTask</c> extension. This is the one real-world uncertainty the
/// issue calls out — whether the agent can self-register the Run key on a clean,
/// unmanaged BYOD box without admin — so it's exercised against the actual
/// <c>Microsoft.Win32.Registry</c> write, not a fake.
///
/// A pure unit test (which this PR also has, against an in-memory store) cannot
/// prove the registry write itself happens: only launching the shipped exe and
/// reading HKCU back shows the produced binary touches the registry as designed.
/// The spec points the agent at a throwaway HKCU subtree (via
/// <c>ANCHOR_STARTUP_RUN_KEY</c>) so it never disturbs a dev's real auto-start
/// entries, and deletes that subtree on the way out.
///
/// No backend is needed (this is a registry path, not a session path), so the spec
/// deliberately stays out of the backend-bound <c>AgentE2ECollection</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartupRegistrationTests
{
    private const string FakeExe = @"C:\Anchor\Test\FocusAgent.App.exe";

    [Fact]
    public async Task RegisterMode_writes_the_run_value_to_HKCU()
    {
        using var key = ThrowawayRunKey.Create();

        var exit = await RunAgentStartupModeAsync("--register-startup", key.RelativePath, FakeExe);
        Assert.Equal(0, exit);

        var command = key.ReadCommand();
        Assert.NotNull(command);
        // The exact value Windows runs at login: the quoted exe path.
        Assert.Equal($"\"{FakeExe}\"", command);
        Assert.True(StartupRunKey.CommandTargets(command, FakeExe));
    }

    [Fact]
    public async Task RegisterMode_is_idempotent_across_two_launches()
    {
        using var key = ThrowawayRunKey.Create();

        Assert.Equal(0, await RunAgentStartupModeAsync("--register-startup", key.RelativePath, FakeExe));
        Assert.Equal(0, await RunAgentStartupModeAsync("--register-startup", key.RelativePath, FakeExe));

        // A second run leaves exactly one Anchor value (re-runs on every install/update).
        Assert.Equal(1, key.AnchorValueCount());
        Assert.Equal($"\"{FakeExe}\"", key.ReadCommand());
    }

    [Fact]
    public async Task UnregisterMode_removes_the_run_value_on_uninstall()
    {
        using var key = ThrowawayRunKey.Create();

        Assert.Equal(0, await RunAgentStartupModeAsync("--register-startup", key.RelativePath, FakeExe));
        Assert.NotNull(key.ReadCommand());

        Assert.Equal(0, await RunAgentStartupModeAsync("--unregister-startup", key.RelativePath, exePath: null));

        // The uninstall path leaves the box as it found it — Anchor's value is gone.
        Assert.Null(key.ReadCommand());
    }

    [Fact]
    public async Task UnregisterMode_preserves_another_products_run_value()
    {
        using var key = ThrowawayRunKey.Create();

        // A different product already has its own auto-start entry.
        key.WriteRaw("SomeOtherApp", @"""C:\Other\Other.exe""");

        Assert.Equal(0, await RunAgentStartupModeAsync("--register-startup", key.RelativePath, FakeExe));
        Assert.Equal(2, key.TotalValueCount()); // ours added alongside, not over

        Assert.Equal(0, await RunAgentStartupModeAsync("--unregister-startup", key.RelativePath, exePath: null));

        // Only Anchor's value is removed; the other product's survives.
        Assert.Null(key.ReadCommand());
        Assert.Equal(@"""C:\Other\Other.exe""", key.ReadRaw("SomeOtherApp"));
    }

    /// <summary>
    /// Launch the real agent exe in a dev-only startup-registration mode, pointed at
    /// a throwaway HKCU key (and an explicit exe path), and wait for it to exit. The
    /// exe is a WinExe (no console attached), so the work runs and the process exits
    /// without blocking on UI; we wait on the process itself.
    /// </summary>
    private static async Task<int> RunAgentStartupModeAsync(string modeFlag, string relativeKeyPath, string? exePath)
    {
        if (!File.Exists(TestConfig.AgentExe))
            throw new FileNotFoundException(
                $"Agent exe not found at {TestConfig.AgentExe}. Build it first: " +
                "dotnet build agent/src/FocusAgent.App/FocusAgent.App.csproj -p:Platform=x64 -c Debug",
                TestConfig.AgentExe);

        var psi = new ProcessStartInfo(TestConfig.AgentExe) { UseShellExecute = false };
        psi.ArgumentList.Add(modeFlag);
        psi.Environment["ANCHOR_STARTUP_RUN_KEY"] = relativeKeyPath;
        if (exePath is not null)
            psi.Environment["ANCHOR_STARTUP_EXE_PATH"] = exePath;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the agent process.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Agent did not exit within 30s of '{modeFlag}'.");
        }

        return process.ExitCode;
    }

    /// <summary>
    /// A throwaway HKCU subtree the spec writes to instead of the live Run key,
    /// removed on dispose. Unique per instance so concurrent specs never collide.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class ThrowawayRunKey : IDisposable
    {
        private const string Root = @"Software\AnchorTests";

        public string RelativePath { get; }
        private readonly string _rootToDelete;

        private ThrowawayRunKey(string relativePath, string rootToDelete)
        {
            RelativePath = relativePath;
            _rootToDelete = rootToDelete;
        }

        public static ThrowawayRunKey Create()
        {
            var unique = $"{Root}\\{Guid.NewGuid():N}";
            var relativePath = $"{unique}\\Run";
            return new ThrowawayRunKey(relativePath, unique);
        }

        /// <summary>Anchor's registered Run command, or null when absent.</summary>
        public string? ReadCommand() => ReadRaw(StartupRunKey.ValueName);

        public string? ReadRaw(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RelativePath);
            return key?.GetValue(valueName) as string;
        }

        public void WriteRaw(string valueName, string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RelativePath, writable: true);
            key.SetValue(valueName, value, RegistryValueKind.String);
        }

        public int TotalValueCount()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RelativePath);
            return key?.GetValueNames().Length ?? 0;
        }

        public int AnchorValueCount()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RelativePath);
            if (key is null) return 0;
            return key.GetValueNames().Count(n =>
                string.Equals(n, StartupRunKey.ValueName, StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(_rootToDelete, throwOnMissingSubKey: false); }
            catch { /* best-effort cleanup */ }
        }
    }
}
