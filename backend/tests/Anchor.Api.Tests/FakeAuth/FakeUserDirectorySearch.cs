using Anchor.Api.Users;

namespace Anchor.Api.Tests.FakeAuth;

internal sealed class FakeUserDirectorySearch : IUserDirectorySearch
{
    public Func<string, int, CancellationToken, Task<IReadOnlyList<DirectoryUser>>> Handler { get; set; }
        = (_, _, _) => Task.FromResult<IReadOnlyList<DirectoryUser>>(Array.Empty<DirectoryUser>());

    public string? LastQuery { get; private set; }
    public int LastTop { get; private set; }
    public int CallCount { get; private set; }

    public Task<IReadOnlyList<DirectoryUser>> SearchAsync(
        string query,
        int top,
        CancellationToken cancellationToken)
    {
        LastQuery = query;
        LastTop = top;
        CallCount++;
        return Handler(query, top, cancellationToken);
    }
}
