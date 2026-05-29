using System.Net;
using Microsoft.Graph;

namespace Anchor.Api.Users;

internal sealed class GraphUserDirectorySearch : IUserDirectorySearch
{
    // User.Read.All is required (not the narrower User.ReadBasic.All used
    // pre-#96) because companyName and department live outside the basic
    // profile and Graph rejects both $select and $filter on them under
    // ReadBasic. Requires admin consent in Entra; see ARCHITECTURE notes.
    public const string GraphScope = "User.Read.All";
    public const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    // Graph caps page size at 999 for /users; we ask for the max so the
    // company-enumeration scan finishes in as few round-trips as possible.
    private const int MaxPageSize = 999;

    // Safety bound on the company-enumeration loop. A school group with
    // >50k users is well beyond the design target — bail before runaway.
    private const int MaxCompanyScanPages = 50;

    private const string SelectFields = "id,displayName,userPrincipalName,companyName,department";

    private readonly GraphServiceClient _graph;

    public GraphUserDirectorySearch(GraphServiceClient graph)
    {
        _graph = graph;
    }

    public async Task<IReadOnlyList<DirectoryUser>> SearchAsync(
        string query,
        int top,
        string? company,
        CancellationToken cancellationToken)
    {
        // Graph $search wraps values in double quotes, so a stray " in the
        // user's input would break the OData expression. Strip them.
        var safe = query.Replace("\"", string.Empty);
        var search = $"\"displayName:{safe}\" OR \"userPrincipalName:{safe}\"";

        // v4 SDK exposes $search only via raw QueryOption.
        // companyName isn't a default-filterable property — Graph requires the
        // advanced-query pair (ConsistencyLevel: eventual + $count=true) for it
        // to work, even when piggybacking on a $search query.
        var options = new List<QueryOption>
        {
            new QueryOption("$search", search),
            new QueryOption("$count", "true"),
        };
        if (!string.IsNullOrWhiteSpace(company))
        {
            options.Add(new QueryOption("$filter", $"companyName eq '{EscapeOData(company)}'"));
        }

        var page = await _graph.Users.Request(options)
            .Header("ConsistencyLevel", "eventual")
            .Select(SelectFields)
            .Top(top)
            .GetAsync(cancellationToken);

        if (page is null || page.Count == 0)
            return Array.Empty<DirectoryUser>();

        var result = new List<DirectoryUser>(page.Count);
        foreach (var u in page)
        {
            var mapped = MapUser(u);
            if (mapped is not null) result.Add(mapped);
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
                .Select(SelectFields)
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

        return MapUser(user);
    }

    public async Task<IReadOnlyList<DirectoryUser>> ListByClassAsync(
        string company,
        string classCode,
        int top,
        CancellationToken cancellationToken)
    {
        // companyName + department are advanced-query properties. Filtering
        // them requires both the ConsistencyLevel: eventual header AND
        // $count=true in the query string — without $count Graph returns a
        // 400 / "Request_UnsupportedQuery" that we surface as a 502.
        var filter = $"companyName eq '{EscapeOData(company)}' and department eq '{EscapeOData(classCode)}'";
        var options = new List<QueryOption>
        {
            new QueryOption("$filter", filter),
            new QueryOption("$count", "true"),
        };

        var page = await _graph.Users.Request(options)
            .Header("ConsistencyLevel", "eventual")
            .Select(SelectFields)
            .Top(top)
            .GetAsync(cancellationToken);

        if (page is null || page.Count == 0)
            return Array.Empty<DirectoryUser>();

        var result = new List<DirectoryUser>(page.Count);
        foreach (var u in page)
        {
            var mapped = MapUser(u);
            if (mapped is not null) result.Add(mapped);
        }
        return result;
    }

    public async Task<IReadOnlyList<string>> ListCompaniesAsync(CancellationToken cancellationToken)
    {
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var page = await _graph.Users.Request()
            .Select("companyName")
            .Top(MaxPageSize)
            .GetAsync(cancellationToken);

        var pages = 0;
        while (page is not null)
        {
            foreach (var u in page)
            {
                if (!string.IsNullOrWhiteSpace(u.CompanyName))
                    distinct.Add(u.CompanyName);
            }
            pages++;
            if (page.NextPageRequest is null || pages >= MaxCompanyScanPages)
                break;
            page = await page.NextPageRequest.GetAsync(cancellationToken);
        }

        return distinct.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static DirectoryUser? MapUser(User? u)
    {
        if (u is null || string.IsNullOrEmpty(u.Id) || !Guid.TryParse(u.Id, out var oid))
            return null;
        if (string.IsNullOrEmpty(u.DisplayName))
            return null;
        return new DirectoryUser(oid, u.DisplayName, u.UserPrincipalName, u.CompanyName, u.Department);
    }

    // OData single-quoted string literal: double any embedded apostrophe.
    private static string EscapeOData(string value) => value.Replace("'", "''");
}
