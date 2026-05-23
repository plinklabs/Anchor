namespace FocusAgent.Core.Settings;

public sealed record BackendSettings
{
    public const string SectionName = "Backend";

    public string BaseUrl { get; init; } = "";
    public string HubPath { get; init; } = "/hubs/session";
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
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan JoinConfirmationDuration { get; init; } = TimeSpan.FromSeconds(5);
}
