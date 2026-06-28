using System.Text.Json;
using FocusAgent.WitnessHost;

namespace FocusAgent.WitnessHost.Tests;

/// <summary>
/// The production-auth hand-off the host gives the extension (#289), the sibling of
/// <see cref="BackendUrlConfigTests"/>. These lock the resolution precedence
/// (env → file → none), the all-or-nothing rule (a half-filled config is no config),
/// and the native-message shape the extension's witness.ts parses.
/// </summary>
public class AuthConfigTests
{
    private static string? NoEnv(string _) => null;
    private static string? NoFile() => null;

    private static string EnvFor(string name) => name switch
    {
        AuthConfig.TenantIdEnvVar => "tenant-env",
        AuthConfig.ClientIdEnvVar => "client-env",
        AuthConfig.ScopeEnvVar => "scope-env",
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Fact]
    public void FileName_matches_the_witness_host_manifest_literal()
    {
        // WitnessHostManifest.AuthConfigFileName in Core duplicates this literal (Core
        // can't reference the host project); the two must never drift.
        Assert.Equal("auth-config.json", AuthConfig.FileName);
    }

    [Fact]
    public void Env_vars_win_over_everything()
    {
        var values = AuthConfig.Resolve(
            getEnv: EnvFor,
            readConfigFile: () => "{\"tenantId\":\"tenant-file\",\"clientId\":\"client-file\",\"scope\":\"scope-file\"}");

        Assert.NotNull(values);
        Assert.Equal("tenant-env", values!.TenantId);
        Assert.Equal("client-env", values.ClientId);
        Assert.Equal("scope-env", values.Scope);
    }

    [Fact]
    public void Falls_back_to_the_config_file_when_no_env_vars()
    {
        var values = AuthConfig.Resolve(
            getEnv: NoEnv,
            readConfigFile: () => "{\"tenantId\":\"tenant-file\",\"clientId\":\"client-file\",\"scope\":\"scope-file\"}");

        Assert.NotNull(values);
        Assert.Equal("tenant-file", values!.TenantId);
        Assert.Equal("client-file", values.ClientId);
        Assert.Equal("scope-file", values.Scope);
    }

    [Fact]
    public void Returns_null_when_nothing_is_configured()
    {
        // No dev fallback (unlike the backend URL): a dev box uses dev_impersonate_oid,
        // so "no auth configured" must yield no auth_config and leave the extension on
        // the dev path.
        Assert.Null(AuthConfig.Resolve(NoEnv, NoFile));
    }

    [Fact]
    public void A_partial_env_config_is_no_config()
    {
        // Tenant + client present but scope blank → unusable; don't fall through to a
        // half-filled config that can't drive a valid sign-in.
        var values = AuthConfig.Resolve(
            getEnv: name => name == AuthConfig.ScopeEnvVar ? "  " : EnvFor(name),
            readConfigFile: NoFile);

        Assert.Null(values);
    }

    [Fact]
    public void A_partial_file_config_is_no_config()
    {
        var values = AuthConfig.Resolve(
            getEnv: NoEnv,
            readConfigFile: () => "{\"tenantId\":\"tenant-file\",\"clientId\":\"\",\"scope\":\"scope-file\"}");

        Assert.Null(values);
    }

    [Fact]
    public void Values_are_trimmed()
    {
        var values = AuthConfig.Resolve(
            getEnv: name => name switch
            {
                AuthConfig.TenantIdEnvVar => "  tenant  ",
                AuthConfig.ClientIdEnvVar => "  client  ",
                AuthConfig.ScopeEnvVar => "  scope  ",
                _ => null,
            },
            readConfigFile: NoFile);

        Assert.NotNull(values);
        Assert.Equal("tenant", values!.TenantId);
        Assert.Equal("client", values.ClientId);
        Assert.Equal("scope", values.Scope);
    }

    [Fact]
    public void Malformed_config_file_yields_null()
    {
        Assert.Null(AuthConfig.Resolve(NoEnv, () => "{ not json"));
    }

    [Fact]
    public void BuildMessage_emits_the_shape_witness_ts_parses()
    {
        var json = AuthConfig.BuildMessage(new AuthConfig.Values("tenant-1", "client-1", "api://x/.default"));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("auth_config", root.GetProperty("type").GetString());
        Assert.Equal("tenant-1", root.GetProperty("tenantId").GetString());
        Assert.Equal("client-1", root.GetProperty("clientId").GetString());
        Assert.Equal("api://x/.default", root.GetProperty("scope").GetString());
    }
}
