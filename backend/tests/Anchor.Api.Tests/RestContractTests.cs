using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Net;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Anchor.Api.Controllers;
using Anchor.Api.Diagnostics;
using Anchor.Api.Sessions;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Api.Users;
using Anchor.Domain.Bundles;
using Anchor.Domain.Classes;
using Anchor.Domain.Sessions;
using Anchor.Infrastructure.Persistence;
using Anchor.Domain.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Anchor.Api.Tests;

/// <summary>
/// The backend's side of the REST contracts: contracts/rest.json, the routes
/// the dashboard calls (#393), and contracts/agent-rest.json, those the
/// student's agent calls (#573). Dashboard E2E runs against fake APIs, so it
/// can't see a backend change to a route, a field or an enum value the
/// dashboard depends on, and the agents in the field lag behind the backend.
/// These tests describe the routes as the backend serves them and fail when
/// that differs from the files. A change to a file runs its client's contract
/// tests, which check the client against it.
/// <para>
/// Routes, their query parameters and request bodies come from the app's own
/// API description. A response is described from what the action actually
/// returned when a scenario called the route in the test host, through the
/// app's JSON settings, and checked against the body that went over the wire.
/// So a route that returns something other than it declares shows up, and a
/// nested list the scenario left empty is still described.
/// </para>
/// </summary>
public sealed class RestContractTests : IClassFixture<RestContractTests.ContractFactory>
{
    private readonly ContractFactory _factory;
    private readonly RestContract _contract = RestContract.Load("contracts/rest.json");
    private readonly RestContract _agentContract = RestContract.Load("contracts/agent-rest.json");

