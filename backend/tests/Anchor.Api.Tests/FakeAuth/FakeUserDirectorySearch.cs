using Anchor.Api.Users;

namespace Anchor.Api.Tests.FakeAuth;

internal sealed class FakeUserDirectorySearch : IUserDirectorySearch
{
    public Func<string, int, CancellationToken, Task<IReadOnlyList<DirectoryUser>>> Handler { get; set; }
        = (_, _, _) => Task.FromResult<IReadOnlyList<DirectoryUser>>(Array.Empty<DirectoryUser>());

    public Func<string, CancellationToken, Task<DirectoryUser?>> ResolveHandler { get; set; }
        = (_, _) => Task.FromResult<DirectoryUser?>(null);

    public string? LastQuery { get; private set; }
    public int LastTop { get; private set; }
    public int CallCount { get; private set; }
    public string? LastResolveUpn { get; private set; }
    public int ResolveCallCount { get; private set; }

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

    public Task<DirectoryUser?> ResolveByUpnAsync(string upn, CancellationToken cancellationToken)
    {
        LastResolveUpn = upn;
        ResolveCallCount++;
        return ResolveHandler(upn, cancellationToken);
    }
}
