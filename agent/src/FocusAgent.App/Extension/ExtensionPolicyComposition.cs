using System.Runtime.Versioning;
using FocusAgent.Core.Extension;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace FocusAgent.App.Extension;

/// <summary>The registry, elevation and state parts the extension setup runs on (#405).</summary>
internal sealed record ExtensionPolicyParts(
    IExtensionPolicyStore Machine,
    IExtensionPolicyStore User,
    IElevatedPolicyWriter ElevatedWriter,
    IExtensionPolicyStateStore State,
    TimeSpan GracePeriod);

/// <summary>
/// Picks the parts of the Edge extension setup for this launch (#405), and is the
/// one place that decides whether a real UAC prompt is possible: only on a
/// user-facing release launch (<paramref name="allowRealElevation"/>). Every other
/// run — <c>dotnet run</c>, the e2e, any dev flag — gets a writer that never
/// elevates, and only there are the test knobs in <see cref="ExtensionSettings"/>
/// honoured.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ExtensionPolicyComposition
{
    public static ExtensionPolicyParts Compose(
        ExtensionSettings settings, bool allowRealElevation, ILoggerFactory loggers)
    {
        var storeLog = loggers.CreateLogger<RegistryExtensionPolicyStore>();

        if (allowRealElevation)
        {
            return new ExtensionPolicyParts(
                RegistryExtensionPolicyStore.ForMachine(storeLog),
                RegistryExtensionPolicyStore.ForUser(log: storeLog),
                new ShellElevatedPolicyWriter(loggers.CreateLogger<ShellElevatedPolicyWriter>()),
                new FileExtensionPolicyStateStore(),
                ExtensionSelfRegistrar.DefaultGracePeriod);
        }

        // A dev or test run: never a real UAC prompt.
        var root = settings.TestPolicyRoot.Trim().TrimEnd('\\');
        var simulated = root.Length > 0;
        IExtensionPolicyStore machine = simulated
            ? new RegistryExtensionPolicyStore(RegistryHive.CurrentUser, $@"{root}\Machine\ExtensionInstallForcelist", storeLog)
            : RegistryExtensionPolicyStore.ForMachine(storeLog);
        IExtensionPolicyStore user = simulated
            ? new RegistryExtensionPolicyStore(RegistryHive.CurrentUser, $@"{root}\User\ExtensionInstallForcelist", storeLog)
            : RegistryExtensionPolicyStore.ForUser(log: storeLog);

        // The simulated answer only ever writes to the simulated (HKCU) machine key.
        IElevatedPolicyWriter writer =
            simulated && SimulatedElevatedPolicyWriter.ParseAnswer(settings.SimulateElevation) is { } answer
                ? new SimulatedElevatedPolicyWriter(machine, answer, loggers.CreateLogger<SimulatedElevatedPolicyWriter>())
                : new DisabledElevatedPolicyWriter(loggers.CreateLogger<DisabledElevatedPolicyWriter>());

        IExtensionPolicyStateStore state = string.IsNullOrWhiteSpace(settings.StatePath)
            ? new InMemoryExtensionPolicyStateStore()
            : new FileExtensionPolicyStateStore(settings.StatePath);

        var grace = settings.GracePeriodSeconds is int seconds and > 0
            ? TimeSpan.FromSeconds(seconds)
            : ExtensionSelfRegistrar.DefaultGracePeriod;

        return new ExtensionPolicyParts(machine, user, writer, state, grace);
    }
}
