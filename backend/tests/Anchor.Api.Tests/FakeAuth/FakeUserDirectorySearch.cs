using Anchor.Api.Users;

namespace Anchor.Api.Tests.FakeAuth;

internal sealed class FakeUserDirectorySearch : IUserDirectorySearch
{
    public Func<string, int, string?, CancellationToken, Task<IReadOnlyList<DirectoryUser>>> Handler { get; set; }
        = (_, _, _, _) => Task.FromResult<IReadOnlyList<DirectoryUser>>(Array.Empty<DirectoryUser>());

    public Func<string, CancellationToken, Task<DirectoryUser?>> ResolveHandler { get; set; }
        = (_, _) => Task.FromResult<DirectoryUser?>(null);

    public Func<string, string, int, CancellationToken, Task<IReadOnlyList<DirectoryUser>>> ListByClassHandler { get; set; }
        = (_, _, _, _) => Task.FromResult<IReadOnlyList<DirectoryUser>>(Array.Empty<DirectoryUser>());

    public Func<CancellationToken, Task<IReadOnlyList<string>>> ListCompaniesHandler { get; set; }
        = _ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public string? LastQuery { get; private set; }
    public int LastTop { get; private set; }
    public string? LastCompany { get; private set; }
    public int CallCount { get; private set; }
    public string? LastResolveUpn { get; private set; }
    public int ResolveCallCount { get; private set; }
    public string? LastListByClassCompany { get; private set; }
    public string? LastListByClassCode { get; private set; }
    public int LastListByClassTop { get; private set; }
    public int ListByClassCallCount { get; private set; }
    public int ListCompaniesCallCount { get; private set; }

    public Task<IReadOnlyList<DirectoryUser>> SearchAsync(
        string query,
        int top,
        string? company,
        CancellationToken cancellationToken)
    {
        LastQuery = query;
        LastTop = top;
        LastCompany = company;
        CallCount++;
        return Handler(query, top, company, cancellationToken);
    }

    public Task<DirectoryUser?> ResolveByUpnAsync(string upn, CancellationToken cancellationToken)
    {
        LastResolveUpn = upn;
        ResolveCallCount++;
        return ResolveHandler(upn, cancellationToken);
    }

    public Task<IReadOnlyList<DirectoryUser>> ListByClassAsync(
        string company,
        string classCode,
        int top,
        CancellationToken cancellationToken)
    {
        LastListByClassCompany = company;
        LastListByClassCode = classCode;
        LastListByClassTop = top;
        ListByClassCallCount++;
        return ListByClassHandler(company, classCode, top, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListCompaniesAsync(CancellationToken cancellationToken)
    {
        ListCompaniesCallCount++;
        return ListCompaniesHandler(cancellationToken);
    }
}