    public RestContractTests(ContractFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Every_route_is_in_the_contract_or_listed_as_not_covered()
    {
        // A new route has to be described for the dashboard or the agent, or
        // left out on purpose.
        Assert.Equal(
            Routes().Keys.Order(StringComparer.Ordinal),
            _contract.Routes.Select(r => r.Key)
                .Concat(_agentContract.Routes.Select(r => r.Key))
                .Concat(_contract.NotCovered)
                .Distinct()
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_routes_the_dashboard_calls_take_and_return_what_the_contract_says()
    {
        var calls = await CallEveryRouteTheDashboardReadsAsync();
        var routes = Routes();
        var describer = new ContractShapeDescriber(WireOptions);
        var served = new JsonObject();
        var wire = new List<string>();
        foreach (var (key, node) in _contract.Routes)
        {
            served[key] = routes.TryGetValue(key, out var api)
                ? DescribeRoute(key, api, node!.AsObject(), calls, describer, wire)
                : null;
        }

        var enums = new JsonObject(served
            .SelectMany(r => ContractJson.Types(r.Value?["request"]).Concat(ContractJson.Types(r.Value?["response"])))
            .Select(t => t.TrimEnd('?'))
            .Where(describer.Enums.ContainsKey)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .Select(name => KeyValuePair.Create(name, (JsonNode?)describer.Enums[name].DeepClone())));

        var differences = ContractJson.Differences(_contract.Routes, served)
            .Concat(ContractJson.Differences(_contract.Enums, enums))
            .ToList();
        if (differences.Count == 0 && wire.Count == 0)
            return;

        Assert.Fail($"""
            contracts/rest.json no longer describes the backend (#393):
            {string.Join(Environment.NewLine, differences.Concat(wire))}

            If the backend change is intended, put what it serves in the file. That runs the dashboard's
            contract tests (Dashboard CI), which check that the dashboard still sends and parses it. A null
            route means the app has no such route; a null response means the scenario here never called it.
            The routes and enums as the backend serves them, each response cut down to the fields the file
            lists:
            {ContractJson.Indented(new JsonObject { ["routes"] = served.DeepClone(), ["enums"] = enums.DeepClone() })}

            Every field of the responses the scenario got, for a field the dashboard starts to read:
            {ContractJson.Indented(describer.Responses)}
            """);
    }

    [Fact]
    public async Task The_routes_the_agent_calls_take_and_return_what_the_contract_says()
    {
        var calls = await CallEveryRouteTheAgentReadsAsync();
        var routes = Routes();
        var describer = new ContractShapeDescriber(WireOptions);
        var served = new JsonObject();
        var wire = new List<string>(calls.Problems);
        foreach (var (key, node) in _agentContract.Routes)
        {
            served[key] = routes.TryGetValue(key, out var api)
                ? DescribeRoute(key, api, node!.AsObject(), calls.Responses, describer, wire)
                : null;
        }

        var differences = ContractJson.Differences(_agentContract.Routes, served).ToList();
        if (differences.Count == 0 && wire.Count == 0)
            return;

        Assert.Fail($"""
            contracts/agent-rest.json no longer describes the backend (#573):
            {string.Join(Environment.NewLine, differences.Concat(wire))}

            If the backend change is intended, put what it serves in the file. That runs the agent's
            contract tests (Agent E2E), which check that the agent still sends and parses it, and the agents
            already in the field have to survive it too. A null route means the app has no such route; a
            null response means the scenario here never called it. The routes as the backend serves them,
            each response cut down to the fields the file lists:
            {ContractJson.Indented(served)}

            Every field of the responses the scenario got, for a field the agent starts to read:
            {ContractJson.Indented(describer.Responses)}
            """);
    }

    // ------- The description -------

    /// <summary>
    /// A route as the backend serves it: the body it binds, the query
    /// parameters the file says the client sends that it binds, and, when the
    /// file says the client reads the response or the error bodies, the fields
    /// of them the file lists.
    /// </summary>
    private static JsonObject DescribeRoute(
        string key,
        ApiDescription api,
        JsonObject inFile,
        IReadOnlyDictionary<string, List<Response>> calls,
        ContractShapeDescriber describer,
        List<string> wire)
    {
        var route = new JsonObject();
        if (api.ParameterDescriptions.SingleOrDefault(p => p.Source == BindingSource.Body) is { } body)
            route["request"] = describer.Describe(body.Type);

        if (inFile["query"] is JsonArray asked)
        {
            var bound = api.ParameterDescriptions
                .Where(p => p.Source == BindingSource.Query)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);
            route["query"] = ContractJson.Strings(asked.Select(q => q!.GetValue<string>()).Where(bound.Contains));
        }

        if (inFile["response"] is { } read)
        {
            var responses = calls.GetValueOrDefault(key) ?? [];
            var described = responses
                .Select(r => describer.Describe(r.Type))
                .DistinctBy(ContractJson.Canonical)
                .ToList();
            foreach (var response in responses)
                describer.Check(describer.Describe(response.Type), response.Body, $"{key} response", wire);
            if (described.Count > 0)
                describer.Responses[key] = described.Count == 1 ? described[0].DeepClone() : new JsonArray(described.Select(d => d.DeepClone()).ToArray());
            route["response"] = described.Count switch
            {
                0 => null,
                1 => ContractJson.Project(described[0], read),
                _ => new JsonArray(described.Select(d => ContractJson.Project(d, read)).ToArray()),
            };
        }

        if (inFile["errorResponse"] is { } errorRead)
        {
            // The body of every error status the route declares with a type of
            // its own; a bare status gets ASP.NET's ProblemDetails, which the
            // client can't count on reading anything from.
            var described = api.SupportedResponseTypes
                .Where(r => r.StatusCode >= 400 && r.Type is { } type && type != typeof(void) &&
                            !typeof(ProblemDetails).IsAssignableFrom(type))
                .Select(r => describer.Describe(r.Type!))
                .DistinctBy(ContractJson.Canonical)
                .ToList();
            route["errorResponse"] = described.Count switch
            {
                0 => null,
                1 => ContractJson.Project(described[0], errorRead),
                _ => new JsonArray(described.Select(d => ContractJson.Project(d, errorRead)).ToArray()),
            };
        }

        return route;
    }

    /// <summary>The app's JSON settings for controllers, which name every field on the wire.</summary>
    private JsonSerializerOptions WireOptions => _wireOptions ??= WireOptionsOf(_factory.Services);

    private JsonSerializerOptions? _wireOptions;

    private static JsonSerializerOptions WireOptionsOf(IServiceProvider services)
    {
        var options = new JsonSerializerOptions(
            services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions);
        options.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver();
        return options;
    }

    /// <summary>The app's routes, as its API description has them, by "METHOD path".</summary>
    private Dictionary<string, ApiDescription> Routes() =>
        _factory.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(g => g.Items)
            .ToDictionary(Key, StringComparer.Ordinal);

    private static string Key(ApiDescription api) => $"{api.HttpMethod} {api.RelativePath}";

    // ------- The scenario -------

    /// <summary>What an action returned, and the body that went over the wire.</summary>
    private sealed record Response(Type Type, JsonNode? Body);

    /// <summary>
    /// Calls each route whose response the dashboard reads, once, the way the
    /// dashboard would: a teacher with a class, students, a bundle and a past
    /// session; an admin for the admin routes; the directory faked.
    /// </summary>
    private async Task<Dictionary<string, List<Response>>> CallEveryRouteTheDashboardReadsAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var school = $"School {suffix}";
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(
            _factory, studentCount: 2, schoolTag: school, classCode: "3A");
        var classId = scenario.Class.Id;
        var admin = await TestSeed.AddUserAsync(_factory, UserRole.Admin, $"Admin {suffix}");
        await TestSeed.AddUserAsync(_factory, UserRole.Teacher, $"Candidate {suffix}");
        var bundle = await TestSeed.AddBundleAsync(_factory, $"Bundle {suffix}");
        await TestSeed.AddBundleEntryAsync(_factory, bundle.Id, BundleEntryKind.Domain, "example.com", BundleEntryMatchType.Suffix);
        var pastSession = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, classId, scenario.Students.Select(s => s.Id).ToList(), ended: true);
        await AddLogUploadAsync(pastSession, scenario.Students[0].Id);

        DirectoryUser InDirectory(string name) =>
            new(Guid.NewGuid(), name, $"{name.ToLowerInvariant()}.{suffix}@school.be", school, "3A");
        var directory = _factory.Services.GetRequiredService<FakeUserDirectorySearch>();
        directory.Handler = (_, _, _, _) => Task.FromResult<IReadOnlyList<DirectoryUser>>([InDirectory("Found")]);
        directory.ResolveHandler = (upn, _) => Task.FromResult<DirectoryUser?>(InDirectory("Imported") with { Upn = upn });
        directory.ListByClassHandler = (_, _, _, _) => Task.FromResult<IReadOnlyList<DirectoryUser>>([InDirectory("Enrolled")]);
        directory.ListCompaniesHandler = _ => Task.FromResult<IReadOnlyList<string>>([school]);

        var calls = new Dictionary<string, List<Response>>(StringComparer.Ordinal);
        using var teacher = Client(scenario.Teacher, "Teacher");
        using var adminClient = Client(admin, "Admin");

        Task<JsonNode?> Call(HttpClient client, string route, object[] path, object? body = null, string? query = null) =>
            CallAsync(calls, client, route, path, body, query);

        static string Id(JsonNode? json) => json!["id"]!.GetValue<string>();

        await Call(teacher, "GET me", []);
        await Call(teacher, "GET classes", []);
        var created = await Call(teacher, "POST classes", [],
            new CreateClassRequest($"Contract {suffix}", "2025-2026", school, "3B"));
        await Call(teacher, "PATCH classes/{id}", [Id(created)], new UpdateClassRequest(school, "3C"));
        await Call(teacher, "POST classes/{id}/archive", [Id(created)]);
        await Call(teacher, "GET classes", [], query: "includeArchived=true");
        await Call(teacher, "POST classes/{id}/unarchive", [Id(created)]);
        await Call(teacher, "GET classes/{id}/members", [classId]);
        await Call(teacher, "POST classes/{id}/members", [classId],
            new AddClassMemberRequest(Guid.NewGuid(), "Placeholder", ClassMembershipRole.Member));
        await Call(teacher, "POST classes/{id}/members/import", [classId],
            new ImportClassMembersRequest([new ImportClassMemberRow($"imported.{suffix}@school.be", ClassMembershipRole.Member)]));
        await Call(teacher, "POST classes/{id}/members/bulk-import", [classId]);
        await Call(teacher, "GET directory/schools", []);
        await Call(teacher, "GET users/search", [], query: $"q=fo&top=10&company={Uri.EscapeDataString(school)}");

        await Call(teacher, "GET bundles", [], query: "includeArchived=true");
        await Call(teacher, "GET bundles/{id}", [bundle.Id]);
        var written = await Call(adminClient, "POST bundles", [], new WriteBundleRequest(
            $"Contract {suffix}", [new WriteBundleEntry(BundleEntryKind.App, "notepad", BundleEntryMatchType.Exact)]));
        await Call(adminClient, "PUT bundles/{id}", [Id(written)], new WriteBundleRequest(
            $"Contract {suffix} 2", [new WriteBundleEntry(BundleEntryKind.Domain, "example.org", BundleEntryMatchType.Exact)]));

        await Call(teacher, "GET sessions/end-options", [], query: $"classId={classId}");
        var session = await Call(teacher, "POST sessions", [], new StartSessionRequest(
            classId, [bundle.Id], SessionEndMode.SpecificTime, DateTimeOffset.UtcNow.AddHours(1)));
        await Call(teacher, "PUT sessions/{id}/bundles", [Id(session)], new UpdateSessionBundlesRequest([bundle.Id]));
        await Call(teacher, "GET sessions/{id}", [Id(session)]);
        await Call(teacher, "GET sessions/active", []);
        await Call(teacher, "GET sessions/{id}/unblock-requests", [Id(session)]);
        await Call(teacher, "GET sessions/{id}/events", [Id(session)], query: "kind=UnblockRequest&kind=ManualLeave&limit=200");
        await Call(teacher, "GET sessions/history", [], query: "limit=50&offset=0");

        await Call(adminClient, "GET admin/users/admins", []);
        await Call(adminClient, "GET admin/users/candidates", [], query: $"query={Uri.EscapeDataString("Candidate " + suffix)}");
        await Call(adminClient, "GET admin/schools", []);
        await Call(adminClient, "POST admin/schools/activation", [], new SetSchoolActivationRequest(school, IsActive: true));
        await Call(adminClient, "PUT admin/schools/lesson-periods", [], new SetSchoolLessonPeriodsRequest(
            school, [new LessonPeriodDto("08:25", "09:15"), new LessonPeriodDto("09:15", "10:05")]));

        // Admin → Logs (#566): the uploads, a recorded download (a file, so
        // its response isn't described) and the access history it left.
        await Call(adminClient, "GET admin/log-uploads", []);
        using (var download = await adminClient.PostAsJsonAsync("/admin/log-uploads/download", new DownloadLogUploadsRequest(
                   pastSession.Id, [scenario.Students[0].Id], LogAccessPurpose.FraudSuspicion,
                   "Contract scenario download.", "The principal")))
            Assert.True(download.IsSuccessStatusCode, $"POST admin/log-uploads/download answered {(int)download.StatusCode}");
        await Call(adminClient, "GET admin/log-access", []);

        return calls;
    }

