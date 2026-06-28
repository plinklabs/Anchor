using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using FocusAgent.Core.Extension;
using FocusAgent.Core.Tamper;
using Microsoft.Win32;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end proof for #288: the <em>real</em> built agent registers (and, on
/// uninstall, removes) the witness native-messaging host — the HKCU
/// <c>NativeMessagingHosts\net.anchor.witness</c> key Edge reads, plus the manifest
/// and <c>backend-url.json</c> files next to the host exe. This was the release-only
/// gap behind the "install the extension" popup firing every launch and the extension
/// never learning its backend, so it's exercised against the actual
/// <c>Microsoft.Win32.Registry</c> + filesystem write, not a fake.
///
/// A pure unit test (which this PR also has, against an in-memory store) cannot prove
/// the registry/file write itself happens: only launching the shipped exe and reading
/// HKCU + the files back shows the produced binary touches them as designed. The spec
/// points the agent at a throwaway HKCU subtree (<c>ANCHOR_WITNESS_HOST_KEY</c>) and a
/// throwaway directory (<c>ANCHOR_WITNESS_HOST_EXE_PATH</c>) so it never disturbs a
/// dev's real native-messaging registration, and cleans both up on the way out.
///
/// No backend is needed (this is a registry/file path, not a session path), so the
/// spec deliberately stays out of the backend-bound <c>AgentE2ECollection</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WitnessHostRegisterTests
{
    private const string BackendUrl = "https://e2e.witness.example";

    [Fact]
    public async Task RegisterMode_writes_the_key_manifest_and_backend_file()
    {
        using var key = ThrowawayHostKey.Create();
        using var dir = ThrowawayHostDir.Create();

        var exit = await RunAgentWitnessModeAsync("--register-witness-host", key, dir);
        Assert.Equal(0, exit);

        // The HKCU key points at the manifest next to the host exe.
        var expectedManifestPath = Path.Combine(dir.Path, "net.anchor.witness.json");
        Assert.Equal(expectedManifestPath, key.ReadManifestPath());

        // The manifest Edge consumes: the absolute exe path + the pinned extension origin.
        using var manifest = JsonDocument.Parse(File.ReadAllText(expectedManifestPath));
        Assert.Equal("net.anchor.witness", manifest.RootElement.GetProperty("name").GetString());
        Assert.Equal(dir.HostExePath, manifest.RootElement.GetProperty("path").GetString());
        var origin = Assert.Single(manifest.RootElement.GetProperty("allowed_origins").EnumerateArray());
        Assert.Equal($"chrome-extension://{EdgeExtensionPolicy.ExtensionId}/", origin.GetString());

        // The backend-url.json the host reads carries the configured backend.
        var backendFile = Path.Combine(dir.Path, "backend-url.json");
        using var backend = JsonDocument.Parse(File.ReadAllText(backendFile));
        Assert.Equal(BackendUrl, backend.RootElement.GetProperty("backendUrl").GetString());
    }

    [Fact]
    public async Task RegisterMode_is_idempotent_across_two_launches()
    {
        using var key = ThrowawayHostKey.Create();
        using var dir = ThrowawayHostDir.Create();

        Assert.Equal(0, await RunAgentWitnessModeAsync("--register-witness-host", key, dir));
        Assert.Equal(0, await RunAgentWitnessModeAsync("--register-witness-host", key, dir));

        // A second run leaves exactly the same single registration (re-runs every launch).
        Assert.Equal(Path.Combine(dir.Path, "net.anchor.witness.json"), key.ReadManifestPath());
    }

    [Fact]
    public async Task UnregisterMode_removes_the_key_on_uninstall()
    {
        using var key = ThrowawayHostKey.Create();
        using var dir = ThrowawayHostDir.Create();

        Assert.Equal(0, await RunAgentWitnessModeAsync("--register-witness-host", key, dir));
        Assert.NotNull(key.ReadManifestPath());

        Assert.Equal(0, await RunAgentWitnessModeAsync("--unregister-witness-host", key, dir));

        // The uninstall path leaves the box as it found it — the host key is gone.
        Assert.Null(key.ReadManifestPath());
    }

    [Fact]
    public async Task UnregisterMode_preserves_another_native_messaging_host()
    {
        using var key = ThrowawayHostKey.Create();
        using var dir = ThrowawayHostDir.Create();

        // A different product registers its own native-messaging host alongside ours.
        key.WriteSibling("com.other.host", @"C:\Other\other-host.json");

        Assert.Equal(0, await RunAgentWitnessModeAsync("--register-witness-host", key, dir));
        Assert.NotNull(key.ReadManifestPath());

        Assert.Equal(0, await RunAgentWitnessModeAsync("--unregister-witness-host", key, dir));

        // Only Anchor's host key is removed; the other product's sibling survives.
        Assert.Null(key.ReadManifestPath());
        Assert.Equal(@"C:\Other\other-host.json", key.ReadSibling("com.other.host"));
    }

    /// <summary>
    /// Launch the real agent exe in a dev-only witness-host mode, pointed at a throwaway
    /// HKCU key + directory + backend URL, and wait for it to exit. The exe is a WinExe
    /// (no console attached), so the work runs and the process exits without blocking on
    /// UI; we wait on the process itself.
    /// </summary>
    private static async Task<int> RunAgentWitnessModeAsync(string modeFlag, ThrowawayHostKey key, ThrowawayHostDir dir)
    {
        if (!File.Exists(TestConfig.AgentExe))
            throw new FileNotFoundException(
                $"Agent exe not found at {TestConfig.AgentExe}. Build it first: " +
                "dotnet build agent/src/FocusAgent.App/FocusAgent.App.csproj -p:Platform=x64 -c Debug",
                TestConfig.AgentExe);

        var psi = new ProcessStartInfo(TestConfig.AgentExe) { UseShellExecute = false };
        psi.ArgumentList.Add(modeFlag);
        psi.Environment["ANCHOR_WITNESS_HOST_KEY"] = key.WitnessKeyPath;
        psi.Environment["ANCHOR_WITNESS_HOST_EXE_PATH"] = dir.HostExePath;
        psi.Environment["ANCHOR_WITNESS_HOST_BACKEND_URL"] = BackendUrl;

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
    /// A throwaway HKCU subtree standing in for the live Edge <c>NativeMessagingHosts</c>
    /// branch, removed on dispose. Unique per instance so concurrent specs never collide.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class ThrowawayHostKey : IDisposable
    {
        private const string Root = @"Software\AnchorTests";

        /// <summary>The full HKCU-relative path to Anchor's host key (what the agent writes).</summary>
        public string WitnessKeyPath { get; }
        private readonly string _hostsParent;
        private readonly string _rootToDelete;

        private ThrowawayHostKey(string witnessKeyPath, string hostsParent, string rootToDelete)
        {
            WitnessKeyPath = witnessKeyPath;
            _hostsParent = hostsParent;
            _rootToDelete = rootToDelete;
        }

        public static ThrowawayHostKey Create()
        {
            var unique = $"{Root}\\{Guid.NewGuid():N}";
            var hostsParent = $"{unique}\\NativeMessagingHosts";
            var witnessKeyPath = $"{hostsParent}\\{WitnessLink.NativeHostName}";
            return new ThrowawayHostKey(witnessKeyPath, hostsParent, unique);
        }

        /// <summary>The manifest path recorded under Anchor's host key (default value), or null.</summary>
        public string? ReadManifestPath()
        {
            using var key = Registry.CurrentUser.OpenSubKey(WitnessKeyPath);
            return key?.GetValue(null) as string;
        }

        public void WriteSibling(string hostName, string manifestPath)
        {
            using var key = Registry.CurrentUser.CreateSubKey($"{_hostsParent}\\{hostName}", writable: true);
            key.SetValue(null, manifestPath, RegistryValueKind.String);
        }

        public string? ReadSibling(string hostName)
        {
            using var key = Registry.CurrentUser.OpenSubKey($"{_hostsParent}\\{hostName}");
            return key?.GetValue(null) as string;
        }

        public void Dispose()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(_rootToDelete, throwOnMissingSubKey: false); }
            catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// A throwaway directory standing in for the host exe's install dir — where the
    /// agent drops the manifest + backend-url files. The exe itself need not exist (the
    /// CLI mode drives the registrar directly). Removed on dispose.
    /// </summary>
    private sealed class ThrowawayHostDir : IDisposable
    {
        public string Path { get; }
        public string HostExePath { get; }

        private ThrowawayHostDir(string path)
        {
            Path = path;
            HostExePath = System.IO.Path.Combine(path, WitnessHostManifest.HostExeName);
        }

        public static ThrowawayHostDir Create()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"anchor-witness-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return new ThrowawayHostDir(dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
