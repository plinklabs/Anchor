using Microsoft.Extensions.Configuration;

namespace Anchor.Api.Tests;

/// <summary>
/// Guards issue #201: environment-specific Entra IDs and CORS origins must not
/// live in the committed <c>appsettings.json</c>. Production supplies them via
/// Azure App Service application settings (double-underscore env-var form), and
/// the config binding must read them from there. These tests load the real
/// committed <c>appsettings.json</c> (copied to the test output directory) and
/// exercise the same layering ASP.NET Core uses: base JSON first, then
/// environment variables on top.
/// </summary>
public sealed class ConfigExternalizationTests
{
    private static readonly string CommittedAppSettingsPath =
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    private static IConfigurationRoot BuildBaseConfig() =>
        new ConfigurationBuilder()
            .AddJsonFile(CommittedAppSettingsPath, optional: false)
            .Build();

    [Fact]
    public void CommittedAppSettings_CarriesNoEnvironmentSpecificEntraIds()
    {
        var config = BuildBaseConfig();

        // Placeholders only — no real tenant/client identifiers committed.
        Assert.True(string.IsNullOrEmpty(config["AzureAd:TenantId"]));
        Assert.True(string.IsNullOrEmpty(config["AzureAd:ClientId"]));
        Assert.True(string.IsNullOrEmpty(config["AzureAd:Audience"]));
    }

    [Fact]
    public void CommittedAppSettings_CarriesNoCorsOrigins()
    {
        var config = BuildBaseConfig();

        var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? Array.Empty<string>();
        Assert.Empty(origins);
    }

    [Fact]
    public void CommittedAppSettings_DoesNotLeakTheDevTenant()
    {
        // The previously-committed dev tenant/client IDs must be gone from the
        // committed base file (they now live only in appsettings.Development.json).
        var raw = File.ReadAllText(CommittedAppSettingsPath);
        Assert.DoesNotContain("8ee90830-e251-45a0-bf95-abdf72738b07", raw);
        Assert.DoesNotContain("c9ba7c0e-763d-4a1b-9d95-894f54fb16da", raw);
        Assert.DoesNotContain("azurestaticapps.net", raw);
    }

    [Fact]
    public void EntraIds_BindFromEnvironmentVariables_DoubleUnderscoreForm()
    {
        // Mirrors how Azure App Service injects application settings: real
        // process environment variables with "__" translated onto config keys
        // by the environment-variable provider, layered over the committed JSON.
        using var _ = SetEnvVars(new Dictionary<string, string?>
        {
            ["AzureAd__TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["AzureAd__ClientId"] = "22222222-2222-2222-2222-222222222222",
            ["AzureAd__Audience"] = "api://prod-anchor",
        });

        var config = new ConfigurationBuilder()
            .AddJsonFile(CommittedAppSettingsPath, optional: false)
            .AddEnvironmentVariables()
            .Build();

        Assert.Equal("11111111-1111-1111-1111-111111111111", config["AzureAd:TenantId"]);
        Assert.Equal("22222222-2222-2222-2222-222222222222", config["AzureAd:ClientId"]);
        Assert.Equal("api://prod-anchor", config["AzureAd:Audience"]);
    }

    [Fact]
    public void CorsOrigins_BindFromEnvironmentVariables_IndexedForm()
    {
        // App Service supplies array elements as Cors__AllowedOrigins__0, __1, ...
        using var _ = SetEnvVars(new Dictionary<string, string?>
        {
            ["Cors__AllowedOrigins__0"] = "https://app.example.org",
            ["Cors__AllowedOrigins__1"] = "https://admin.example.org",
        });

        var config = new ConfigurationBuilder()
            .AddJsonFile(CommittedAppSettingsPath, optional: false)
            .AddEnvironmentVariables()
            .Build();

        var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? Array.Empty<string>();
        Assert.Equal(
            new[] { "https://app.example.org", "https://admin.example.org" },
            origins);
    }

    /// <summary>
    /// Sets process environment variables for the duration of a test and
    /// restores their prior values on dispose, so cases stay hermetic.
    /// </summary>
    private static IDisposable SetEnvVars(IReadOnlyDictionary<string, string?> vars)
        => new EnvVarScope(vars);

    private sealed class EnvVarScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new();

        public EnvVarScope(IReadOnlyDictionary<string, string?> vars)
        {
            foreach (var (key, value) in vars)
            {
                _previous[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in _previous)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
