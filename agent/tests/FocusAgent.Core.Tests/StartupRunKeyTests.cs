using FocusAgent.Core.Startup;

namespace FocusAgent.Core.Tests;

public class StartupRunKeyTests
{
    [Fact]
    public void RunKeyPath_is_the_per_user_run_key()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRunKey.RunKeyPath);
    }

    [Fact]
    public void CommandFor_quotes_the_exe_path()
    {
        var path = @"C:\Program Files\Anchor\FocusAgent.App.exe";
        Assert.Equal($"\"{path}\"", StartupRunKey.CommandFor(path));
    }

    [Fact]
    public void CommandFor_trims_surrounding_whitespace()
    {
        Assert.Equal("\"C:\\a\\b.exe\"", StartupRunKey.CommandFor("  C:\\a\\b.exe  "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CommandFor_rejects_a_blank_path(string? path)
    {
        Assert.Throws<ArgumentException>(() => StartupRunKey.CommandFor(path!));
    }

    [Fact]
    public void CommandTargets_matches_the_quoted_command_for_the_exe()
    {
        var exe = @"C:\Apps\FocusAgent.App.exe";
        Assert.True(StartupRunKey.CommandTargets($"\"{exe}\"", exe));
    }

    [Fact]
    public void CommandTargets_matches_an_unquoted_command_for_the_exe()
    {
        var exe = @"C:\Apps\FocusAgent.App.exe";
        Assert.True(StartupRunKey.CommandTargets(exe, exe));
    }

    [Fact]
    public void CommandTargets_is_case_insensitive_on_the_path()
    {
        var exe = @"C:\Apps\FocusAgent.App.exe";
        Assert.True(StartupRunKey.CommandTargets("\"c:\\apps\\focusagent.app.exe\"", exe));
    }

    [Fact]
    public void CommandTargets_does_not_match_a_different_exe()
    {
        Assert.False(StartupRunKey.CommandTargets("\"C:\\old\\FocusAgent.App.exe\"",
            @"C:\new\FocusAgent.App.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CommandTargets_is_false_for_a_blank_command(string? command)
    {
        Assert.False(StartupRunKey.CommandTargets(command, @"C:\a\b.exe"));
    }
}
