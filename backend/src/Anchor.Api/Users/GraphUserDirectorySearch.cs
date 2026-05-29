using System.Net;
using Microsoft.Graph;

namespace Anchor.Api.Users;

internal sealed class GraphUserDirectorySearch : IUserDirectorySearch
{
    public const string GraphScope = "User.ReadBasic.All";
    public const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    private readonly GraphServiceClient _graph;

    public GraphUserDirectorySearch(GraphServiceClient graph)
    {
        _graph = graph;
    }

    public async Task<IReadOnlyList<DirectoryUser>> SearchAsync(
        string query,
        int top,
        CancellationToken cancellationToken)
    {
        // Graph $search wraps values in double quotes, so a stray " in the
        // user's input would break the OData expression. Strip them.
        var safe = query.Replace("\"", string.Empty);
        var search = $"\"displayName:{safe}\" OR \"userPrincipalName:{safe}\"";

        // v4 SDK exposes $search only via raw QueryOption.
        var options = new List<QueryOption> { new QueryOption("$search", search) };

        var page = await _graph.Users.Request(options)
            .Header("ConsistencyLevel", "eventual")
            .Select("id,displayName,userPrincipalName")
            .Top(top)
            .GetAsync(cancellationToken);

        if (page is null || page.Count == 0)
            return Array.Empty<DirectoryUser>();

        var result = new List<DirectoryUser>(page.Count);
        foreach (var u in page)
        {
            if (string.IsNullOrEmpty(u.Id) || !Guid.TryParse(u.Id, out var oid))
                continue;
            if (string.IsNullOrEmpty(u.DisplayName))
                continue;
            result.Add(new DirectoryUser(oid, u.DisplayName, u.UserPrincipalName));
        }
        return result;
    }

    public async Task<DirectoryUser?> ResolveByUpnAsync(
        string upn,
        CancellationToken cancellationToken)
    {
        User user;
        try
        {
            // GET /users/{upn} is a direct lookup — the UPN is a valid key.
            user = await _graph.Users[upn].Request()
                .Select("id,displayName,userPrincipalName")
                .GetAsync(cancellationToken);
        }
        catch (ServiceException ex) when (
            ex.StatusCode == HttpStatusCode.NotFound ||
            ex.StatusCode == HttpStatusCode.BadRequest)
        {
            // Unknown or malformed UPN — a per-row failure, not a systemic one.
            // Anything else (auth/consent, throttling, outage) propagates so the
            // caller can surface a 502 for the whole import.
            return null;
        }

        if (user is null || string.IsNullOrEmpty(user.Id) || !Guid.TryParse(user.Id, out var oid))
            return null;
        if (string.IsNullOrEmpty(user.DisplayName))
            return null;
        return new DirectoryUser(oid, user.DisplayName, user.UserPrincipalName);
    }
}
