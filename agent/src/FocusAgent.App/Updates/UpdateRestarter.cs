using FocusAgent.Core.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace FocusAgent.App.Updates;

/// <summary>
/// The production <see cref="IAgentRestarter"/> (#456): starts Velopack's updater on
/// the downloaded package, then shuts the agent down on the UI thread so the updater
/// can apply it and start the agent again.
///
/// <para>
/// The updater waits at most 60 s for this process before it kills it (#449). The
/// shutdown takes a second or two; should it ever hang, a backstop ends the process
/// after <see cref="ExitDeadline"/>, still well inside that wait, so the agent always
/// exits on its own.
/// </para>
///
/// <para>
/// The restart arguments are the ones this launch got: none for the sign-in launch
/// (the Run entry passes none), so the agent comes back in the tray with no window,
/// toast or prompt, exactly like at sign-in.
/// </para>
/// </summary>
internal sealed class UpdateRestarter : IAgentRestarter
{
    /// <summary>Longest the agent takes to exit once the updater waits for it.</summary>
    public static readonly TimeSpan ExitDeadline = TimeSpan.FromSeconds(20);

    private readonly IAgentUpdateManager _manager;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _exitOnUiThread;
    private readonly ILogger _log;

    public UpdateRestarter(IAgentUpdateManager manager, DispatcherQueue dispatcher, Action exitOnUiThread, ILogger log)
    {
        _manager = manager;
        _dispatcher = dispatcher;
        _exitOnUiThread = exitOnUiThread;
        _log = log;
    }

    public bool TryRestartIntoUpdate(string version)
    {
        var restartArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
        try
        {
            _manager.ApplyOnExitAndRestart(restartArgs);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not start Velopack's updater for update {Version}.", version);
            return false;
        }

        _log.LogInformation(
            "Velopack's updater is waiting for the agent to exit; exiting to restart into {Version}.", version);

        _ = Task.Delay(ExitDeadline).ContinueWith(
            _ =>
            {
                _log.LogWarning(
                    "The agent had not exited {Seconds:N0} s after handing update {Version} to the updater; ending it now.",
                    ExitDeadline.TotalSeconds, version);
                Environment.Exit(0);
            },
            TaskScheduler.Default);

        if (!_dispatcher.TryEnqueue(() => _exitOnUiThread()))
            Environment.Exit(0);
        return true;
    }
}
