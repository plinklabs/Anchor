using FocusAgent.Core.Extension;

namespace FocusAgent.Core.Tests;

public class ExtensionSelfRegistrarTests
{
    [Fact]
    public void EnsurePolicyWritten_writes_the_anchor_entry_on_a_clean_store()
    {
        var store = new FakePolicyStore();
        var registrar = NewRegistrar(store, checkedIn: () => false);

        var wrote = registrar.EnsurePolicyWritten();

        Assert.True(wrote);
        var entry = Assert.Single(store.Entries.Values);
        Assert.Equal(EdgeExtensionPolicy.ForcelistEntry, entry);
        // First free slot on a clean store.
        Assert.Equal("1", Assert.Single(store.Entries.Keys));
    }

    [Fact]
    public void EnsurePolicyWritten_is_idempotent_no_duplicate_on_a_second_run()
    {
        var store = new FakePolicyStore();
        var registrar = NewRegistrar(store, checkedIn: () => false);

        Assert.True(registrar.EnsurePolicyWritten());
        Assert.False(registrar.EnsurePolicyWritten()); // already present

        Assert.Single(store.Entries); // not two
    }

    [Fact]
    public void EnsurePolicyWritten_preserves_a_coinstalled_products_entry()
    {
        var store = new FakePolicyStore();
        store.Entries["1"] = "someotherextensionidaaaaaaaaaaaa;https://example/crx";
        var registrar = NewRegistrar(store, checkedIn: () => false);

        registrar.EnsurePolicyWritten();

        Assert.Equal(2, store.Entries.Count);
        Assert.Equal("someotherextensionidaaaaaaaaaaaa;https://example/crx", store.Entries["1"]);
        Assert.Equal(EdgeExtensionPolicy.ForcelistEntry, store.Entries["2"]);
    }

    [Fact]
    public async Task RegisterAndVerify_skips_the_wait_when_already_checked_in()
    {
        var store = new FakePolicyStore();
        var guided = new GuidedSpy();
        var delayed = false;
        var registrar = NewRegistrar(
            store,
            checkedIn: () => true, // already installed from a previous run
            guided: guided,
            delay: (_, _) => { delayed = true; return Task.CompletedTask; });

        var ok = await registrar.RegisterAndVerifyAsync();

        Assert.True(ok);
        Assert.False(delayed);              // no grace wait
        Assert.Equal(0, guided.ShowCount);  // no fallback
        Assert.Single(store.Entries);       // policy still written
    }

    [Fact]
    public async Task RegisterAndVerify_succeeds_when_the_extension_checks_in_within_the_grace_period()
    {
        var store = new FakePolicyStore();
        var guided = new GuidedSpy();
        var checkedIn = false;
        var registrar = NewRegistrar(
            store,
            checkedIn: () => checkedIn,
            guided: guided,
            // The "grace period elapses" — simulate the extension installing during it.
            delay: (_, _) => { checkedIn = true; return Task.CompletedTask; });

        var ok = await registrar.RegisterAndVerifyAsync();

        Assert.True(ok);
        Assert.Equal(0, guided.ShowCount); // the force-install took; no fallback
    }

    [Fact]
    public async Task RegisterAndVerify_falls_back_to_guided_install_when_the_extension_never_checks_in()
    {
        var store = new FakePolicyStore();
        var guided = new GuidedSpy();
        var registrar = NewRegistrar(
            store,
            checkedIn: () => false, // never installs
            guided: guided,
            delay: (_, _) => Task.CompletedTask);

        var ok = await registrar.RegisterAndVerifyAsync();

        Assert.False(ok);
        Assert.Equal(1, guided.ShowCount); // guided install opened
    }

    [Fact]
    public async Task RegisterAndVerify_falls_back_to_guided_install_when_the_registry_write_throws()
    {
        var store = new ThrowingPolicyStore();
        var guided = new GuidedSpy();
        var registrar = NewRegistrar(
            store,
            checkedIn: () => false,
            guided: guided,
            delay: (_, _) => Task.CompletedTask);

        // A locked-down HKCU (write throws) must not crash startup — it's exactly
        // the case the guided fallback exists for.
        var ok = await registrar.RegisterAndVerifyAsync();

        Assert.False(ok);
        Assert.Equal(1, guided.ShowCount);
    }

    [Fact]
    public void RemovePolicy_removes_only_the_anchor_entry()
    {
        var store = new FakePolicyStore();
        store.Entries["1"] = "someotherextensionidaaaaaaaaaaaa;https://example/crx";
        store.Entries["2"] = EdgeExtensionPolicy.ForcelistEntry;
        var registrar = NewRegistrar(store, checkedIn: () => false);

        registrar.RemovePolicy();

        var remaining = Assert.Single(store.Entries);
        Assert.Equal("someotherextensionidaaaaaaaaaaaa;https://example/crx", remaining.Value);
    }

    [Fact]
    public void RemovePolicy_is_a_noop_when_nothing_was_written()
    {
        var store = new FakePolicyStore();
        var registrar = NewRegistrar(store, checkedIn: () => false);

        var ex = Record.Exception(() => registrar.RemovePolicy());

        Assert.Null(ex);
        Assert.Empty(store.Entries);
    }

    private static ExtensionSelfRegistrar NewRegistrar(
        IExtensionPolicyStore store,
        Func<bool> checkedIn,
        GuidedSpy? guided = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(
            store,
            checkedIn,
            showGuidedInstall: (guided ?? new GuidedSpy()).ShowAsync,
            delayAsync: delay ?? ((_, _) => Task.CompletedTask),
            gracePeriod: TimeSpan.FromMilliseconds(1));

    private sealed class GuidedSpy
    {
        public int ShowCount { get; private set; }
        public Task ShowAsync(CancellationToken ct)
        {
            ShowCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePolicyStore : IExtensionPolicyStore
    {
        public Dictionary<string, string> Entries { get; } = new();

        public IReadOnlyList<string> GetForcelistEntries() => Entries.Values.ToList();
        public IReadOnlyList<string> GetForcelistValueNames() => Entries.Keys.ToList();
        public void AddForcelistEntry(string valueName, string entry) => Entries[valueName] = entry;

        public void RemoveAnchorForcelistEntries()
        {
            foreach (var name in Entries.Where(kv => EdgeExtensionPolicy.IsAnchorEntry(kv.Value))
                                        .Select(kv => kv.Key).ToList())
            {
                Entries.Remove(name);
            }
        }
    }

    private sealed class ThrowingPolicyStore : IExtensionPolicyStore
    {
        public IReadOnlyList<string> GetForcelistEntries() => Array.Empty<string>();
        public IReadOnlyList<string> GetForcelistValueNames() => Array.Empty<string>();
        public void AddForcelistEntry(string valueName, string entry) => throw new UnauthorizedAccessException("locked-down HKCU");
        public void RemoveAnchorForcelistEntries() { }
    }
}
