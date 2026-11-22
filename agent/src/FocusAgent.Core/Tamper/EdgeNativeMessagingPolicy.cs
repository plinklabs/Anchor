namespace FocusAgent.Core.Tamper;

/// <summary>
/// Edge's native-messaging policies in one policy hive (#561), read from
/// <c>Software\Policies\Microsoft\Edge</c> under HKLM or HKCU. Any of them can
/// keep Edge from starting the witness host, which the agent registers per user
/// (HKCU): an organisation that set them on a laptop leaves the extension with
/// no link, and the agent with nothing in its log to say why.
/// </summary>
/// <param name="UserLevelHosts">
/// <c>NativeMessagingUserLevelHosts</c>: false when only hosts registered for the
/// whole machine may start, which rules the agent's out. Null: not set.
/// </param>
/// <param name="Blocklist">
/// <c>NativeMessagingBlocklist</c>: the host names that may not start; <c>*</c>
/// blocks every host the allowlist doesn't name. Null: not set.
/// </param>
/// <param name="Allowlist">
/// <c>NativeMessagingAllowlist</c>: the host names exempt from the blocklist.
/// Null: not set.
/// </param>
public sealed record EdgeNativeMessagingPolicy(
    bool? UserLevelHosts,
    IReadOnlyList<string>? Blocklist,
    IReadOnlyList<string>? Allowlist)
{
    /// <summary>No policy set.</summary>
    public static EdgeNativeMessagingPolicy None { get; } = new(null, null, null);

    /// <summary>
    /// The policy Edge applies: per setting, the machine's when it sets one,
    /// otherwise the user's. Edge doesn't merge the two lists.
    /// </summary>
    public static EdgeNativeMessagingPolicy Effective(EdgeNativeMessagingPolicy machine, EdgeNativeMessagingPolicy user) =>
        new(machine.UserLevelHosts ?? user.UserLevelHosts,
            machine.Blocklist ?? user.Blocklist,
            machine.Allowlist ?? user.Allowlist);

    /// <summary>Only hosts registered for the whole machine may start: not the agent's (HKCU).</summary>
    public bool BlocksUserLevelHosts => UserLevelHosts == false;

    /// <summary>
    /// The blocklist names <paramref name="hostName"/>, or blocks every host with
    /// <c>*</c>, and the allowlist doesn't exempt it.
    /// </summary>
    public bool Blocks(string hostName) =>
        Blocklist is { } blocked
        && blocked.Any(b => b.Trim() == "*" || string.Equals(b.Trim(), hostName, StringComparison.OrdinalIgnoreCase))
        && !(Allowlist?.Any(a => string.Equals(a.Trim(), hostName, StringComparison.OrdinalIgnoreCase)) ?? false);
}

/// <summary>Reads <see cref="EdgeNativeMessagingPolicy"/> from the registry (#561).</summary>
public interface IEdgeNativeMessagingPolicySource
{
    /// <summary>The policy under HKLM (<paramref name="machine"/>) or HKCU.</summary>
    EdgeNativeMessagingPolicy Read(bool machine);
}
