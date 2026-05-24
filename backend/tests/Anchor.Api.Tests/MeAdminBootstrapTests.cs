using System.Net.Http.Json;
using Anchor.Api;
using Anchor.Api.Auth;
using Anchor.Api.Controllers;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Anchor.Api.Tests;

/// <summary>
/// Auto-promote-the-first-admin behaviour (#75). Runs under the Development
/// environment because the bootstrap branch is gated on it — a misconfigured
/// production deployment must not be able to mint admins by accident.
/// </summary>
public sealed class MeAdminBootstrapTests : IClassFixture<MeAdminBootstrapTests.DevAnchorApiFactory>
{
    private readonly DevAnchorApiFactory _factory;

    public MeAdminBootstrapTests(DevAnchorApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task First_real_jwt_caller_with_no_existing_admin_is_promoted()
    {
        await ResetAdminsAsync();
        var oid = Guid.NewGuid();
        using var client = _factory.CreateClient();
        SetFakeBearer(client, oid, "Teacher", "Real Dev " + Suffix());

        var me = await client.GetFromJsonAsync<MeResponse>("/me");

        Assert.NotNull(me);
        Assert.Equal(UserRole.Admin, me!.Role);
    }

    [Fact]
    public async Task Bootstrap_skipped_when_authenticated_via_dev_impersonation()
    {
        // The impersonation header is for testing other users' flows; it must
        // not consume the bootstrap slot, otherwise a smoke script against the
        // seeded Dev Teacher would lock the real developer out of the editor
        // (the failure mode that motivated this guard).
        await ResetAdminsAsync();
        var impersonated = await SeedUserAsync(UserRole.Teacher, "Impersonated " + Suffix());

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(DevImpersonationAuthHandler.HeaderName, impersonated.EntraOid.ToString());

        var me = await client.GetFromJsonAsync<MeResponse>("/me");

        Assert.NotNull(me);
        Assert.Equal(UserRole.Teacher, me!.Role);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Role == UserRole.Admin));
    }

    [Fact]
    public async Task Explicit_promote_me_query_works_even_through_impersonation()
    {
        // Documented escape hatch from #75: when a caller hits
        // /me?promote-me=admin, they get promoted regardless of which auth
        // scheme they came in on. This lets headless dev tooling provision
        // an admin on demand without depending on the bootstrap state.
        await ResetAdminsAsync();
        var impersonated = await SeedUserAsync(UserRole.Teacher, "Explicit " + Suffix());

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(DevImpersonationAuthHandler.HeaderName, impersonated.EntraOid.ToString());

        var me = await client.GetFromJsonAsync<MeResponse>("/me?promote-me=admin");

        Assert.NotNull(me);
        Assert.Equal(UserRole.Admin, me!.Role);
    }

    [Fact]
    public async Task Second_real_jwt_caller_after_admin_exists_stays_teacher()
    {
        await ResetAdminsAsync();

        using (var firstClient = _factory.CreateClient())
        {
            SetFakeBearer(firstClient, Guid.NewGuid(), "Teacher", "First Dev " + Suffix());
            var firstMe = await firstClient.GetFromJsonAsync<MeResponse>("/me");
            Assert.Equal(UserRole.Admin, firstMe!.Role);
        }

        using var secondClient = _factory.CreateClient();
        SetFakeBearer(secondClient, Guid.NewGuid(), "Teacher", "Second Dev " + Suffix());
        var secondMe = await secondClient.GetFromJsonAsync<MeResponse>("/me");

        Assert.NotNull(secondMe);
        Assert.Equal(UserRole.Teacher, secondMe!.Role);
    }

    private async Task ResetAdminsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var admins = await db.Users.Where(u => u.Role == UserRole.Admin).ToListAsync();
        foreach (var admin in admins)
        {
            admin.Role = UserRole.Teacher;
        }
        await db.SaveChangesAsync();
    }

    private async Task<User> SeedUserAsync(UserRole role, string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var user = new User
        {
            EntraOid = Guid.NewGuid(),
            DisplayName = displayName,
            Role = role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static void SetFakeBearer(HttpClient client, Guid oid, string role, string name)
    {
        client.DefaultRequestHeaders.Add(FakeJwtBearerHandler.HeaderOid, oid.ToString());
        client.DefaultRequestHeaders.Add(FakeJwtBearerHandler.HeaderRole, role);
        client.DefaultRequestHeaders.Add(FakeJwtBearerHandler.HeaderName, name);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N").Substring(0, 6);

    /// <summary>
    /// Augments <see cref="AnchorApiFactory"/> to run under Development and
    /// to also accept the test fake bearer scheme in the Development
    /// authorization policy. Real production builds in Development whitelist
    /// only JwtBearer + DevImpersonation; widening that here lets the bootstrap
    /// test exercise the "fresh real sign-in" path that real Entra users hit,
    /// without needing a live token-minting infrastructure.
    /// </summary>
    public sealed class DevAnchorApiFactory : AnchorApiFactory
    {
        protected override string EnvironmentName => "Development";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IPostConfigureOptions<AuthorizationOptions>, AddFakeBearerToDevPolicies>();
            });
        }
    }

    private sealed class AddFakeBearerToDevPolicies : IPostConfigureOptions<AuthorizationOptions>
    {
        public void PostConfigure(string? name, AuthorizationOptions options)
        {
            var schemes = new[]
            {
                JwtBearerDefaults.AuthenticationScheme,
                DevImpersonationAuthHandler.SchemeName,
                FakeJwtBearerHandler.SchemeName,
            };

            var multiScheme = new AuthorizationPolicyBuilder(schemes)
                .RequireAuthenticatedUser()
                .Build();
            options.DefaultPolicy = multiScheme;
            options.FallbackPolicy = multiScheme;

            options.AddPolicy(AuthorizationPolicies.Teacher, p => p
                .AddAuthenticationSchemes(schemes)
                .RequireRole("Teacher"));
            options.AddPolicy(AuthorizationPolicies.Student, p => p
                .AddAuthenticationSchemes(schemes)
                .RequireRole("Student"));
            options.AddPolicy(AuthorizationPolicies.Admin, p =>
            {
                p.AddAuthenticationSchemes(schemes);
                p.RequireAuthenticatedUser();
                p.AddRequirements(new AdminRoleRequirement());
            });
        }
    }
}
