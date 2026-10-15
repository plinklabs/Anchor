using FocusAgent.Core.Extension;

namespace FocusAgent.Core.Tests;

/// <summary>
/// The file that remembers a "not now" and which HKLM entry is ours (#405) — it
/// must survive a restart, and a missing or damaged file must never stop the agent
/// (at worst the student is asked once more).
/// </summary>
public sealed class ExtensionPolicyStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"anchor-ext-state-{Guid.NewGuid():N}");

    private string StatePath => Path.Combine(_dir, "nested", "extension-policy.json");

    [Fact]
    public void A_missing_file_loads_as_nothing_remembered()
    {
        var state = new FileExtensionPolicyStateStore(StatePath).Load();

        Assert.False(state.PromptDeclined);
        Assert.Null(state.MachineEntryValueName);
    }

    [Fact]
    public void Saved_state_survives_a_new_store_instance()
    {
        var at = new DateTimeOffset(2026, 10, 15, 8, 30, 0, TimeSpan.Zero);
        new FileExtensionPolicyStateStore(StatePath).Save(new ExtensionPolicyState
        {
            PromptDeclined = true,
            PromptDeclinedAt = at,
            MachineEntryValueName = "3",
        });

        var loaded = new FileExtensionPolicyStateStore(StatePath).Load();

        Assert.True(loaded.PromptDeclined);
        Assert.Equal(at, loaded.PromptDeclinedAt);
        Assert.Equal("3", loaded.MachineEntryValueName);
        Assert.False(File.Exists(StatePath + ".tmp"));
    }

    [Fact]
    public void A_damaged_file_loads_as_nothing_remembered()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, "{ not json");

        var state = new FileExtensionPolicyStateStore(StatePath).Load();

        Assert.False(state.PromptDeclined);
    }

    [Fact]
    public void The_default_file_sits_next_to_the_agent_logs_outside_the_install_folder()
    {
        var path = FileExtensionPolicyStateStore.DefaultPath();

        Assert.EndsWith(Path.Combine("Anchor", "FocusAgent", "extension-policy.json"), path);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