    private async Task AddLogUploadAsync(Session session, Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        db.SessionLogUploads.Add(new SessionLogUpload
        {
            SessionId = session.Id,
            UserId = userId,
            WindowFrom = session.StartedAt.AddMinutes(-30),
            WindowTo = session.EndedAt!.Value.AddMinutes(10),
            IsComplete = true,
            UploadedAt = DateTimeOffset.UtcNow,
            AgentVersion = "0.0.0-contract",
            ContentLength = 2,
            Content = [0x1f, 0x8b],
        });
        await db.SaveChangesAsync();
    }

    /// <summary>What the agent's calls got, and what a call that went wrong left behind.</summary>
    private sealed record AgentCalls(Dictionary<string, List<Response>> Responses, List<string> Problems);

    /// <summary>
    /// Calls each route the agent calls, the way it does: a student in a
    /// running session they joined, rejoining after a restart, asking which of
    /// their log windows to upload and uploading one, and a classmate joining
    /// by code, once with a code that isn't a session's.
    /// </summary>
    private async Task<AgentCalls> CallEveryRouteTheAgentReadsAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        var bundle = await TestSeed.AddBundleAsync(_factory, $"Agent bundle {suffix}");
        await TestSeed.AddBundleEntryAsync(_factory, bundle.Id, BundleEntryKind.Domain, "example.com", BundleEntryMatchType.Suffix);
        await TestSeed.AddBundleEntryAsync(_factory, bundle.Id, BundleEntryKind.App, "winword", BundleEntryMatchType.Exact);

