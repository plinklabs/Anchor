using System.Diagnostics;
using System.Runtime.Versioning;
using FocusAgent.Native;

namespace FocusAgent.Native.Tests;

/// <summary>
/// #487: <see cref="ProcessQueries"/> against real processes: the parent a child
/// reports, and the command line it was started with, which the Velopack hooks read
/// from the updater or installer running them.
/// </summary>
[SupportedOSPlatform("windows")]
public class ProcessQueriesTests
{
    [Fact]
    public void A_child_names_the_process_that_started_it_as_its_parent()
    {
        using var child = StartChild("-n 120 127.0.0.1");
        try
        {
            Assert.Equal(Environment.ProcessId, ProcessQueries.ParentIdOf(child.Id));
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public void A_childs_command_line_is_the_one_it_was_started_with()
    {
        using var child = StartChild("-n 120 -w 1000 127.0.0.1");
        try
        {
            var commandLine = ProcessQueries.CommandLineOf(child.Id);

            Assert.NotNull(commandLine);
            Assert.EndsWith("-n 120 -w 1000 127.0.0.1", commandLine);
            Assert.Contains("PING.EXE", commandLine, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public void A_long_command_line_is_read_whole()
    {
        // Longer than the first buffer the query tries.
        var padding = string.Join(' ', Enumerable.Repeat("-w 1000", 300));
        using var child = StartChild($"-n 120 {padding} 127.0.0.1");
        try
        {
            Assert.EndsWith($"{padding} 127.0.0.1", ProcessQueries.CommandLineOf(child.Id));
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public void This_process_reads_its_own_command_line()
    {
        var commandLine = ProcessQueries.CommandLineOf(Environment.ProcessId);

        Assert.False(string.IsNullOrEmpty(commandLine));
    }

    [Fact]
    public void A_process_that_isnt_there_has_neither()
    {
        // Far beyond any id Windows hands out. (A real process's id, freed, can be
        // handed to a new process at once, so it can't stand in for "not there".)
        const int pid = 0x7FFFFFFC;

        Assert.Null(ProcessQueries.CommandLineOf(pid));
        Assert.Null(ProcessQueries.ParentIdOf(pid));
    }

    private static Process StartChild(string arguments)
    {
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        var psi = new ProcessStartInfo(ping, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start ping.");
    }
}
