using System.Runtime.Versioning;
using FocusAgent.Core.Tamper;
using Microsoft.Win32;

namespace FocusAgent.App.Tamper;

/// <summary>
/// Reads Edge's native-messaging policies from <c>Software\Policies\Microsoft\Edge</c>
/// under HKLM and HKCU (#561), for the link self-check. Read only; a policy that
/// can't be read counts as not set.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryEdgeNativeMessagingPolicySource : IEdgeNativeMessagingPolicySource
{
    private const string EdgePolicyKey = @"Software\Policies\Microsoft\Edge";

    public EdgeNativeMessagingPolicy Read(bool machine)
    {
        try
        {
            using var edge = (machine ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(EdgePolicyKey);
            if (edge is null) return EdgeNativeMessagingPolicy.None;
            bool? userLevelHosts = edge.GetValue("NativeMessagingUserLevelHosts") is int value ? value != 0 : null;
            return new EdgeNativeMessagingPolicy(userLevelHosts, List(edge, "NativeMessagingBlocklist"), List(edge, "NativeMessagingAllowlist"));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return EdgeNativeMessagingPolicy.None;
        }
    }

    // A list policy is a subkey whose values ("1", "2", ...) are the entries.
    private static IReadOnlyList<string>? List(RegistryKey edge, string name)
    {
        using var list = edge.OpenSubKey(name);
        if (list is null) return null;
        return list.GetValueNames()
            .Select(value => list.GetValue(value) as string)
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry!)
            .ToList();
    }
}
