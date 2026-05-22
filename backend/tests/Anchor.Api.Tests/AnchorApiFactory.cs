using Anchor.Api.Tests.FakeAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

public sealed class AnchorApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(FakeJwtBearerHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, FakeJwtBearerHandler>(
                    FakeJwtBearerHandler.SchemeName, _ => { });

            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = FakeJwtBearerHandler.SchemeName;
                options.DefaultChallengeScheme = FakeJwtBearerHandler.SchemeName;
            });
        });
    }
}
