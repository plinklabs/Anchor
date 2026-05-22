using System.Collections.Concurrent;
using Anchor.Domain.Users;

namespace Anchor.Infrastructure.Users;

internal sealed class InMemoryUserStore : IUserStore
{
    private readonly ConcurrentDictionary<Guid, User> _byEntraOid = new();

    public Task<User?> FindByEntraOidAsync(Guid entraOid, CancellationToken cancellationToken = default)
    {
        _byEntraOid.TryGetValue(entraOid, out var user);
        return Task.FromResult(user);
    }

    public Task<User> UpsertAsync(Guid entraOid, string displayName, UserRole role, CancellationToken cancellationToken = default)
    {
        var user = _byEntraOid.AddOrUpdate(
            entraOid,
            _ => new User
            {
                EntraOid = entraOid,
                DisplayName = displayName,
                Role = role,
            },
            (_, existing) =>
            {
                existing.DisplayName = displayName;
                existing.Role = role;
                return existing;
            });

        return Task.FromResult(user);
    }
}
