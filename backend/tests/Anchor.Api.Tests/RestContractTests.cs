using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Anchor.Api.Controllers;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Api.Users;
using Anchor.Domain.Bundles;
using Anchor.Domain.Classes;
using Anchor.Domain.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Anchor.Api.Tests;

/// <summary>
/// The backend's side of the REST contract, contracts/rest.json (#393).
/// Dashboard E2E runs against fake APIs, so it can't see a backend change to a
/// route, a field or an enum value the dashboard depends on. These tests
/// describe the routes as the backend serves them and fail when that differs
/// from the file. A change to the file runs the dashboard's contract tests,
/// which check the dashboard against it.
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
    private readonly RestContract _contract = RestContract.Load();

    public RestContractTests(ContractFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Every_route_is_in_the_contract_or_listed_as_not_covered()
    {
        // A new route has to be described for the dashboard, or left to the
        // agent and the extension on purpose.
        Assert.Equal(
            Routes().Keys.Order(StringComparer.Ordinal),
            _contract.Routes.Select(r => r.Key).Concat(_contract.NotCovered).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_routes_the_dashboard_calls_take_and_return_what_the_contract_says()
    {
        var calls = await CallEveryRouteTheDashboardReadsAsync();
        var routes = Routes();
        var describer = new Describer(WireOptions);
        var served = new JsonObject();
        var wire = new List<string>();
        foreach (var (key, node) in _contract.Routes)
        {
            served[key] = routes.TryGetValue(key, out var api)
                ? DescribeRoute(key, api, node!.AsObject(), calls, describer, wire)
                : null;
        }

        var enums = new JsonObject(served
            .SelectMany(r => Types(r.Value?["request"]).Concat(Types(r.Value?["response"])))
            .Select(t => t.TrimEnd('?'))
            .Where(describer.Enums.ContainsKey)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .Select(name => KeyValuePair.Create(name, (JsonNode?)describer.Enums[name].DeepClone())));

        var differences = Differences(_contract.Routes, served).Concat(Differences(_contract.Enums, enums)).ToList();
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
            {Indented(new JsonObject { ["routes"] = served.DeepClone(), ["enums"] = enums.DeepClone() })}

            Every field of the responses the scenario got, for a field the dashboard starts to read:
            {Indented(describer.Responses)}
            """);
    }

    // ------- The description -------

    /// <summary>
    /// A route as the backend serves it: the body it binds, the query
    /// parameters the file says the dashboard sends that it binds, and, when the
    /// file says the dashboard reads the response, the fields of it the file
    /// lists.
    /// </summary>
    private static JsonObject DescribeRoute(
        string key,
        ApiDescription api,
        JsonObject inFile,
        IReadOnlyDictionary<string, List<Response>> calls,
        Describer describer,
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
            route["query"] = Strings(asked.Select(q => q!.GetValue<string>()).Where(bound.Contains));
        }

        if (inFile["response"] is { } read)
        {
            var responses = calls.GetValueOrDefault(key) ?? [];
            var described = responses
                .Select(r => describer.Describe(r.Type))
                .DistinctBy(Canonical)
                .ToList();
            foreach (var response in responses)
                describer.Check(describer.Describe(response.Type), response.Body, $"{key} response", wire);
            if (described.Count > 0)
                describer.Responses[key] = described.Count == 1 ? described[0].DeepClone() : new JsonArray(described.Select(d => d.DeepClone()).ToArray());
            route["response"] = described.Count switch
            {
                0 => null,
                1 => Project(described[0], read),
                _ => new JsonArray(described.Select(d => Project(d, read)).ToArray()),
            };
        }

        return route;
    }

    /// <summary>
    /// What the backend serves, cut down to the fields the file lists: extra
    /// fields are safe, the dashboard doesn't read them. A field the file lists
    /// that the backend doesn't serve stays, as "(not served)".
    /// </summary>
    private static JsonNode? Project(JsonNode? served, JsonNode? inFile) => (served, inFile) switch
    {
        (JsonObject fields, JsonObject listed) => new JsonObject(listed.Select(p => KeyValuePair.Create(
            p.Key,
            fields.TryGetPropertyValue(p.Key, out var field) ? Project(field, p.Value) : JsonValue.Create("(not served)")))),
        (JsonArray { Count: 1 } element, JsonArray { Count: 1 } listed) => new JsonArray(Project(element[0], listed[0])),
        _ => served?.DeepClone(),
    };

    /// <summary>The type names in a shape, enum names among them.</summary>
    private static IEnumerable<string> Types(JsonNode? shape) => shape switch
    {
        JsonObject fields => fields.SelectMany(f => Types(f.Value)),
        JsonArray elements => elements.SelectMany(Types),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => [value.GetValue<string>()],
        _ => [],
    };

    /// <summary>
    /// Describes types the way contracts/rest.json does, through the app's JSON
    /// settings: an object's fields by their wire names, a list by its element,
    /// a leaf by its type (? when its annotation allows null), an enum by its
    /// name, with the values it goes over the wire as kept in <see cref="Enums"/>.
    /// </summary>
    private sealed class Describer(JsonSerializerOptions options)
    {
        private readonly NullabilityInfoContext _nullability = new();
        private readonly Dictionary<string, Type> _enumTypes = new(StringComparer.Ordinal);

        public Dictionary<string, JsonArray> Enums { get; } = new(StringComparer.Ordinal);

        /// <summary>Every field of each response the scenario got, not only those the file lists.</summary>
        public JsonObject Responses { get; } = new();

        public JsonNode Describe(Type type) => Describe(type, nullable: false);

        private JsonNode Describe(Type type, bool nullable)
        {
            if (Nullable.GetUnderlyingType(type) is { } underlying)
                return Describe(underlying, nullable: true);
            if (type.IsEnum)
                return Leaf(EnumName(type), nullable);
            if (type == typeof(string) || type == typeof(Guid))
                return Leaf("string", nullable);
            if (type == typeof(DateTimeOffset) || type == typeof(DateTime))
                return Leaf("date-time", nullable);
            if (type == typeof(bool))
                return Leaf("boolean", nullable);
            if (type.IsPrimitive || type == typeof(decimal))
                return Leaf("number", nullable);

            var info = options.GetTypeInfo(type);
            return info.Kind switch
            {
                JsonTypeInfoKind.Enumerable => new JsonArray(Describe(info.ElementType!, nullable: false)),
                JsonTypeInfoKind.Object => new JsonObject(info.Properties.Select(p =>
                    KeyValuePair.Create(p.Name, (JsonNode?)Describe(p.PropertyType, IsNullable(p))))),
                _ => throw new NotSupportedException(
                    $"{type} goes over the wire as {info.Kind}, which contracts/rest.json has no shape for."),
            };
        }

        private static JsonValue Leaf(string type, bool nullable) => JsonValue.Create(nullable ? type + "?" : type);

        private bool IsNullable(JsonPropertyInfo property) => property.AttributeProvider switch
        {
            PropertyInfo p => _nullability.Create(p).ReadState == NullabilityState.Nullable,
            FieldInfo f => _nullability.Create(f).ReadState == NullabilityState.Nullable,
            _ => !property.PropertyType.IsValueType,
        };

        private string EnumName(Type type)
        {
            if (_enumTypes.TryGetValue(type.Name, out var known) && known != type)
                throw new NotSupportedException($"Two enums named {type.Name}: {known} and {type}.");
            _enumTypes[type.Name] = type;
            Enums[type.Name] = new JsonArray([.. Enum.GetValues(type).Cast<object>()
                .Select(v => JsonSerializer.SerializeToNode(v, type, options))]);
            return type.Name;
        }

        /// <summary>
        /// Checks a description against a body that went over the wire: every
        /// field there, null only where the type allows it, every value of its
        /// type.
        /// </summary>
        public void Check(JsonNode shape, JsonNode? body, string path, List<string> problems)
        {
            switch (shape)
            {
                case JsonObject fields when body is JsonObject sent:
                    foreach (var (name, field) in fields)
                    {
                        if (sent.TryGetPropertyValue(name, out var value))
                            Check(field!, value, $"{path}.{name}", problems);
                        else
                            problems.Add($"  {path}.{name}: described, but not in the body the backend sent");
                    }
                    break;
                case JsonArray element when body is JsonArray sent:
                    foreach (var item in sent)
                        Check(element[0]!, item, $"{path}[]", problems);
                    break;
                case JsonValue leaf when leaf.GetValueKind() == JsonValueKind.String:
                    var type = leaf.GetValue<string>();
                    if (body is null)
                    {
                        if (!type.EndsWith('?'))
                            problems.Add($"  {path}: the backend sent null, which {type} doesn't allow");
                        break;
                    }
                    if (!IsA(type.TrimEnd('?'), body))
                        problems.Add($"  {path}: the backend sent {Text(body)}, which isn't a {type}");
                    break;
                default:
                    problems.Add($"  {path}: the backend sent {Text(body)} where it describes {Text(shape)}");
                    break;
            }
        }

        private bool IsA(string type, JsonNode value) => type switch
        {
            "string" => value.GetValueKind() == JsonValueKind.String,
            "date-time" => value.GetValueKind() == JsonValueKind.String &&
                           DateTimeOffset.TryParse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            "number" => value.GetValueKind() == JsonValueKind.Number,
            "boolean" => value.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
            _ => Enums.TryGetValue(type, out var values) && values.Any(v => JsonNode.DeepEquals(v, value)),
        };
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

    private static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Distinct().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static IEnumerable<string> Differences(JsonObject inFile, JsonObject served) =>
        inFile.Select(p => p.Key).Union(served.Select(p => p.Key))
            .Select(name => (
                Name: name,
                InFile: Canonical(inFile.TryGetPropertyValue(name, out var f) ? f : null),
                Served: Canonical(served.TryGetPropertyValue(name, out var s) ? s : null)))
            .Where(d => d.InFile != d.Served)
            .Select(d => $"  {d.Name}: the file says {d.InFile}, the backend serves {d.Served}");

    /// <summary>
    /// A form that ignores order: fields, query parameters and enum values are
    /// sets, and the file lists them in whatever order reads best.
    /// </summary>
    private static string Canonical(JsonNode? node) => Sorted(node)?.ToJsonString() ?? "null";

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray a => new JsonArray(a
            .Select(Sorted)
            .OrderBy(n => n?.ToJsonString(), StringComparer.Ordinal)
            .ToArray()),
        _ => node?.DeepClone(),
    };

    private static string Indented(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string Text(JsonNode? node) =>
        node?.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";

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
        await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, classId, scenario.Students.Select(s => s.Id).ToList(), ended: true);

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

        async Task<JsonNode?> Call(HttpClient client, string route, object[] path, object? body = null, string? query = null)
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

        var session = await Call(teacher, "POST sessions", [], new StartSessionRequest(classId, [bundle.Id]));
        await Call(teacher, "PUT sessions/{id}/bundles", [Id(session)], new UpdateSessionBundlesRequest([bundle.Id]));
        await Call(teacher, "GET sessions/{id}", [Id(session)]);
        await Call(teacher, "GET sessions/active", []);
        await Call(teacher, "GET sessions/{id}/unblock-requests", [Id(session)]);
        await Call(teacher, "GET sessions/history", [], query: "limit=50&offset=0");

        await Call(adminClient, "GET admin/users/admins", []);
        await Call(adminClient, "GET admin/users/candidates", [], query: $"query={Uri.EscapeDataString("Candidate " + suffix)}");
        await Call(adminClient, "GET admin/schools", []);
        await Call(adminClient, "POST admin/schools/activation", [], new SetSchoolActivationRequest(school, IsActive: true));

        return calls;
    }

    private HttpClient Client(User user, string role)
    {
        var client = _factory.CreateClient();
        TestAuth.Set(client, user, role);
        return client;
    }

    /// <summary>contracts/rest.json, found above the test's output directory.</summary>
    private sealed class RestContract
    {
        private const string RelativePath = "contracts/rest.json";

        private RestContract(JsonObject root)
        {
            Enums = root["enums"]!.AsObject();
            Routes = root["routes"]!.AsObject();
            NotCovered = root["notCovered"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        }

        public JsonObject Enums { get; }
        public JsonObject Routes { get; }
        public IReadOnlyList<string> NotCovered { get; }

        public static RestContract Load()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, RelativePath);
                if (File.Exists(path))
                    return new RestContract(JsonNode.Parse(File.ReadAllText(path))!.AsObject());
            }
            throw new FileNotFoundException($"No {RelativePath} above {AppContext.BaseDirectory}.");
        }
    }

    // ------- Recording what the actions return -------

    /// <summary>Own factory (own database), with what each action returns recorded.</summary>
    public sealed class ContractFactory : AnchorApiFactory
    {
        public ConcurrentQueue<ActionReturned> Results { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
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
