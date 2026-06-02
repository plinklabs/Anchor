using Anchor.Api;
using Anchor.Api.Auth;
using Anchor.Api.Events;
using Anchor.Api.Realtime;
using Anchor.Api.Sessions;
using Anchor.Infrastructure;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Anchor.Api.Users;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"))
    // OBO token acquisition for downstream Graph calls (directory search).
    // The dashboard requests `<clientId>/.default`, so the user token includes
    // every preconsented delegated scope on the app registration; we just
    // need to request the Graph scope here for the OBO exchange. Production
    // also requires AzureAd:ClientCredentials (client secret/cert) — without
    // it the OBO exchange fails at first call, not at startup.
    .EnableTokenAcquisitionToCallDownstreamApi()
    .AddMicrosoftGraph(GraphUserDirectorySearch.GraphBaseUrl, GraphUserDirectorySearch.GraphScope)
    .AddInMemoryTokenCaches();

builder.Services.AddScoped<IUserDirectorySearch, GraphUserDirectorySearch>();

builder.Services.Configure<JwtBearerOptions>(
    JwtBearerDefaults.AuthenticationScheme,
    options => JwtBearerSetup.Configure(options, builder.Configuration));

// In Development, accept the X-Dev-Impersonate-Oid header as a secondary
// authentication option so headless callers (verify scripts, the agent in
// --inject-token mode) can hit the API without a real Entra token. The
// scheme is registered only here so a misconfigured production deployment
// physically cannot route requests through it.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAuthentication()
        .AddScheme<AuthenticationSchemeOptions, DevImpersonationAuthHandler>(
            DevImpersonationAuthHandler.SchemeName, _ => { });
}

builder.Services.AddAuthorization(options =>
{
    // In Development, accept either JwtBearer or the DevImpersonation header
    // scheme. Explicit scheme lists are scoped to Development so Test and
    // Production keep the original DefaultAuthenticateScheme fallback (Test
    // overrides the default with FakeJwtBearerHandler; production uses
    // JwtBearer). Without this guard the test factory's auth override is
    // silently bypassed by the explicit list and every existing test 401s.
    if (builder.Environment.IsDevelopment())
    {
        var schemes = new[]
        {
            JwtBearerDefaults.AuthenticationScheme,
            DevImpersonationAuthHandler.SchemeName,
        };
        // [Authorize] without args (used by SessionHub) consults DefaultPolicy.
        // [Authorize(Policy=...)] uses the named policy. Unmarked endpoints
        // consult FallbackPolicy. All three need the multi-scheme builder so
        // the dev impersonation header is accepted everywhere.
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
    else
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
        options.AddPolicy(AuthorizationPolicies.Teacher, p => p.RequireRole("Teacher"));
        options.AddPolicy(AuthorizationPolicies.Student, p => p.RequireRole("Student"));
        options.AddPolicy(AuthorizationPolicies.Admin, p =>
        {
            p.RequireAuthenticatedUser();
            p.AddRequirements(new AdminRoleRequirement());
        });
    }
});

builder.Services.AddScoped<IAuthorizationHandler, AdminRoleAuthorizationHandler>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddInfrastructureServices();
if (!builder.Environment.IsEnvironment("Test"))
{
    // Dev uses a local SQLite file so the backend runs anywhere (laptop on a
    // train, no network). Production points at Azure SQL via SqlServer.
    if (builder.Environment.IsDevelopment())
    {
        builder.Services.AddInfrastructureSqlite(builder.Configuration);
    }
    else
    {
        builder.Services.AddInfrastructureSqlServer(builder.Configuration);
    }
}

builder.Services.AddSignalR();
builder.Services.AddSingleton<JoinByCodeRateLimiter>();
builder.Services.AddScoped<ISessionAllowlistExpander, SessionAllowlistExpander>();
builder.Services.AddSingleton<ISessionBroadcaster, SessionBroadcaster>();
builder.Services.AddSingleton<HeartbeatTracker>();
builder.Services.AddSingleton<ParticipantLiveStateResolver>();
builder.Services.Configure<HeartbeatOptions>(builder.Configuration.GetSection(HeartbeatOptions.SectionName));
var heartbeatSection = builder.Configuration.GetSection(HeartbeatOptions.SectionName);
var enableHeartbeatMonitor = heartbeatSection.GetValue<bool?>(nameof(HeartbeatOptions.EnableMonitor)) ?? true;
if (enableHeartbeatMonitor)
{
    builder.Services.AddHostedService<HeartbeatMonitor>();
}

builder.Services.Configure<EventRetentionOptions>(builder.Configuration.GetSection(EventRetentionOptions.SectionName));
var retentionSection = builder.Configuration.GetSection(EventRetentionOptions.SectionName);
var enableEventPruner = retentionSection.GetValue<bool?>(nameof(EventRetentionOptions.EnablePruner)) ?? true;
if (enableEventPruner)
{
    builder.Services.AddHostedService<EventPruner>();
}

const string DashboardCorsPolicy = "DashboardCors";
var dashboardOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(options =>
{
    options.AddPolicy(DashboardCorsPolicy, policy => policy
        .WithOrigins(dashboardOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
    // SQLite dev DB doesn't share migrations with the SqlServer prod schema,
    // so build the schema from the current model instead of running migrations.
    await db.Database.EnsureCreatedAsync();
    await DevDataSeeder.SeedAsync(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors(DashboardCorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<SessionHub>(SessionHub.Path);

await app.RunAsync();

public partial class Program
{
    protected Program() { }
}
