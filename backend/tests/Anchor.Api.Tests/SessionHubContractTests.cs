using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Anchor.Api.Controllers;
using Anchor.Api.Realtime;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Events;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Anchor.Api.Tests;

/// <summary>
/// The backend's side of the session hub contract, contracts/session-hub.json
/// (#390). Dashboard E2E runs against a stubbed hub, so it can't see a backend
/// change to what the dashboard receives: #354 moved <c>SessionEnded</c> from
/// the session group to user groups, and the live page broke with every test
/// green. These tests describe the hub as the backend serves it and fail when
/// that differs from the file. A change to the file runs the dashboard's
/// contract tests, which check the dashboard against it.
/// <para>
/// Where an event goes and what it carries come from real calls: a scenario
/// drives the hub, the REST endpoints, <c>SessionEnder</c> and the
/// heartbeat monitor, and every send is recorded under the real
/// <c>IHubContext</c>, at the hub lifetime manager, with the groups it went to
/// and its arguments as the hub's JSON protocol writes them.
/// </para>
/// </summary>
public sealed class SessionHubContractTests : IClassFixture<SessionHubContractTests.ContractFactory>
{
    private readonly ContractFactory _factory;
    private readonly SessionHubContract _contract = SessionHubContract.Load();

    public SessionHubContractTests(ContractFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_events_the_dashboard_listens_for_go_to_and_carry_what_the_contract_says()
    {
        _factory.Sends.Clear();
        var (scenario, sessionId) = await StartSessionAsync();
        var student = scenario.Students[0];

        await using var teacher = Connect(scenario.Teacher, "Teacher");
        await using var own = Connect(student, "Student");
        await teacher.StartAndAwaitOnConnectedAsync();
        await own.StartAndAwaitOnConnectedAsync();

        // The teacher's live page subscribes; the student's agent joins, asks
        // to open a site, trips a tamper check, goes quiet, comes back and
        // leaves; then the teacher ends the session.
        await teacher.InvokeAsync<JoinSessionResult>("JoinSession", new JoinSessionRequest(sessionId, JoinCode: null));
        await own.InvokeAsync<JoinSessionResult>("JoinSession", new JoinSessionRequest(sessionId, JoinCode: null));
        await own.InvokeAsync("ReportEvent", new ReportEventRequest(
            sessionId, nameof(EventKind.UnblockRequest),
            """{"url":"https://reddit.com/r/aww","host":"reddit.com"}""", OccurredAt: null));
        await own.InvokeAsync("ReportEvent", new ReportEventRequest(
            sessionId, nameof(EventKind.TamperDetected), """{"kind":"inprivate_opened"}""", OccurredAt: null));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var tracker = new HeartbeatTracker();
        var monitor = ActivatorUtilities.CreateInstance<HeartbeatMonitor>(_factory.Services, tracker, (TimeProvider)clock);
        tracker.Record(sessionId, student.Id, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(5));
        await monitor.ScanOnceAsync(CancellationToken.None);
        tracker.Record(sessionId, student.Id, clock.GetUtcNow());
        await monitor.ScanOnceAsync(CancellationToken.None);
        await own.InvokeAsync("LeaveSession", sessionId);
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        (await client.PostAsync($"/sessions/{sessionId}/end", content: null)).EnsureSuccessStatusCode();

        var audiences = await AudiencesAsync(sessionId, scenario.Teacher.Id);
        var served = new JsonObject();
        foreach (var (name, _) in _contract.Events)
        {
            var sends = _factory.Sends.Where(s => s.Method == name).ToList();
            served[name] = sends.Count == 0 ? null : DescribeEvent(name, sends, audiences);
        }

        AssertDescribes(_contract.Events, served, "events");
    }

    [Fact]
    public async Task The_hub_takes_the_methods_the_dashboard_calls_in_the_shape_the_contract_gives()
    {
        var served = new JsonObject();
        foreach (var (name, _) in _contract.Methods)
        {
            var method = HubMethods().SingleOrDefault(m => m.Name == name);
            served[name] = method is null ? null : DescribeParameters(method);
        }
        AssertDescribes(_contract.Methods, served, "methods");

        // And the hub accepts them sent that way, as the dashboard sends them:
        // the teacher's live page joins its session and leaves it.
        var (scenario, sessionId) = await StartSessionAsync();
        await using var teacher = Connect(scenario.Teacher, "Teacher");
        await teacher.StartAndAwaitOnConnectedAsync();
        var joined = await teacher.InvokeAsync<JoinSessionResult>(
            "JoinSession", ArgumentFor(_contract.Methods["JoinSession"]!.AsObject(), sessionId));
        Assert.Equal(sessionId, joined.SessionId);
        await teacher.InvokeAsync("LeaveSession", ArgumentFor(_contract.Methods["LeaveSession"]!.AsObject(), sessionId));
    }

    [Fact]
    public void Every_hub_event_and_method_is_in_the_contract_or_listed_as_not_covered()
    {
        // A new event or hub method has to be described for the dashboard, or
        // left to the agent and the extension on purpose.
        Assert.Equal(
            typeof(ISessionHubClient).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal),
            _contract.Events.Select(e => e.Key).Concat(_contract.NotCoveredEvents).Order(StringComparer.Ordinal));
        Assert.Equal(
            HubMethods().Select(m => m.Name).Order(StringComparer.Ordinal),
            _contract.Methods.Select(m => m.Key).Concat(_contract.NotCoveredMethods).Order(StringComparer.Ordinal));
        Assert.Equal(AudienceNames.Order(StringComparer.Ordinal), _contract.Audiences.Order(StringComparer.Ordinal));
    }

