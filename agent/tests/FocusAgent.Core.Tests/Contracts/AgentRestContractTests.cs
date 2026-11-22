using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FocusAgent.Core.Auth;
using FocusAgent.Core.Diagnostics;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Tests.Contracts;

/// <summary>
/// The agent's side of contracts/agent-rest.json (#573): each REST client the
/// agent calls the backend with, against a fake backend that answers with a
/// body built from the file. What it sends has to be the route, query and body
/// the file gives, and it has to read every response field the file lists.
/// Backend CI holds the backend to the same file (RestContractTests), so a
/// change on either side fails until both agree.
/// </summary>
public sealed class AgentRestContractTests
{
    private const string BaseUrl = "https://backend.test/api";

    private static readonly JsonObject Routes = ContractFiles.Load("agent-rest.json")["routes"]!.AsObject();

    [Fact]
    public void Every_route_in_the_contract_is_one_these_tests_call()
    {
        Assert.Equal(
            new[] { "GET log-uploads/pending", "GET sessions/rejoinable", "POST log-uploads/{sessionId}", "POST sessions/join-by-code" },
            Routes.Select(r => r.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Rejoining_reads_every_field_of_the_sessions_the_backend_lists()
    {
        var route = Route("GET sessions/rejoinable");
        var sample = ContractFiles.Sample(route["response"]!);
        var backend = new FakeBackend(sample);

        var sessions = await new HttpSessionRehydrationClient(
            backend.Client(), new NoTokens(), Backend(), Options.Create(new DevSettings()),
            NullLogger<HttpSessionRehydrationClient>.Instance).GetRejoinableSessionsAsync();

        var request = Assert.Single(backend.Requests);
        AssertSent(request, "GET sessions/rejoinable", route);
        AssertReads(sample, ContractFiles.AsRead(sessions), "GET sessions/rejoinable response");
    }

    [Fact]
    public async Task Asking_for_log_windows_reads_every_field_of_the_answer()
    {
        var route = Route("GET log-uploads/pending");
        var sample = ContractFiles.Sample(route["response"]!);
        var backend = new FakeBackend(sample);

        var pending = await LogUploads(backend).GetPendingAsync();

        var request = Assert.Single(backend.Requests);
        AssertSent(request, "GET log-uploads/pending", route);
        AssertReads(sample, ContractFiles.AsRead(pending), "GET log-uploads/pending response");
    }

    [Fact]
    public async Task A_log_upload_goes_to_the_sessions_route_with_the_query_the_contract_gives()
    {
        var route = Route("POST log-uploads/{sessionId}");
        var backend = new FakeBackend(answer: null, HttpStatusCode.NoContent);
        var window = new LogWindow(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow);

        var outcome = await LogUploads(backend).UploadAsync(window, "1.2.3", [0x1f, 0x8b, 0x08]);

        Assert.Equal(LogUploadOutcome.Stored, outcome);
        var request = Assert.Single(backend.Requests);
        AssertSent(request, "POST log-uploads/{sessionId}", route, window.SessionId);
        // Not JSON: the gzipped log text, which the backend takes as it comes.
        Assert.Equal("application/gzip", request.ContentType);
    }

    [Fact]
    public async Task Joining_by_code_sends_the_body_and_reads_the_refusal_the_contract_gives()
    {
        var route = Route("POST " + JoinByCodeWire.Route);

        using var body = JoinByCodeWire.Body("123456");
        var sent = JsonNode.Parse(await body.ReadAsStringAsync());
        var problems = ContractFiles.Mismatches(route["request"]!, sent, "POST sessions/join-by-code request");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.Equal("123456", sent!["code"]?.GetValue<string>());

        var refusal = ContractFiles.Sample(route["errorResponse"]!, name => name == "message" ? "Code not found." : null);
        using var response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(refusal.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        Assert.Equal("Code not found.", await JoinByCodeWire.ReadErrorMessageAsync(response));
    }

    private static JsonObject Route(string key) =>
        Routes[key]?.AsObject() ?? throw new KeyNotFoundException($"contracts/agent-rest.json has no {key}.");

    private static IOptions<BackendSettings> Backend() => Options.Create(new BackendSettings { BaseUrl = BaseUrl });

    private static HttpLogUploadClient LogUploads(FakeBackend backend) =>
        new(backend.Client(), new NoTokens(), Backend(), Options.Create(new DevSettings()),
            NullLogger<HttpLogUploadClient>.Instance);

    /// <summary>
    /// The request went to the route the key names (its placeholder filled with
    /// <paramref name="id"/>), with exactly the query parameters the file lists,
    /// and a JSON body only where the file gives one.
    /// </summary>
    private static void AssertSent(SentRequest request, string key, JsonObject route, Guid? id = null)
    {
        var (method, template) = (key[..key.IndexOf(' ')], key[(key.IndexOf(' ') + 1)..]);
        Assert.Equal(method, request.Method);
        Assert.Equal(template.Replace("{sessionId}", id?.ToString() ?? "{sessionId}"), request.Path);
        var query = (route["query"]?.AsArray().Select(q => q!.GetValue<string>()) ?? []).Order(StringComparer.Ordinal);
        Assert.Equal(query, request.Query.Order(StringComparer.Ordinal));
        if (route["request"] is null)
            Assert.NotEqual("application/json", request.ContentType);
    }

    private static void AssertReads(JsonNode sample, JsonNode? read, string path)
    {
        var problems = ContractFiles.ReadDifferences(sample, read, path);
        Assert.True(problems.Count == 0, $"""
            The agent no longer reads what contracts/agent-rest.json lists (#573):
              {string.Join(Environment.NewLine + "  ", problems)}
            """);
    }

    private sealed record SentRequest(string Method, string Path, IReadOnlyList<string> Query, string? ContentType);

    /// <summary>Records each request and answers with one body.</summary>
    private sealed class FakeBackend(JsonNode? answer, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = new();

        public HttpClient Client() => new(this);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var basePath = new Uri(BaseUrl + "/").AbsolutePath;
            var query = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => Uri.UnescapeDataString(p.Split('=')[0]))
                .ToList();
            Requests.Add(new SentRequest(
                request.Method.Method,
                uri.AbsolutePath[basePath.Length..],
                query,
                request.Content?.Headers.ContentType?.MediaType));
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = answer is null ? null : new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class NoTokens : IAuthTokenProvider
    {
        public Task<AuthResult> AcquireTokenAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("The fake backend takes no token.");

        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("");
    }
}
