namespace Anchor.Domain.Users;

public sealed class User
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid EntraOid { get; init; }
    public required string DisplayName { get; set; }
    public required UserRole Role { get; set; }
}
