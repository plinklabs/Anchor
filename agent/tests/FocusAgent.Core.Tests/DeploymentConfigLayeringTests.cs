using FocusAgent.Core.Settings;
using Microsoft.Extensions.Configuration;

namespace FocusAgent.Core.Tests;

/// <summary>
/// Unit coverage for the #203 config-substitution contract: the agent layers a
/// per-environment file (appsettings.{Environment}.json) over the committed dev
/// defaults (appsettings.json), so a release build's substituted
/// appsettings.Production.json repoints Backend:BaseUrl + Auth without editing the
/// committed source. This mirrors the exact layering App.BuildHost performs
/// (base, then the environment file added last) against the bound settings
/// records, with no WinUI host needed.
/// </summary>
public class DeploymentConfigLayeringTests
{
    private const string BaseJson = """
        {
          "Backend": { "BaseUrl": "http://localhost:5276", "HubPath": "/hubs/session" },
          "Auth": { "TenantId": "", "ClientId": "", "Scope": "dev-scope/.default", "LoginHint": "" }
        }
        """;

    [Fact]
    public void ProductionLayer_OverridesBackendUrlAndAuth_OverDevDefaults()
    {
        // The substituted release file: a fork's own backend + Entra.
        const string productionJson = """
            {
              "Backend": { "BaseUrl": "https://anchor-api-fork.example.net" },
              "Auth": {
                "TenantId": "11111111-1111-1111-1111-111111111111",
                "ClientId": "22222222-2222-2222-2222-222222222222",
                "Scope": "33333333-3333-3333-3333-333333333333/.default"
              }
            }
            """;

        var (backend, auth) = BindLayered(BaseJson, productionJson);

        Assert.Equal("https://anchor-api-fork.example.net", backend.BaseUrl);
        Assert.Equal("11111111-1111-1111-1111-111111111111", auth.TenantId);
        Assert.Equal("22222222-2222-2222-2222-222222222222", auth.ClientId);
        Assert.Equal("33333333-3333-3333-3333-333333333333/.default", auth.Scope);

        // A key the deployment layer omits falls through to the committed default.
        Assert.Equal("/hubs/session", backend.HubPath);
    }

    [Fact]
    public void NoDeploymentLayer_KeepsCommittedDevDefaults()
    {
        // A plain dev run with no per-environment override: the agent stays on the
        // committed appsettings.json values (this is what every non-Production run
        // relies on).
        var (backend, auth) = BindLayered(BaseJson, overrideJson: null);

        Assert.Equal("http://localhost:5276", backend.BaseUrl);
        Assert.Equal("dev-scope/.default", auth.Scope);
    }

    private static (BackendSettings Backend, AuthSettings Auth) BindLayered(
        string baseJson, string? overrideJson)
    {
        var builder = new ConfigurationBuilder()
            .AddJsonStream(ToStream(baseJson));
        if (overrideJson is not null)
            builder.AddJsonStream(ToStream(overrideJson));
        var config = builder.Build();

        var backend = config.GetSection(BackendSettings.SectionName).Get<BackendSettings>()!;
        var auth = config.GetSection(AuthSettings.SectionName).Get<AuthSettings>()!;
        return (backend, auth);
    }

    private static Stream ToStream(string s) =>
        new MemoryStream(System.Text.Encoding.UTF8.GetBytes(s));
}
