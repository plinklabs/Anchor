using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusAgent.WitnessHost;

/// <summary>
/// Resolves the backend URL the witness host hands down to the extension over
/// the native-messaging link (#204).
///
/// The extension is backend-agnostic: a single published listing serves every
/// fork, so it learns which backend to target from the on-box agent at runtime
/// rather than baking it in. The witness host is the agent's native-messaging
/// endpoint to the browser, so it's the natural carrier — and only the
/// registered host can reach the extension's witness port, so an arbitrary web
/// page can't repoint it.
///
/// Source of truth, in order (mirrors how the agent itself layers deployment
/// config in #203 — an env override on top of a committed file, with a dev
/// fallback):
///   1. <c>ANCHOR_WITNESS_BACKEND_URL</c> env var — what the agent installer
///      sets per deployment, and what the extension e2e harness sets to point
///      the host at its throwaway backend.
///   2. a <c>backend-url.json</c> file next to the host exe — an alternative the
///      installer can drop ({"backendUrl":"https://…"}).
///   3. the dev fallback (the local backend port) so a plain dev loop works
///      before any deployment config exists.
/// </summary>
public static class BackendUrlConfig
{
    /// <summary>Env var the agent installer / e2e harness sets per deployment.</summary>
    public const string EnvVarName = "ANCHOR_WITNESS_BACKEND_URL";

    /// <summary>Optional config file the installer can drop next to the exe.</summary>
    public const string FileName = "backend-url.json";

    /// <summary>
    /// Local dev backend port. NOT a production default — it only keeps a plain
    /// `dotnet run` dev loop working before any per-deployment config exists.
    /// Matches the extension's DEV_FALLBACK_BACKEND_URL and the agent's
    /// committed appsettings.json Backend:BaseUrl.
    /// </summary>
    public const string DevFallbackUrl = "http://localhost:5276";

    /// <summary>
    /// The native message the host sends the extension. <c>witness.ts</c>
    /// classifies <c>type: "backend_url"</c> and stores the <c>url</c>.
    /// </summary>
    public static string BuildMessage(string url) =>
        JsonSerializer.Serialize(new BackendUrlMessage("backend_url", url));

    [Serializable]
    private sealed record BackendUrlMessage(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("url")] string Url);

    // Shape of the optional config file the installer can drop next to the exe:
    // {"backendUrl":"https://…"}.
    private sealed record BackendUrlFile(
        [property: JsonPropertyName("backendUrl")] string? BackendUrl);

    /// <summary>
    /// Resolve the backend URL at runtime from the env var, then the optional
    /// file next to the exe, then the dev fallback. Pure-ish: the IO sources are
    /// injected so the precedence logic is unit-tested without touching the
    /// real environment or filesystem.
    /// </summary>
    public static string Resolve(
        Func<string, string?> getEnv,
        Func<string?> readConfigFile)
    {
        var fromEnv = Normalize(getEnv(EnvVarName));
        if (fromEnv is not null) return fromEnv;

        var fromFile = Normalize(ReadFromJson(readConfigFile()));
        if (fromFile is not null) return fromFile;

        return DevFallbackUrl;
    }

    /// <summary>Runtime resolution against the real environment + exe directory.</summary>
    public static string ResolveFromEnvironment() =>
        Resolve(
            Environment.GetEnvironmentVariable,
            () =>
            {
                try
                {
                    var path = Path.Combine(AppContext.BaseDirectory, FileName);
                    return File.Exists(path) ? File.ReadAllText(path) : null;
                }
                catch
                {
                    // A malformed/unreadable config file must not crash the host;
                    // fall through to the dev fallback.
                    return null;
                }
            });

    private static string? ReadFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<BackendUrlFile>(json)?.BackendUrl;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.EndsWith('/') ? trimmed.TrimEnd('/') : trimmed;
    }
}
