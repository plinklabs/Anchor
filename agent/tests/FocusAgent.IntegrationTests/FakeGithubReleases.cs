using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// A local stand-in for github.com serving one release's assets the way GitHub does
/// (#465), on <c>http://127.0.0.1:&lt;port&gt;</c>:
/// <list type="bullet">
///   <item><c>/&lt;owner&gt;/&lt;repo&gt;/releases/latest/download/&lt;file&gt;</c> redirects to the
///   tagged release's download address,</item>
///   <item>which redirects again to the file itself (GitHub's asset host), served
///   from <paramref name="assetsDirectory"/>;</item>
///   <item>every API path (<c>/api/…</c>, <c>/repos/…</c>) answers 403 "API rate limit
///   exceeded", which is what an anonymous client behind a school's shared address
///   gets once that address has used its 60 requests for the hour.</item>
/// </list>
/// It serves no API, so an agent that still lists the releases through the API
/// (Velopack's <c>GithubSource</c>) finds no release here. Every request path is
/// recorded for the spec to assert on.
/// </summary>
internal sealed class FakeGithubReleases : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly string _base;
    private readonly string _latestPrefix;
    private readonly string _taggedPrefix;
    private readonly string _assetsDirectory;
    private readonly Task _loop;

    public FakeGithubReleases(string assetsDirectory, string tag, string owner = "plinklabs", string repo = "Anchor")
    {
        _assetsDirectory = assetsDirectory;
        _base = $"http://127.0.0.1:{TestConfig.FreeLoopbackPort()}";
        _latestPrefix = $"/{owner}/{repo}/releases/latest/download/";
        _taggedPrefix = $"/{owner}/{repo}/releases/download/{tag}/";
        RepoUrl = $"{_base}/{owner}/{repo}";

        _listener.Prefixes.Add(_base + "/");
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    /// <summary>The repository URL to give the agent, like <c>https://github.com/owner/repo</c>.</summary>
    public string RepoUrl { get; }

    /// <summary>The path (without query) of every request served so far, in order.</summary>
    public IReadOnlyList<string> RequestPaths => _requests.ToArray();

    public static bool IsApiPath(string path) =>
        path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/repos/", StringComparison.OrdinalIgnoreCase);

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                return; // listener stopped
            }

            try
            {
                Handle(context);
            }
            catch
            {
                try { context.Response.Abort(); } catch { /* best-effort */ }
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath;
        _requests.Enqueue(path);
        var response = context.Response;

        if (IsApiPath(path))
        {
            response.StatusCode = 403;
            response.Headers["x-ratelimit-limit"] = "60";
            response.Headers["x-ratelimit-remaining"] = "0";
            WriteBody(response, "application/json",
                Encoding.UTF8.GetBytes("{\"message\":\"API rate limit exceeded for 127.0.0.1.\"}"));
        }
        else if (path.StartsWith(_latestPrefix, StringComparison.Ordinal))
        {
            response.Redirect(_base + _taggedPrefix + path[_latestPrefix.Length..]);
        }
        else if (path.StartsWith(_taggedPrefix, StringComparison.Ordinal))
        {
            // GitHub's second hop: a signed URL on its asset host.
            response.Redirect($"{_base}/release-assets/{Guid.NewGuid():N}/{path[_taggedPrefix.Length..]}?sig=fake");
        }
        else if (path.StartsWith("/release-assets/", StringComparison.Ordinal) &&
                 AssetPath(Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..])) is { } file)
        {
            WriteBody(response, "application/octet-stream", File.ReadAllBytes(file));
        }
        else
        {
            response.StatusCode = 404;
        }

        response.Close();
    }

    // Only a file that is really in the release, by its bare name.
    private string? AssetPath(string name)
    {
        if (name.Length == 0 || name != Path.GetFileName(name)) return null;
        var file = Path.Combine(_assetsDirectory, name);
        return File.Exists(file) ? file : null;
    }

    private static void WriteBody(HttpListenerResponse response, string contentType, byte[] body)
    {
        response.ContentType = contentType;
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { /* best-effort */ }
        try { _listener.Close(); } catch { /* best-effort */ }
        try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch { /* best-effort */ }
        _stop.Dispose();
    }
}
