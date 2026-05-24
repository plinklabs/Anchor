using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anchor.Infrastructure.Users;

internal sealed class EfUserStore : IUserStore
{
    private readonly AnchorDbContext _db;

    public EfUserStore(AnchorDbContext db)
    {
        _db = db;
    }

    public Task<User?> FindByEntraOidAsync(Guid entraOid, CancellationToken cancellationToken = default)
        => _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.EntraOid == entraOid, cancellationToken);

    public async Task<User> UpsertAsync(Guid entraOid, string displayName, UserRole role, CancellationToken cancellationToken = default)
    {
        var existing = await _db.Users.FirstOrDefaultAsync(u => u.EntraOid == entraOid, cancellationToken);
        if (existing is null)
        {
            var created = new User
            {
                EntraOid = entraOid,
                DisplayName = displayName,
                Role = role,
            };
            _db.Users.Add(created);
            await _db.SaveChangesAsync(cancellationToken);
            return created;
        }

        existing.DisplayName = displayName;
        // Admin is a DB-only role layered on top of an underlying Teacher/Student
        // identity (#75). Re-running /me would otherwise downgrade an admin back
        // to whatever role the JWT carries on every sign-in.
        if (existing.Role != UserRole.Admin)
            existing.Role = role;
        await _db.SaveChangesAsync(cancellationToken);
        return existing;
    }
}