    // ------- The description -------

    /// <summary>The audience names, in the order a description lists them.</summary>
    private static readonly string[] AudienceNames = ["session", "teacher", "participants"];

    /// <summary>
    /// The audience each group a send can target stands for, for one session:
    /// its session group, its teacher's user group, its participants' user
    /// groups. A send anywhere else shows up under its raw target.
    /// </summary>
    private async Task<Dictionary<string, string>> AudiencesAsync(Guid sessionId, Guid teacherId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var participantIds = await db.SessionParticipants.AsNoTracking()
            .Where(p => p.SessionId == sessionId)
            .Select(p => p.UserId)
            .ToListAsync();

        var audiences = participantIds.ToDictionary(SessionHub.UserGroupName, _ => "participants");
        audiences[SessionHub.UserGroupName(teacherId)] = "teacher";
        audiences[SessionHub.GroupName(sessionId)] = "session";
        return audiences;
    }

    private JsonObject DescribeEvent(string name, IReadOnlyList<HubSend> sends, IReadOnlyDictionary<string, string> audiences)
    {
        var description = DescribeArguments(
            sends[0].Args,
            typeof(ISessionHubClient).GetMethod(name)?.GetParameters());
        var audience = sends
            .SelectMany(s => s.Targets)
            .Select(t => audiences.TryGetValue(t, out var a) ? a : $"elsewhere: {t}")
            .Distinct()
            .OrderBy(a => Array.IndexOf(AudienceNames, a) is var i and >= 0 ? i : AudienceNames.Length)
            .ThenBy(a => a, StringComparer.Ordinal);
        description["audience"] = Strings(audience);
        return description;
    }

    /// <summary>
    /// A message's one argument as it goes over the wire: an object's field
    /// names, or the parameter name of a bare value.
    /// </summary>
    private JsonObject DescribeArguments(object?[] args, ParameterInfo[]? parameters)
    {
        if (args.Length != 1)
            return new JsonObject { ["argumentCount"] = args.Length };

        var json = JsonSerializer.SerializeToElement(args[0], args[0]?.GetType() ?? typeof(object), WireOptions);
        return json.ValueKind == JsonValueKind.Object
            ? new JsonObject { ["fields"] = Strings(json.EnumerateObject().Select(p => p.Name)) }
            : new JsonObject { ["argument"] = parameters?.FirstOrDefault()?.Name };
    }

    private JsonObject DescribeParameters(MethodInfo method)
    {
        var parameters = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).ToArray();
        if (parameters.Length != 1)
            return new JsonObject { ["argumentCount"] = parameters.Length };

