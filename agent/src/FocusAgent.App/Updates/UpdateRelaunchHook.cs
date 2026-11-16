using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using FocusAgent.Core.Updates;
using FocusAgent.Native;
using Velopack.Locators;
using Velopack.Logging;

namespace FocusAgent.App.Updates;

/// <summary>
/// The relaunch half of the Velopack install and update hooks (#487): when the
/// updater or installer running the hook won't start the agent again, start a waiter
/// that does once it has exited. See <see cref="UpdateRelaunch"/> for why, and why the
/// waiter runs from outside the install folder.
///
/// <para>
/// The hook must return at once: Update.exe gives it 15 s (Setup.exe 30 s) and is
/// waiting on it. Starting the waiter is the last thing it does; the waiter is not
/// waited on. Every outcome goes to Velopack's log, next to the updater's own lines
/// for the same apply. Best-effort: nothing here may fail the install or update.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UpdateRelaunchHook
{
    // The variables Velopack's updater sets on the agent it starts itself. The waiter's
    // agent wasn't started by the updater, so they must not reach it.
    private static readonly string[] VelopackStartVariables = ["VELOPACK_RESTART", "VELOPACK_FIRSTRUN"];

    public static void Run(VelopackHookKind hook)
    {
        IVelopackLogger? log = null;
        try
        {
            log = VelopackLocator.IsCurrentSet ? VelopackLocator.Current.Log : null;

            bool runsAsSystem;
            using (var identity = WindowsIdentity.GetCurrent())
                runsAsSystem = identity.IsSystem;
            using var self = Process.GetCurrentProcess();
            var parent = FindParent(self);
            var parentCommandLine = parent is null ? null : ProcessQueries.CommandLineOf(parent.Value.Id);
            var plan = UpdateRelaunch.Plan(hook, parentCommandLine, runsAsSystem, self.SessionId);

            var parentText = parent is { } p ? $"{p.Name} (pid {p.Id})" : "<unknown>";
            if (parent is not null && parentCommandLine is null)
                log?.LogWarning($"Anchor relaunch: could not read the command line of {parentText}.");

            switch (plan.Decision)
            {
                case RelaunchDecision.UpdaterRestarts:
                    log?.LogInformation($"Anchor relaunch: {parentText} starts the agent again itself; not relaunching.");
                    return;
                case RelaunchDecision.SetupStarts:
                    log?.LogInformation($"Anchor relaunch: {parentText} isn't silent and starts the agent itself; not relaunching.");
                    return;
                case RelaunchDecision.NotInUserSession:
                    log?.LogInformation(
                        $"Anchor relaunch: running as SYSTEM ({runsAsSystem}) or in session {self.SessionId}, " +
                        "not in a signed-in user's session; not relaunching.");
                    return;
            }

            if (parent is null)
            {
                log?.LogWarning(
                    "Anchor relaunch: could not find the updater or installer running this hook, so there is " +
                    "nothing to wait for; not relaunching (it would be stopped along with the install folder).");
                return;
            }

            var exe = Environment.ProcessPath
                      ?? throw new InvalidOperationException("Could not resolve the agent's own path.");
            var waiter = StartWaiter(exe, plan.AgentArguments, parent.Value);
            log?.LogInformation(
                $"Anchor relaunch: {parentText} won't start the agent again; a waiter (powershell.exe, pid {waiter}) " +
                $"starts {exe} {UpdateRelaunch.JoinArguments(plan.AgentArguments)} once it has exited.");
        }
        catch (Exception ex)
        {
            try { log?.LogWarning(ex, "Anchor relaunch: could not hand the agent's start to a waiter."); }
            catch { /* the hook must not fail */ }
        }
    }

    // The updater or installer running this hook: the process that started it, which
    // waits for the hook to return. Checked to have started first, so a process that
    // took over the id of an exited parent isn't it.
    private static (int Id, string Name)? FindParent(Process self)
    {
        if (ProcessQueries.ParentIdOf(self.Id) is not { } parentId || parentId <= 0) return null;
        try
        {
            using var parent = Process.GetProcessById(parentId);
            string name;
            try { name = parent.ProcessName; }
            catch { name = string.Empty; }
            try
            {
                if (parent.StartTime > self.StartTime) return null;
            }
            catch
            {
                // Its start time can't be read (another integrity level); trust the id.
            }
            return (parentId, name);
        }
        catch (ArgumentException)
        {
            return null; // already gone
        }
    }

    private static int StartWaiter(string agentExe, IReadOnlyList<string> agentArguments, (int Id, string Name) waitFor)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Not the install folder: an open working directory there would hold it
            // while the updater tidies up, and keep the next update from renaming it.
            WorkingDirectory = Environment.SystemDirectory,
        };
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(UpdateRelaunch.WaiterScript);
        psi.Environment[UpdateRelaunch.ExeEnvVar] = agentExe;
        psi.Environment[UpdateRelaunch.ArgumentsEnvVar] = UpdateRelaunch.JoinArguments(agentArguments);
        psi.Environment[UpdateRelaunch.WaitPidEnvVar] = waitFor.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment[UpdateRelaunch.WaitNameEnvVar] = waitFor.Name;
        foreach (var name in VelopackStartVariables)
            psi.Environment.Remove(name);

        using var waiter = Process.Start(psi)
                           ?? throw new InvalidOperationException("powershell.exe did not start.");
        return waiter.Id;
    }
}
