using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusAgent.WitnessHost;

/// <summary>
/// Resolves the production student-auth config the witness host hands down to the
/// extension over the native-messaging link (#289), the exact sibling of
/// <see cref="BackendUrlConfig"/> (#204).
///
/// The extension is deployment-agnostic: a single published listing serves every
/// fork, so it must not bake in any tenant/client. It learns its Entra
/// tenant/client/scope from the on-box agent at runtime instead, and uses them to
/// acquire an Entra access token via <c>chrome.identity.launchWebAuthFlow</c> for
/// the SignalR hub. Only the registered native host can reach the extension's
/// witness port, so an arbitrary web page can't repoint it.
///
/// Source of truth, in order (mirrors <see cref="BackendUrlConfig"/>):
///   1. the <c>ANCHOR_WITNESS_AUTH_*</c> env vars — what a test harness can set.
///   2. an <c>auth-config.json</c> file next to the host exe — what the agent's
///      <c>WitnessHostRegistrar</c> writes from its configured <c>Auth</c> section.
///
/// Unlike the backend URL there is NO dev fallback: a dev box authenticates with
/// the <c>dev_impersonate_oid</c> shortcut, so when nothing configures real auth
/// the host simply sends no <c>auth_config</c> and the extension stays on the dev
/// path. All three values must be present for a config to be considered usable —
/// a half-filled config can't drive a valid sign-in.
/// </summary>
public static class AuthConfig
{
    /// <summary>Env vars a test harness can set per run (sibling of <see cref="BackendUrlConfig.EnvVarName"/>).</summary>
    public const string TenantIdEnvVar = "ANCHOR_WITNESS_AUTH_TENANT_ID";
    public const string ClientIdEnvVar = "ANCHOR_WITNESS_AUTH_CLIENT_ID";
    public const string ScopeEnvVar = "ANCHOR_WITNESS_AUTH_SCOPE";

    /// <summary>
    /// Config file the agent's registrar drops next to the exe. MUST match
    /// <c>WitnessHostManifest.AuthConfigFileName</c> in FocusAgent.Core (Core can't
    /// reference the host project, so the literal is duplicated there under test).
    /// </summary>
    public const string FileName = "auth-config.json";

    /// <summary>The resolved auth config; every field is non-blank.</summary>
    public sealed record Values(string TenantId, string ClientId, string Scope);

    // Shape of the file the registrar writes / the env-var resolution, and the
    // shape the extension's witness.ts parses off the auth_config message.
    private sealed record AuthConfigFile(
        [property: JsonPropertyName("tenantId")] string? TenantId,
        [property: JsonPropertyName("clientId")] string? ClientId,
        [property: JsonPropertyName("scope")] string? Scope);

    private sealed record AuthConfigMessage(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("tenantId")] string TenantId,
        [property: JsonPropertyName("clientId")] string ClientId,
        [property: JsonPropertyName("scope")] string Scope);

    /// <summary>
    /// The native message the host sends the extension. <c>witness.ts</c>
    /// classifies <c>type: "auth_config"</c> and stores the tenant/client/scope.
    /// </summary>
    public static string BuildMessage(Values values) =>
        JsonSerializer.Serialize(
            new AuthConfigMessage("auth_config", values.TenantId, values.ClientId, values.Scope));

    /// <summary>
    /// Resolve the auth config from the env vars, then the optional file next to
    /// the exe, then null (no real auth configured). Pure-ish: the IO sources are
    /// injected so the precedence is unit-tested without the real environment or
    /// filesystem. Returns null unless all three fields resolve to non-blank.
    /// </summary>
    public static Values? Resolve(
        Func<string, string?> getEnv,
        Func<string?> readConfigFile)
    {
        var fromEnv = Compose(
            getEnv(TenantIdEnvVar), getEnv(ClientIdEnvVar), getEnv(ScopeEnvVar));
        if (fromEnv is not null) return fromEnv;

        var file = ReadFromJson(readConfigFile());
        if (file is null) return null;
        return Compose(file.TenantId, file.ClientId, file.Scope);
    }

    /// <summary>Runtime resolution against the real environment + exe directory.</summary>
    public static Values? ResolveFromEnvironment() =>
        Resolve(
            Environment.GetEnvironmentVariable,
            () =>
            {
                try
                {
                    var path = Path.Combine(AppContext.BaseDirectory, FileName);
                    return File.Exists(path) ? File.ReadAllText(path) : null;
                }
                catch
                {
                    // A malformed/unreadable config file must not crash the host;
                    // fall through to "no auth configured".
                    return null;
                }
            });

    private static AuthConfigFile? ReadFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<AuthConfigFile>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // All three fields must be present and non-blank for a usable config.
    private static Values? Compose(string? tenantId, string? clientId, string? scope)
    {
        var t = Normalize(tenantId);
        var c = Normalize(clientId);
        var s = Normalize(scope);
        return t is null || c is null || s is null ? null : new Values(t, c, s);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
