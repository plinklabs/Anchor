namespace Anchor.Domain.Users;

public interface IUserStore
{
    Task<User?> FindByEntraOidAsync(Guid entraOid, CancellationToken cancellationToken = default);

    Task<User> UpsertAsync(Guid entraOid, string displayName, UserRole role, CancellationToken cancellationToken = default);
}