        var info = WireOptions.GetTypeInfo(parameters[0].ParameterType);
        return info.Kind == JsonTypeInfoKind.Object
            ? new JsonObject { ["fields"] = Strings(info.Properties.Select(p => p.Name)) }
            : new JsonObject { ["argument"] = parameters[0].Name };
    }

    /// <summary>The hub's JSON protocol settings, which name every field on the wire.</summary>
    private JsonSerializerOptions WireOptions => _wireOptions ??= WireOptionsOf(_factory.Services);

    private JsonSerializerOptions? _wireOptions;

    private static JsonSerializerOptions WireOptionsOf(IServiceProvider services)
    {
        var options = new JsonSerializerOptions(
            services.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions);
        options.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver();
        return options;
    }

    /// <summary>
    /// The methods a client can invoke on the hub, found the way SignalR finds
    /// them: its public instance methods, less those of <see cref="Hub"/>.
    /// </summary>
    private static IEnumerable<MethodInfo> HubMethods() =>
        typeof(SessionHub).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName)
            .Where(m => m.GetBaseDefinition().DeclaringType is { } declaring &&
                        declaring != typeof(object) &&
                        declaring != typeof(Hub) &&
                        !(declaring.IsGenericType && declaring.GetGenericTypeDefinition() == typeof(Hub<>)));

    /// <summary>
    /// A method's argument as the dashboard sends it per the contract: its
    /// session id under the name the contract gives it, nothing else set.
    /// </summary>
    private static object? ArgumentFor(JsonObject method, Guid sessionId)
    {
        object? ValueOf(string name) => name == "sessionId" ? sessionId : null;
        return method["argument"] is { } bare
            ? ValueOf(bare.GetValue<string>())
            : method["fields"]!.AsArray().Select(f => f!.GetValue<string>()).ToDictionary(f => f, ValueOf);
    }

    private static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Distinct().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static void AssertDescribes(JsonObject inFile, JsonObject served, string section)
    {
        var differences = inFile.Select(p => p.Key).Union(served.Select(p => p.Key))
            .Select(name => (
                Name: name,
                InFile: Canonical(inFile.TryGetPropertyValue(name, out var f) ? f : null),
                Served: Canonical(served.TryGetPropertyValue(name, out var s) ? s : null)))
            .Where(d => d.InFile != d.Served)
            .Select(d => $"  {d.Name}: the file says {d.InFile}, the backend serves {d.Served}")
            .ToList();
        if (differences.Count == 0)
            return;

        Assert.Fail($"""
            The "{section}" in contracts/session-hub.json no longer describe the backend (#390):
            {string.Join(Environment.NewLine, differences)}

            If the backend change is intended, put what it serves in the file. That runs the dashboard's
            contract tests (Dashboard CI), which check that the dashboard still handles it. A null means
            the scenario in this test never sent it. All of "{section}" as the backend serves it:
            {served.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}
            """);
    }

    /// <summary>
    /// A form that ignores order: field and audience lists are sets, and the
    /// file lists them in whatever order reads best.
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

    // ------- The scenario -------

    private async Task<(TestScenario Scenario, Guid SessionId)> StartSessionAsync()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(scenario.Class.Id, null));
        response.EnsureSuccessStatusCode();
        return (scenario, (await response.Content.ReadFromJsonAsync<StartSessionResponse>())!.Id);
    }

    /// <summary>A connection to the hub at the contract's path.</summary>
    private HubConnection Connect(User user, string role)
    {
        var server = _factory.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, _contract.Hub.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[FakeJwtBearerHandler.HeaderOid] = user.EntraOid.ToString();
                options.Headers[FakeJwtBearerHandler.HeaderRole] = role;
                options.Headers[FakeJwtBearerHandler.HeaderName] = user.DisplayName;
            })
            .Build();
    }

    /// <summary>contracts/session-hub.json, found above the test's output directory.</summary>
    private sealed class SessionHubContract
    {
        private const string RelativePath = "contracts/session-hub.json";

        private SessionHubContract(JsonObject root)
        {
            Hub = root["hub"]!.GetValue<string>();
            Audiences = root["audiences"]!.AsObject().Select(a => a.Key).ToList();
            Events = root["events"]!.AsObject();
            Methods = root["methods"]!.AsObject();
            NotCoveredEvents = Names(root["notCovered"]!["events"]!);
            NotCoveredMethods = Names(root["notCovered"]!["methods"]!);
        }

        public string Hub { get; }
        public IReadOnlyList<string> Audiences { get; }
        public JsonObject Events { get; }
        public JsonObject Methods { get; }
        public IReadOnlyList<string> NotCoveredEvents { get; }
        public IReadOnlyList<string> NotCoveredMethods { get; }

        public static SessionHubContract Load()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, RelativePath);
                if (File.Exists(path))
                    return new SessionHubContract(JsonNode.Parse(File.ReadAllText(path))!.AsObject());
            }
            throw new FileNotFoundException($"No {RelativePath} above {AppContext.BaseDirectory}.");
        }

        private static List<string> Names(JsonNode list) =>
            list.AsArray().Select(n => n!.GetValue<string>()).ToList();
    }

    // ------- Recording the sends -------

    /// <summary>
    /// Own factory (own database and singletons), with every hub send recorded
    /// on its way out.
    /// </summary>
    public sealed class ContractFactory : AnchorApiFactory
    {
        public ConcurrentQueue<HubSend> Sends => Services.GetRequiredService<RecordingHubLifetimeManager>().Sends;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<RecordingHubLifetimeManager>();
                services.AddSingleton<HubLifetimeManager<SessionHub>>(
                    sp => sp.GetRequiredService<RecordingHubLifetimeManager>());
            });
        }
    }

    /// <summary>One message the hub sent: its name, its arguments and where it went.</summary>
    public sealed record HubSend(string Method, object?[] Args, IReadOnlyList<string> Targets);

    /// <summary>
    /// SignalR's own lifetime manager, which every <c>IHubContext</c> send goes
    /// through, with each send recorded. A group is recorded by its name;
    /// anything else (all connections, one connection, a SignalR user) by what
    /// it targets, so it can't pass for a group.
    /// </summary>
    public sealed class RecordingHubLifetimeManager : HubLifetimeManager<SessionHub>
    {
        private readonly DefaultHubLifetimeManager<SessionHub> _inner;

        public RecordingHubLifetimeManager(ILogger<DefaultHubLifetimeManager<SessionHub>> logger)
        {
            _inner = new DefaultHubLifetimeManager<SessionHub>(logger);
        }

        public ConcurrentQueue<HubSend> Sends { get; } = new();

        private void Record(string methodName, object?[] args, params string[] targets) =>
            Sends.Enqueue(new HubSend(methodName, args, targets));

        public override Task OnConnectedAsync(HubConnectionContext connection) =>
            _inner.OnConnectedAsync(connection);

        public override Task OnDisconnectedAsync(HubConnectionContext connection) =>
            _inner.OnDisconnectedAsync(connection);

        public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            _inner.AddToGroupAsync(connectionId, groupName, cancellationToken);

        public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            _inner.RemoveFromGroupAsync(connectionId, groupName, cancellationToken);

        public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default)
        {
            Record(methodName, args, "all connections");
            return _inner.SendAllAsync(methodName, args, cancellationToken);
        }

        public override Task SendAllExceptAsync(string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default)
        {
            Record(methodName, args, "all connections");
            return _inner.SendAllExceptAsync(methodName, args, excludedConnectionIds, cancellationToken);
        }

        public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default)
        {
            Record(methodName, args, $"connection {connectionId}");
            return _inner.SendConnectionAsync(connectionId, methodName, args, cancellationToken);
        }

        public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args, CancellationToken cancellationToken = default)
        {
            Record(methodName, args, connectionIds.Select(c => $"connection {c}").ToArray());
            return _inner.SendConnectionsAsync(connectionIds, methodName, args, cancellationToken);
        }

        public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default)
        {
            Record(methodName, args, groupName);
            return _inner.SendGroupAsync(groupName, methodName, args, cancellationToken);
        }

        public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args, CancellationToken cancellationToken = default)
        {
            Record(methodName, args, groupNames.ToArray());
            return _inner.SendGroupsAsync(groupNames, methodName, args, cancellationToken);
        }

        public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default)
        {
            Record(methodName, args, groupName);
            return _inner.SendGroupExceptAsync(groupName, methodName, args, excludedConnectionIds, cancellationToken);
        }

        public override Task SendUserAsync(string userId, string methodName, object?[] args, CancellationToken cancellationToken = default)
        {
            Record(methodName, args, $"SignalR user {userId}");
            return _inner.SendUserAsync(userId, methodName, args, cancellationToken);
        }

        public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args, CancellationToken cancellationToken = default)
        {
            Record(methodName, args, userIds.Select(u => $"SignalR user {u}").ToArray());
            return _inner.SendUsersAsync(userIds, methodName, args, cancellationToken);
        }

        public override Task<T> InvokeConnectionAsync<T>(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken) =>
            _inner.InvokeConnectionAsync<T>(connectionId, methodName, args, cancellationToken);

        public override Task SetConnectionResultAsync(string connectionId, CompletionMessage result) =>
            _inner.SetConnectionResultAsync(connectionId, result);

        public override bool TryGetReturnType(string invocationId, [NotNullWhen(true)] out Type? type) =>
            _inner.TryGetReturnType(invocationId, out type);
    }
}
