using System.Runtime.Versioning;
using FocusAgent.Core.Extension;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace FocusAgent.App.Extension;

/// <summary>
/// The registry, elevation and state parts the extension setup runs on (#405), the
/// Edge profiles it looks for the installed extension in (#488), and the extension
/// IDs that are Anchor's (#497), which the witness host registration lets connect.
/// </summary>
internal sealed record ExtensionPolicyParts(
    IExtensionPolicyStore Machine,
    IExtensionPolicyStore User,
    IElevatedPolicyWriter ElevatedWriter,
    IExtensionPolicyStateStore State,
    TimeSpan GracePeriod,
    EdgeProfileExtensionProbe Profiles,
    AnchorExtensionIds Ids);

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
        var probeLog = loggers.CreateLogger<EdgeProfileExtensionProbe>();
        var edgeUserData = EdgeProfileExtensionProbe.UserDataFolders(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        if (allowRealElevation)
        {
            // #497: the IDs extension/test-ring.json named when this agent was built.
            var ids = AnchorExtensionIds.Configured;
            return new ExtensionPolicyParts(
                RegistryExtensionPolicyStore.ForMachine(storeLog),
                RegistryExtensionPolicyStore.ForUser(log: storeLog),
                new ShellElevatedPolicyWriter(loggers.CreateLogger<ShellElevatedPolicyWriter>()),
                new FileExtensionPolicyStateStore(),
                ExtensionSelfRegistrar.DefaultGracePeriod,
                new EdgeProfileExtensionProbe(edgeUserData, ids.All, probeLog),
                ids);
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

        // #497: the e2e plays a test-ring laptop with a test-ring ID of its own.
        var testIds = string.IsNullOrWhiteSpace(settings.TestRingExtensionId)
            ? AnchorExtensionIds.Configured
            : AnchorExtensionIds.WithTestRing(settings.TestRingExtensionId);

        // Reading the real profiles is harmless, but the e2e points at a folder of
        // its own, so whether this box's Edge has the extension can't decide a spec.
        var profiles = new EdgeProfileExtensionProbe(
            string.IsNullOrWhiteSpace(settings.TestEdgeUserDataPath)
                ? edgeUserData
                : [settings.TestEdgeUserDataPath.Trim()],
            testIds.All,
            probeLog);

        return new ExtensionPolicyParts(machine, user, writer, state, grace, profiles, testIds);
    }
}
