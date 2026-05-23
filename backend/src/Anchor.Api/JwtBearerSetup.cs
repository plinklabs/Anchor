using Anchor.Api.Realtime;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Anchor.Api;

internal static class JwtBearerSetup
{
    public static void Configure(JwtBearerOptions options, IConfiguration configuration)
    {
        // Keep JWT claim names as-is ('roles', 'oid', etc.); without this the
        // JWT handler renames short claims to long SAML-style URIs and our
        // RoleClaimType/NameClaimType below stop matching.
        options.MapInboundClaims = false;
        options.TokenValidationParameters.RoleClaimType = "roles";
        options.TokenValidationParameters.NameClaimType = "name";

        // Accept both the GUID and api:// audience forms so the SPA (which must
        // use the GUID-form scope when sharing the app registration) and any
        // other clients using the api:// form both validate.
        var clientId = configuration["AzureAd:ClientId"];
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            options.TokenValidationParameters.ValidAudiences = new[]
            {
                clientId,
                $"api://{clientId}",
            };
        }

        options.Events ??= new JwtBearerEvents();
        var existing = options.Events.OnMessageReceived;
        options.Events.OnMessageReceived = context => OnMessageReceivedAsync(context, existing);
    }

    private static async Task OnMessageReceivedAsync(
        MessageReceivedContext context,
        Func<MessageReceivedContext, Task>? existing)
    {
        if (existing is not null)
            await existing(context);

        if (!string.IsNullOrEmpty(context.Token)) return;

        var accessToken = context.Request.Query["access_token"];
        var path = context.HttpContext.Request.Path;
        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments(SessionHub.Path))
            context.Token = accessToken;
    }
}