        var calls = new Dictionary<string, List<Response>>(StringComparer.Ordinal);
        var problems = new List<string>();
        var (student, classmate) = (scenario.Students[0], scenario.Students[1]);
        using var teacher = Client(scenario.Teacher, "Teacher");
        using var agent = Client(student, "Student");
        using var classmateAgent = Client(classmate, "Student");

        var session = await CallAsync(calls, teacher, "POST sessions", [],
            new StartSessionRequest(scenario.Class.Id, [bundle.Id]));
        var sessionId = Guid.Parse(session!["id"]!.GetValue<string>());
        var joinCode = session["joinCode"]!.GetValue<string>();
        using (var scope = _factory.Services.CreateScope())
        {
            // The agent's JoinSession on the hub, which /sessions/rejoinable
            // looks for: it lists the sessions the student is in.
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            var participant = await db.SessionParticipants.SingleAsync(p => p.SessionId == sessionId && p.UserId == student.Id);
            participant.JoinedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await CallAsync(calls, agent, "GET sessions/rejoinable", []);
        var pending = await CallAsync(calls, agent, "GET log-uploads/pending", []);

        // The upload's body is the gzipped log text, up to the window end GET
        // pending reported.
        var to = pending!["windows"]!.AsArray().Single(w => w!["sessionId"]!.GetValue<string>() == sessionId.ToString())!["to"]!;
        using var gzip = new MemoryStream();
        await using (var zip = new GZipStream(gzip, CompressionLevel.Fastest, leaveOpen: true))
            await zip.WriteAsync("2026-11-22 08:30:00 [INF] contract\n"u8.ToArray());
        using (var upload = new HttpRequestMessage(HttpMethod.Post,
                   $"/log-uploads/{sessionId}?to={Uri.EscapeDataString(to.GetValue<string>())}&agentVersion=0.0.0-contract")
        {
            Content = new ByteArrayContent(gzip.ToArray()),
        })
        {
            upload.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(LogUploadsController.GzipContentType);
            using var uploaded = await agent.SendAsync(upload);
            Assert.True(uploaded.IsSuccessStatusCode, $"POST log-uploads/{sessionId} answered {(int)uploaded.StatusCode}");
        }

        // A code that is no session's: the agent shows the message the backend
        // sends with it, so that body has to be what the route declares.
        var describer = new ContractShapeDescriber(WireOptions);
        var unknownCode = joinCode == "999999" ? "999998" : "999999";
        using (var unknown = await classmateAgent.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(unknownCode)))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            describer.Check(
                describer.Describe(typeof(JoinByCodeErrorResponse)),
                JsonNode.Parse(await unknown.Content.ReadAsStringAsync()),
                "POST sessions/join-by-code 404 body",
                problems);
        }
        await CallAsync(calls, classmateAgent, "POST sessions/join-by-code", [], new JoinByCodeRequest(joinCode));

        return new AgentCalls(calls, problems);
    }

    /// <summary>
    /// Calls a route by its contract key, filling the path's placeholders in
    /// order, and records what the action returned with the body that went
    /// over the wire.
    /// </summary>
    private async Task<JsonNode?> CallAsync(
        Dictionary<string, List<Response>> calls,
        HttpClient client,
        string route,
        object[] path,
        object? body = null,
        string? query = null)
    {
        var (method, template) = (route[..route.IndexOf(' ')], route[(route.IndexOf(' ') + 1)..]);
        var segment = 0;
        var url = "/" + Regex.Replace(template, @"\{[^}]+\}", _ => Uri.EscapeDataString(path[segment++].ToString()!))
                  + (query is null ? "" : "?" + query);

        _factory.Results.Clear();
        using var request = new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = body is null ? null : JsonContent.Create(body, body.GetType()),
        };
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{method} {url} answered {(int)response.StatusCode}: {text}");

        var result = Assert.Single(_factory.Results);
        var key = Key(Routes().Values.Single(a => a.ActionDescriptor.Id == result.Action.Id));
        var json = text.Length == 0 ? null : JsonNode.Parse(text);
        if (!calls.TryGetValue(key, out var list))
            calls[key] = list = [];
        list.Add(new Response(result.Value?.GetType() ?? typeof(object), json));
        return json;
    }

    private HttpClient Client(User user, string role)
    {
        var client = _factory.CreateClient();
        TestAuth.Set(client, user, role);
        return client;
    }

    /// <summary>A REST contract under contracts/, found above the test's output directory.</summary>
    private sealed class RestContract
    {
        private RestContract(JsonObject root)
        {
            Enums = root["enums"]?.AsObject() ?? new JsonObject();
            Routes = root["routes"]!.AsObject();
            NotCovered = root["notCovered"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
        }

        public JsonObject Enums { get; }
        public JsonObject Routes { get; }
        public IReadOnlyList<string> NotCovered { get; }

        public static RestContract Load(string relativePath) => new(ContractJson.Load(relativePath));
    }

    // ------- Recording what the actions return -------

    /// <summary>Own factory (own database), with what each action returns recorded.</summary>
    public sealed class ContractFactory : AnchorApiFactory
    {
        public ConcurrentQueue<ActionReturned> Results { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // On, as for the pilot, so GET log-uploads/pending reports the
            // agent's windows and the upload is taken.
            builder.UseSetting($"{LogUploadOptions.SectionName}:{nameof(LogUploadOptions.Enabled)}", "true");
            builder.ConfigureTestServices(services =>
                services.Configure<MvcOptions>(o => o.Filters.Add(new RecordingResultFilter(Results))));
        }
    }

    /// <summary>The object an action returned, before it is written to the response.</summary>
    public sealed record ActionReturned(ActionDescriptor Action, object? Value);

    private sealed class RecordingResultFilter(ConcurrentQueue<ActionReturned> results) : IResultFilter
    {
        public void OnResultExecuting(ResultExecutingContext context)
        {
            if (context.Result is ObjectResult result)
                results.Enqueue(new ActionReturned(context.ActionDescriptor, result.Value));
        }

        public void OnResultExecuted(ResultExecutedContext context)
        {
        }
    }
}
