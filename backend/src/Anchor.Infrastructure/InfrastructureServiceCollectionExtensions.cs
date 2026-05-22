using Anchor.Domain.Users;
using Anchor.Infrastructure.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IUserStore, InMemoryUserStore>();
        return services;
    }
}
