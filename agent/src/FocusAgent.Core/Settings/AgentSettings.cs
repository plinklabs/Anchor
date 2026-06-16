namespace FocusAgent.Core.Settings;

public sealed record BackendSettings
{
    public const string SectionName = "Backend";

    public string BaseUrl { get; init; } = "";
    public string HubPath { get; init; } = "/hubs/session";

    /// <summary>
    /// Throws a clear, actionable <see cref="InvalidOperationException"/> if
    /// <see cref="BaseUrl"/> is blank or not an absolute http(s) URL. Called at
    /// startup (#247): a release built without its per-deployment config bakes in
    /// an empty BaseUrl, which the SignalR hub builder otherwise turns into a bare
    /// <c>UriFormatException</c> thrown deep in DI — before any window or tray
    /// icon exists — so the process just vanished ("opens and closes instantly").
    /// Validating here converts that into a readable message the startup path can
    /// surface in a visible dialog.
    /// </summary>
    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            throw new InvalidOperationException(
                "Backend:BaseUrl is not configured, so the agent has no server to connect to. " +
                "This usually means the installed build was packaged without its per-deployment " +
                "configuration. Please reinstall from an official release.");
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Backend:BaseUrl ('{BaseUrl}') is not a valid absolute http(s) URL.");
        }
    }
}

public sealed record AuthSettings
{
    public const string SectionName = "Auth";

    public string TenantId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string Scope { get; init; } = "";
    public string LoginHint { get; init; } = "";
}

public sealed record RealtimeSettings
{
    public const string SectionName = "Realtime";

    public TimeSpan ReconnectMaxBackoff { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan JoinConfirmationDuration { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed record UpdateSettings
{
    public const string SectionName = "Update";

    /// <summary>
    /// Master switch for the Velopack auto-update check (#224). On by default so a
    /// shipped agent keeps itself current; a fork or a locked-down deployment can
    /// turn it off in config. The check is additionally a no-op whenever the agent
    /// isn't a Velopack install (a `dotnet run` / self-test build), regardless of
    /// this flag — see <c>AgentUpdateService</c>.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The GitHub repository whose Releases carry the Velopack feed (the
    /// <c>RELEASES</c> / delta+full nupkg the release pipeline uploads, #209).
    /// Defaults to this project's repo; a fork ships its own here so its installed
    /// agents update from the fork's releases, not upstream.
    /// </summary>
    public string GithubRepoUrl { get; init; } = "https://github.com/plinklabs/Anchor";

    /// <summary>
    /// Whether to treat GitHub pre-releases as update candidates. Off by default:
    /// students track the stable line only.
    /// </summary>
    public bool AllowPrerelease { get; init; } = false;

    /// <summary>
    /// How often, after the startup check, the agent re-checks the feed. Default
    /// 6h — frequent enough that a new tag reaches students within a school day,
    /// rare enough to be invisible. Values &lt; 1 minute are clamped up so a
    /// mis-set config can't busy-loop the check.
    /// </summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(6);
}

public sealed record DevSettings
{
    public const string SectionName = "Dev";

    /// <summary>
    /// Optional Entra OID (GUID) to impersonate on the hub connection. When set,
    /// the agent sends <c>X-Dev-Impersonate-Oid</c> on the SignalR negotiate
    /// request, and the backend (Development only) resolves the user from this
    /// value instead of the token's oid claim. Lets one machine play multiple
    /// student identities without multiple Entra accounts.
    /// </summary>
    public string ImpersonateOid { get; init; } = "";
}
