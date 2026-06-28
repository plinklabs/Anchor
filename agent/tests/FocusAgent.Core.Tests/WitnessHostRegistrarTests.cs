using System.Text.Json;
using FocusAgent.Core.Tamper;

namespace FocusAgent.Core.Tests;

/// <summary>
/// The idempotency logic of the witness host registrar (#288): on every launch it
/// makes the manifest, backend-url file, and HKCU key agree with the current install,
/// without churning when nothing changed and re-pointing when the install dir or
/// backend URL changes. Driven against an in-memory store so the present/update/remove
/// decisions are tested without a real registry or filesystem (the real paths are
/// exercised by the integration test).
/// </summary>
public class WitnessHostRegistrarTests
{
    private const string ExeA = @"C:\Anchor\current\anchor-witness-host.exe";
    private const string ExeB = @"C:\Anchor\app-1.2.3\anchor-witness-host.exe";
    private const string Backend = "https://anchor.example";

    private static string ManifestPathFor(string exePath) =>
        Path.Combine(Path.GetDirectoryName(exePath)!, "net.anchor.witness.json");

    private static string BackendPathFor(string exePath) =>
        Path.Combine(Path.GetDirectoryName(exePath)!, "backend-url.json");

    private static string AuthPathFor(string exePath) =>
        Path.Combine(Path.GetDirectoryName(exePath)!, "auth-config.json");

    private static readonly WitnessAuthConfig Auth = new("tenant-1", "client-1", "api://x/.default");

    [Fact]
    public void EnsureRegistered_writes_manifest_backend_and_key_on_a_clean_store()
    {
        var store = new FakeWitnessHostStore();
        var registrar = new WitnessHostRegistrar(store);

        var wrote = registrar.EnsureRegistered(ExeA, Backend);

        Assert.True(wrote);
        // The key points at the manifest next to the exe.
        Assert.Equal(ManifestPathFor(ExeA), store.ManifestPath);
        // The manifest embeds the exe path.
        var manifest = JsonDocument.Parse(store.Files[ManifestPathFor(ExeA)]);
        Assert.Equal(ExeA, manifest.RootElement.GetProperty("path").GetString());
        // The backend file carries the configured URL.
        var backend = JsonDocument.Parse(store.Files[BackendPathFor(ExeA)]);
        Assert.Equal(Backend, backend.RootElement.GetProperty("backendUrl").GetString());
    }

    [Fact]
    public void EnsureRegistered_is_idempotent_no_rewrite_when_already_current()
    {
        var store = new FakeWitnessHostStore();
        var registrar = new WitnessHostRegistrar(store);

        Assert.True(registrar.EnsureRegistered(ExeA, Backend)); // first write
        store.KeyWriteCount = 0;
        store.FileWriteCount = 0;

        Assert.False(registrar.EnsureRegistered(ExeA, Backend)); // already current

        Assert.Equal(0, store.KeyWriteCount);  // no second key write
        Assert.Equal(0, store.FileWriteCount); // no second file write (content identical)
    }

