using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using FocusAgent.Core.Extension;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.App.Extension;

/// <summary>
/// The real <see cref="IElevatedPolicyWriter"/> (#405): has Windows run the
/// system <c>reg.exe</c> with ShellExecute's <c>runas</c> verb, which shows one UAC
/// prompt, to add (or on uninstall delete) Anchor's value under
/// <c>HKLM\Software\Policies\Microsoft\Edge\ExtensionInstallForcelist</c>.
///
/// Why <c>reg.exe</c> rather than the agent elevating itself: the prompt then
/// names a Microsoft-signed Windows tool (a blue prompt, "Verified publisher:
/// Microsoft Windows") instead of an unsigned app; no agent code ever runs
/// elevated (in particular not Velopack's startup, which could apply a downloaded
/// update as the admin); and on uninstall it doesn't depend on the app folder
/// Velopack is about to delete.
///
/// <b>Never used in a dev or test run</b> — the composition root swaps in a writer
/// that doesn't elevate, because a real prompt takes over the secure desktop and
/// waits for an answer nobody is there to give.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellElevatedPolicyWriter : IElevatedPolicyWriter
{
    // ShellExecuteEx's error when the user says No (or cancels the credential prompt).
    private const int ErrorCancelled = 1223;

    // Generous: reg.exe itself takes milliseconds once the prompt is answered.
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(2);

    private readonly ILogger<ShellElevatedPolicyWriter> _log;

    public ShellElevatedPolicyWriter(ILogger<ShellElevatedPolicyWriter>? log = null) =>
        _log = log ?? NullLogger<ShellElevatedPolicyWriter>.Instance;

    /// <summary>The system <c>reg.exe</c> — by full path, never resolved off PATH.</summary>
    public static string RegExePath => Path.Combine(Environment.SystemDirectory, "reg.exe");

    public async Task<ElevatedPolicyWriteResult> AddMachineEntryAsync(
        string valueName, string entry, CancellationToken ct = default)
    {
        var start = Elevated(EdgeExtensionPolicy.RegAddArguments(valueName, entry));

        Process? process;
        try
        {
            // ShellExecuteEx blocks until the student answers the UAC prompt, so keep
            // it off the caller's thread (the UI thread, for the tray action).
            process = await Task.Run(() => Process.Start(start), ct).ConfigureAwait(false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            _log.LogInformation("The UAC prompt for the Edge policy write was declined.");
            return ElevatedPolicyWriteResult.Declined;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Starting the elevated Edge policy write failed.");
            return ElevatedPolicyWriteResult.Failed;
        }

        if (process is null)
            return ElevatedPolicyWriteResult.Failed;

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(ct).WaitAsync(WriteTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.LogWarning("The elevated Edge policy write didn't finish within {Timeout}.", WriteTimeout);
                return ElevatedPolicyWriteResult.Failed;
            }

            if (process.ExitCode == 0)
                return ElevatedPolicyWriteResult.Succeeded;

            _log.LogWarning("The elevated Edge policy write exited with code {ExitCode}.", process.ExitCode);
            return ElevatedPolicyWriteResult.Failed;
        }
    }

    public Task StartRemovingMachineEntry(string valueName)
    {
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProcessStartInfo start;
        try
        {
            start = Elevated(EdgeExtensionPolicy.RegDeleteArguments(valueName));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Not removing HKLM forcelist value {Slot}.", valueName);
            return Task.CompletedTask;
        }

        // Its own thread, because ShellExecuteEx blocks until the prompt is answered
        // and the uninstall hook must decide for itself how long to wait for that.
        // reg.exe then runs on its own: nothing waits for it to finish.
        var thread = new Thread(() =>
        {
            try
            {
                using var process = Process.Start(start);
                _log.LogInformation("Started the elevated removal of HKLM forcelist value {Slot}.", valueName);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                _log.LogInformation("The UAC prompt for removing the Edge policy was declined.");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Starting the elevated Edge policy removal failed.");
            }
            finally
            {
                answered.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "anchor-edge-policy-removal",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return answered.Task;
    }

    private static ProcessStartInfo Elevated(string arguments) => new(RegExePath, arguments)
    {
        UseShellExecute = true,
        Verb = "runas",
        WindowStyle = ProcessWindowStyle.Hidden,
        ErrorDialog = false,
    };
}
