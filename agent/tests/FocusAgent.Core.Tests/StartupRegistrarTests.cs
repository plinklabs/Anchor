using FocusAgent.Core.Startup;

namespace FocusAgent.Core.Tests;

public class StartupRegistrarTests
{
    private const string ExeA = @"C:\Users\bob\AppData\Local\AnchorAgent\current\FocusAgent.App.exe";
    private const string ExeB = @"C:\Users\bob\AppData\Local\AnchorAgent\app-1.2.3\FocusAgent.App.exe";

    [Fact]
    public void EnsureRegistered_writes_the_quoted_command_on_a_clean_store()
    {
        var store = new FakeStartupStore();
        var registrar = new StartupRegistrar(store);

        var wrote = registrar.EnsureRegistered(ExeA);

        Assert.True(wrote);
        Assert.Equal($"\"{ExeA}\"", store.Command);
    }

    [Fact]
    public void EnsureRegistered_is_idempotent_no_rewrite_when_already_pointing_at_the_exe()
    {
        var store = new FakeStartupStore();
        var registrar = new StartupRegistrar(store);

        Assert.True(registrar.EnsureRegistered(ExeA));   // first write
        store.WriteCount = 0;
        Assert.False(registrar.EnsureRegistered(ExeA));  // already current

        Assert.Equal(0, store.WriteCount); // no second write
        Assert.Equal($"\"{ExeA}\"", store.Command);
    }

    [Fact]
    public void EnsureRegistered_repoints_a_stale_path_on_update()
    {
        // An update lands the agent in a new versioned install dir; the Run value
        // still points at the previous version's exe and must be re-pointed.
        var store = new FakeStartupStore { Command = $"\"{ExeA}\"" };
        var registrar = new StartupRegistrar(store);

        var wrote = registrar.EnsureRegistered(ExeB);

        Assert.True(wrote);
        Assert.Equal($"\"{ExeB}\"", store.Command);
    }

    [Fact]
    public void EnsureRegistered_treats_an_unquoted_existing_command_for_the_same_exe_as_current()
    {
        // A hand-written or legacy entry without surrounding quotes still targets
        // the same exe, so we must not pointlessly rewrite it.
        var store = new FakeStartupStore { Command = ExeA };
        var registrar = new StartupRegistrar(store);

        var wrote = registrar.EnsureRegistered(ExeA);

        Assert.False(wrote);
    }

    [Fact]
    public void Remove_deletes_the_registration()
    {
        var store = new FakeStartupStore { Command = $"\"{ExeA}\"" };
        var registrar = new StartupRegistrar(store);

        registrar.Remove();

        Assert.Null(store.Command);
    }

    [Fact]
    public void Remove_is_a_noop_when_nothing_was_written()
    {
        var store = new FakeStartupStore();
        var registrar = new StartupRegistrar(store);

        var ex = Record.Exception(() => registrar.Remove());

        Assert.Null(ex);
        Assert.Null(store.Command);
    }

    [Fact]
    public void Remove_swallows_a_store_failure_so_uninstall_is_never_blocked()
    {
        var registrar = new StartupRegistrar(new ThrowingStartupStore());

        var ex = Record.Exception(() => registrar.Remove());

        Assert.Null(ex);
    }

    private sealed class FakeStartupStore : IStartupRegistrationStore
    {
        public string? Command { get; set; }
        public int WriteCount { get; set; }

        public string? GetRegisteredCommand() => Command;
        public void SetRegisteredCommand(string command)
        {
            Command = command;
            WriteCount++;
        }
        public void RemoveRegistration() => Command = null;
    }

    private sealed class ThrowingStartupStore : IStartupRegistrationStore
    {
        public string? GetRegisteredCommand() => null;
        public void SetRegisteredCommand(string command) => throw new UnauthorizedAccessException("locked-down HKCU");
        public void RemoveRegistration() => throw new UnauthorizedAccessException("locked-down HKCU");
    }
}
