using System.Text.Json;
using FocusAgent.Core.Extension;
using FocusAgent.Core.Tamper;

namespace FocusAgent.Core.Tests;

/// <summary>
/// The idempotency logic of the witness host registrar (#288): on every launch it
/// makes the manifest and HKCU key agree with the current install, without churning
/// when nothing changed and re-pointing when the install dir changes. Driven against
/// an in-memory store so the present/update/remove decisions are tested without a
/// real registry or filesystem (the real paths are exercised by the integration test).
/// </summary>
public class WitnessHostRegistrarTests
{
    private const string ExeA = @"C:\Anchor\current\anchor-witness-host.exe";
    private const string ExeB = @"C:\Anchor\app-1.2.3\anchor-witness-host.exe";

    private static string ManifestPathFor(string exePath) =>
        Path.Combine(Path.GetDirectoryName(exePath)!, "net.anchor.witness.json");

    [Fact]
    public void EnsureRegistered_writes_manifest_and_key_on_a_clean_store()
    {
        var store = new FakeWitnessHostStore();
        var registrar = new WitnessHostRegistrar(store);

        var wrote = registrar.EnsureRegistered(ExeA);

        Assert.True(wrote);
        // The key points at the manifest next to the exe.
        Assert.Equal(ManifestPathFor(ExeA), store.ManifestPath);
        // The manifest embeds the exe path.
        var manifest = JsonDocument.Parse(store.Files[ManifestPathFor(ExeA)]);
        Assert.Equal(ExeA, manifest.RootElement.GetProperty("path").GetString());
        // The manifest is the only file (#495): the host no longer hands the
        // extension a backend URL or Entra config, so nothing else goes next to it.
        Assert.Equal([ManifestPathFor(ExeA)], store.Files.Keys);
    }

    [Fact]
    public void EnsureRegistered_writes_a_manifest_that_lets_each_of_its_extension_ids_connect()
    {
        // #497: the registrar's IDs reach the manifest Edge reads.
        const string testRing = "abcdefghijklmnopabcdefghijklmnop";
        var store = new FakeWitnessHostStore();

        new WitnessHostRegistrar(store, extensionIds: AnchorExtensionIds.WithTestRing(testRing))
            .EnsureRegistered(ExeA);

        var manifest = JsonDocument.Parse(store.Files[ManifestPathFor(ExeA)]);
        Assert.Equal(
            [$"chrome-extension://{EdgeExtensionPolicy.ExtensionId}/", $"chrome-extension://{testRing}/"],
            manifest.RootElement.GetProperty("allowed_origins").EnumerateArray().Select(o => o.GetString()));
    }

    [Fact]
    public void EnsureRegistered_is_idempotent_no_rewrite_when_already_current()
    {
        var store = new FakeWitnessHostStore();
        var registrar = new WitnessHostRegistrar(store);

        Assert.True(registrar.EnsureRegistered(ExeA)); // first write
        store.KeyWriteCount = 0;
        store.FileWriteCount = 0;

        Assert.False(registrar.EnsureRegistered(ExeA)); // already current

        Assert.Equal(0, store.KeyWriteCount);  // no second key write
        Assert.Equal(0, store.FileWriteCount); // no second file write (content identical)
    }

    [Fact]
    public void EnsureRegistered_repoints_everything_on_update()
    {
        // A Velopack update lands the agent (+ host) in a new versioned dir, so the
        // manifest path and its embedded exe path both change and must be re-pointed.
        var store = new FakeWitnessHostStore();
        new WitnessHostRegistrar(store).EnsureRegistered(ExeA);

        var wrote = new WitnessHostRegistrar(store).EnsureRegistered(ExeB);

        Assert.True(wrote);
        Assert.Equal(ManifestPathFor(ExeB), store.ManifestPath);
        var manifest = JsonDocument.Parse(store.Files[ManifestPathFor(ExeB)]);
        Assert.Equal(ExeB, manifest.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void EnsureRegistered_treats_a_differently_cased_key_path_as_current()
    {
        // Windows paths are case-insensitive; a prior write recorded in a different
        // case must not trigger a pointless key re-write.
        var store = new FakeWitnessHostStore { ManifestPath = ManifestPathFor(ExeA).ToUpperInvariant() };
        // Seed the files with the exact content so only key idempotency is under test.
        store.Files[ManifestPathFor(ExeA)] = WitnessHostManifest.BuildManifest(ExeA);
        store.KeyWriteCount = 0;
        store.FileWriteCount = 0;

        var wrote = new WitnessHostRegistrar(store).EnsureRegistered(ExeA);

        Assert.False(wrote);
        Assert.Equal(0, store.KeyWriteCount);
        Assert.Equal(0, store.FileWriteCount);
    }

    [Fact]
    public void EnsureRegistered_rejects_a_blank_exe_path()
    {
        var registrar = new WitnessHostRegistrar(new FakeWitnessHostStore());
        Assert.Throws<ArgumentException>(() => registrar.EnsureRegistered("  "));
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
