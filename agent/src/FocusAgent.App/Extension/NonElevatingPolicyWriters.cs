using FocusAgent.Core.Extension;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.App.Extension;

/// <summary>
/// The <see cref="IElevatedPolicyWriter"/> for dev and test runs (#405): it never
/// elevates and reports every write as failed, so the flow falls back to the
/// guided install exactly as on a box where the write didn't work. A real UAC
/// prompt in a dev or test run would take over the secure desktop and wait for an
/// answer nobody is there to give.
/// </summary>
public sealed class DisabledElevatedPolicyWriter : IElevatedPolicyWriter
{
    private readonly ILogger _log;

    public DisabledElevatedPolicyWriter(ILogger<DisabledElevatedPolicyWriter>? log = null) =>
        _log = log ?? NullLogger<DisabledElevatedPolicyWriter>.Instance;

    public Task<ElevatedPolicyWriteResult> AddMachineEntryAsync(
        string valueName, string entry, CancellationToken ct = default)
    {
        _log.LogWarning(
            "Not asking for elevation in a dev/test run; the HKLM Edge policy write is skipped " +
            "(a release build on a student's laptop shows the UAC prompt here).");
        return Task.FromResult(ElevatedPolicyWriteResult.Failed);
    }

    public Task StartRemovingMachineEntry(string valueName) => Task.CompletedTask;
}

/// <summary>
/// The test-only <see cref="IElevatedPolicyWriter"/> (#405) the agent e2e opts
/// into: it plays the student's answer to the UAC prompt without showing one. On
/// "yes" it performs the write itself against the (throwaway) machine store, the
/// way the elevated <c>reg.exe</c> would; otherwise it returns the configured
/// answer. Only ever composed in a dev/test run, never in a release build's real
/// startup.
/// </summary>
public sealed class SimulatedElevatedPolicyWriter : IElevatedPolicyWriter
{
    private readonly IExtensionPolicyStore _machine;
    private readonly ElevatedPolicyWriteResult _answer;
    private readonly ILogger _log;

    public SimulatedElevatedPolicyWriter(
        IExtensionPolicyStore machineStore,
        ElevatedPolicyWriteResult answer,
        ILogger<SimulatedElevatedPolicyWriter>? log = null)
    {
        _machine = machineStore;
        _answer = answer;
        _log = log ?? NullLogger<SimulatedElevatedPolicyWriter>.Instance;
    }

    /// <summary>
    /// Parse the <c>Extension:SimulateElevation</c> setting: <c>allow</c>,
    /// <c>decline</c> or <c>fail</c>. Anything else is null (no simulation).
    /// </summary>
    public static ElevatedPolicyWriteResult? ParseAnswer(string? setting) =>
        setting?.Trim().ToLowerInvariant() switch
        {
            "allow" => ElevatedPolicyWriteResult.Succeeded,
            "decline" => ElevatedPolicyWriteResult.Declined,
            "fail" => ElevatedPolicyWriteResult.Failed,
            _ => null,
        };

    public Task<ElevatedPolicyWriteResult> AddMachineEntryAsync(
        string valueName, string entry, CancellationToken ct = default)
    {
        _log.LogWarning("Simulating the UAC prompt (test run): answer {Answer}.", _answer);
        if (_answer == ElevatedPolicyWriteResult.Succeeded)
            _machine.AddForcelistEntry(valueName, entry);
        return Task.FromResult(_answer);
    }

    public Task StartRemovingMachineEntry(string valueName)
    {
        if (_answer == ElevatedPolicyWriteResult.Succeeded)
            _machine.RemoveAnchorForcelistEntries();
        return Task.CompletedTask;
    }
}
