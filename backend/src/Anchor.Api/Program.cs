using Anchor.Api;
using Anchor.Api.Realtime;
using Anchor.Infrastructure;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.Configure<JwtBearerOptions>(
    JwtBearerDefaults.AuthenticationScheme,
    options => JwtBearerSetup.Configure(options, builder.Configuration));

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    options.AddPolicy(AuthorizationPolicies.Teacher, p => p.RequireRole("Teacher"));
    options.AddPolicy(AuthorizationPolicies.Student, p => p.RequireRole("Student"));
});

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
builder.Services.AddSingleton<ISessionBroadcaster, SessionBroadcaster>();

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