    [Fact]
    public void EnsureRegistered_repoints_everything_on_update()
    {
        // A Velopack update lands the agent (+ host) in a new versioned dir, so the
        // manifest path and its embedded exe path both change and must be re-pointed.
        var store = new FakeWitnessHostStore();
        new WitnessHostRegistrar(store).EnsureRegistered(ExeA, Backend);

        var wrote = new WitnessHostRegistrar(store).EnsureRegistered(ExeB, Backend);

        Assert.True(wrote);
        Assert.Equal(ManifestPathFor(ExeB), store.ManifestPath);
        var manifest = JsonDocument.Parse(store.Files[ManifestPathFor(ExeB)]);
        Assert.Equal(ExeB, manifest.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void EnsureRegistered_rewrites_the_backend_file_when_the_url_changes()
    {
        var store = new FakeWitnessHostStore();
        var registrar = new WitnessHostRegistrar(store);
        registrar.EnsureRegistered(ExeA, Backend);
        store.FileWriteCount = 0;

        var wrote = registrar.EnsureRegistered(ExeA, "https://other.example");

        Assert.True(wrote);
        Assert.Equal(1, store.FileWriteCount); // only the backend file changed
        var backend = JsonDocument.Parse(store.Files[BackendPathFor(ExeA)]);
        Assert.Equal("https://other.example", backend.RootElement.GetProperty("backendUrl").GetString());
    }

    [Fact]
    public void EnsureRegistered_treats_a_differently_cased_key_path_as_current()
    {
        // Windows paths are case-insensitive; a prior write recorded in a different
        // case must not trigger a pointless key re-write.
        var store = new FakeWitnessHostStore { ManifestPath = ManifestPathFor(ExeA).ToUpperInvariant() };
        // Seed the files with the exact content so only key idempotency is under test.
        store.Files[ManifestPathFor(ExeA)] = WitnessHostManifest.BuildManifest(ExeA);
        store.Files[BackendPathFor(ExeA)] = WitnessHostManifest.BuildBackendUrlFile(Backend);
        store.KeyWriteCount = 0;
        store.FileWriteCount = 0;

        var wrote = new WitnessHostRegistrar(store).EnsureRegistered(ExeA, Backend);

        Assert.False(wrote);
        Assert.Equal(0, store.KeyWriteCount);
        Assert.Equal(0, store.FileWriteCount);
    }

    [Fact]
    public void EnsureRegistered_writes_the_auth_config_file_when_auth_is_supplied()
    {
        var store = new FakeWitnessHostStore();

        var wrote = new WitnessHostRegistrar(store).EnsureRegistered(ExeA, Backend, Auth);

        Assert.True(wrote);
        var auth = JsonDocument.Parse(store.Files[AuthPathFor(ExeA)]).RootElement;
        Assert.Equal("tenant-1", auth.GetProperty("tenantId").GetString());
        Assert.Equal("client-1", auth.GetProperty("clientId").GetString());
        Assert.Equal("api://x/.default", auth.GetProperty("scope").GetString());
    }

    [Fact]
    public void EnsureRegistered_writes_no_auth_config_file_in_dev_when_auth_is_null()
    {
        var store = new FakeWitnessHostStore();

        new WitnessHostRegistrar(store).EnsureRegistered(ExeA, Backend, auth: null);

        // A dev / no-auth deployment leaves no auth-config.json behind, so the host
        // sends no auth_config and the extension stays on the dev impersonation path.
        Assert.False(store.Files.ContainsKey(AuthPathFor(ExeA)));
    }

    [Fact]
    public void EnsureRegistered_rewrites_the_auth_config_file_when_it_changes()
    {
        var store = new FakeWitnessHostStore();
        var registrar = new WitnessHostRegistrar(store);
        registrar.EnsureRegistered(ExeA, Backend, Auth);
        store.FileWriteCount = 0;

        var wrote = registrar.EnsureRegistered(ExeA, Backend, new WitnessAuthConfig("tenant-2", "client-1", "api://x/.default"));

        Assert.True(wrote);
        Assert.Equal(1, store.FileWriteCount); // only the auth-config file changed
        var auth = JsonDocument.Parse(store.Files[AuthPathFor(ExeA)]).RootElement;
        Assert.Equal("tenant-2", auth.GetProperty("tenantId").GetString());
    }

    [Fact]
    public void EnsureRegistered_rejects_a_blank_exe_path()
    {
        var registrar = new WitnessHostRegistrar(new FakeWitnessHostStore());
        Assert.Throws<ArgumentException>(() => registrar.EnsureRegistered("  ", Backend));
    }

    [Fact]
    public void Remove_deletes_the_key()
    {
        var store = new FakeWitnessHostStore { ManifestPath = ManifestPathFor(ExeA) };
        var registrar = new WitnessHostRegistrar(store);

        registrar.Remove();

        Assert.Null(store.ManifestPath);
    }

    [Fact]
    public void Remove_swallows_a_store_failure_so_uninstall_is_never_blocked()
    {
        var registrar = new WitnessHostRegistrar(new ThrowingWitnessHostStore());

        var ex = Record.Exception(() => registrar.Remove());

        Assert.Null(ex);
    }

    private sealed class FakeWitnessHostStore : IWitnessHostStore
    {
        public string? ManifestPath { get; set; }
        public int KeyWriteCount { get; set; }
        public Dictionary<string, string> Files { get; } = new();
        public int FileWriteCount { get; set; }

        public string? GetRegisteredManifestPath() => ManifestPath;
        public void SetRegisteredManifestPath(string manifestPath)
        {
            ManifestPath = manifestPath;
            KeyWriteCount++;
        }
        public void RemoveRegistration() => ManifestPath = null;
        public string? ReadFile(string path) => Files.TryGetValue(path, out var c) ? c : null;
        public void WriteFile(string path, string content)
        {
            Files[path] = content;
            FileWriteCount++;
        }
    }

    private sealed class ThrowingWitnessHostStore : IWitnessHostStore
    {
        public string? GetRegisteredManifestPath() => null;
        public void SetRegisteredManifestPath(string manifestPath) => throw new UnauthorizedAccessException();
        public void RemoveRegistration() => throw new UnauthorizedAccessException("locked-down HKCU");
        public string? ReadFile(string path) => null;
        public void WriteFile(string path, string content) => throw new UnauthorizedAccessException();
    }
}
