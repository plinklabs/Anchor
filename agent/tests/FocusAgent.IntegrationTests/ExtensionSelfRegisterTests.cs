using System.Diagnostics;
using System.Runtime.Versioning;
using FocusAgent.Core.Extension;
using Microsoft.Win32;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end proof for #211: the <em>real</em> built agent writes (and, on
/// uninstall, removes) the per-user Edge <c>ExtensionInstallForcelist</c> policy
/// under <c>HKCU</c>. This is the one real-world uncertainty the issue calls out —
/// whether the agent can self-register the force-install policy on a clean,
/// unmanaged BYOD box without admin — so it's exercised against the actual
/// <c>Microsoft.Win32.Registry</c> write, not a fake.
///
/// A pure unit test (which this PR also has, against an in-memory store) cannot
/// prove the registry write itself happens: only launching the shipped exe and
/// reading HKCU back shows the produced binary touches the registry as designed.
/// The spec points the agent at a throwaway HKCU subtree (via
/// <c>ANCHOR_EXTENSION_POLICY_KEY</c>) so it never disturbs a dev's real Edge
/// policy, and deletes that subtree on the way out.
///
/// No backend is needed (this is a registry path, not a session path), so the
/// spec deliberately stays out of the backend-bound <c>AgentE2ECollection</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ExtensionSelfRegisterTests
{
    [Fact]
    public async Task RegisterMode_writes_the_forcelist_policy_to_HKCU()
    {
        using var key = ThrowawayPolicyKey.Create();

        var exit = await RunAgentPolicyModeAsync("--register-extension", key.RelativePath);
        Assert.Equal(0, exit);

        var entries = key.ReadEntries();
        var entry = Assert.Single(entries);
        // The exact value Edge consumes: <stable-id>;<store-update-url>.
        Assert.Equal(EdgeExtensionPolicy.ForcelistEntry, entry);
        Assert.True(EdgeExtensionPolicy.IsAnchorEntry(entry));
    }

    [Fact]
    public async Task RegisterMode_is_idempotent_across_two_launches()
    {
        using var key = ThrowawayPolicyKey.Create();

        Assert.Equal(0, await RunAgentPolicyModeAsync("--register-extension", key.RelativePath));
        Assert.Equal(0, await RunAgentPolicyModeAsync("--register-extension", key.RelativePath));

        // A second run must not add a duplicate entry (re-runs on every startup).
        Assert.Single(key.ReadEntries());
    }

    [Fact]
    public async Task UnregisterMode_removes_the_forcelist_policy_on_uninstall()
    {
        using var key = ThrowawayPolicyKey.Create();

        Assert.Equal(0, await RunAgentPolicyModeAsync("--register-extension", key.RelativePath));
        Assert.Single(key.ReadEntries());

        Assert.Equal(0, await RunAgentPolicyModeAsync("--unregister-extension", key.RelativePath));

        // The uninstall path leaves the box as it found it — Anchor's entry is gone.
        Assert.Empty(key.ReadEntries());
    }

    [Fact]
    public async Task UnregisterMode_preserves_a_coinstalled_products_entry()
    {
        using var key = ThrowawayPolicyKey.Create();

        // A different product already force-installs its own extension in slot 1.
        const string other = "someotherextensionidaaaaaaaaaaaa;https://example.test/crx";
        key.WriteRaw("1", other);

        Assert.Equal(0, await RunAgentPolicyModeAsync("--register-extension", key.RelativePath));
        Assert.Equal(2, key.ReadEntries().Count); // ours added alongside, not over

        Assert.Equal(0, await RunAgentPolicyModeAsync("--unregister-extension", key.RelativePath));

        // Only Anchor's entry is removed; the co-installed product's survives.
        var remaining = Assert.Single(key.ReadEntries());
        Assert.Equal(other, remaining);
    }

    /// <summary>
    /// Launch the real agent exe in a dev-only extension-policy mode, pointed at a
    /// throwaway HKCU key, and wait for it to exit. The exe is a WinExe (no console
    /// attached), so the work runs and the process exits without blocking on UI;
    /// we wait on the process itself.
    /// </summary>
    private static async Task<int> RunAgentPolicyModeAsync(string modeFlag, string relativeKeyPath)
    {
        if (!File.Exists(TestConfig.AgentExe))
            throw new FileNotFoundException(
                $"Agent exe not found at {TestConfig.AgentExe}. Build it first: " +
                "dotnet build agent/src/FocusAgent.App/FocusAgent.App.csproj -p:Platform=x64 -c Debug",
                TestConfig.AgentExe);

        var psi = new ProcessStartInfo(TestConfig.AgentExe) { UseShellExecute = false };
        psi.ArgumentList.Add(modeFlag);
        psi.Environment["ANCHOR_EXTENSION_POLICY_KEY"] = relativeKeyPath;

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
    /// A throwaway HKCU subtree the spec writes to instead of the live Edge policy
    /// key, removed on dispose. Unique per instance so concurrent specs never
    /// collide.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class ThrowawayPolicyKey : IDisposable
    {
        private const string Root = @"Software\AnchorTests";

        public string RelativePath { get; }
        private readonly string _rootToDelete;

        private ThrowawayPolicyKey(string relativePath, string rootToDelete)
        {
            RelativePath = relativePath;
            _rootToDelete = rootToDelete;
        }

        public static ThrowawayPolicyKey Create()
        {
            var unique = $"{Root}\\{Guid.NewGuid():N}";
            var relativePath = $"{unique}\\ExtensionInstallForcelist";
            return new ThrowawayPolicyKey(relativePath, unique);
        }

        public IReadOnlyList<string> ReadEntries()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RelativePath);
            if (key is null) return Array.Empty<string>();
            var entries = new List<string>();
            foreach (var name in key.GetValueNames())
                if (key.GetValue(name) is string s)
                    entries.Add(s);
            return entries;
        }

        public void WriteRaw(string valueName, string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RelativePath, writable: true);
            key.SetValue(valueName, value, RegistryValueKind.String);
        }

        public void Dispose()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(_rootToDelete, throwOnMissingSubKey: false); }
            catch { /* best-effort cleanup */ }
        }
    }
}
