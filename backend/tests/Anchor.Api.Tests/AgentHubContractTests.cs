using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Anchor.Api.Controllers;
using Anchor.Api.Realtime;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Bundles;
using Anchor.Domain.Events;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Anchor.Api.Tests;

/// <summary>
/// The backend's side of the agent's hub contract, contracts/agent-hub.json
/// (#573). The agents in the field lag behind the backend, and nothing else in
/// Backend CI sees a change to what they receive or send: these tests describe
/// the events, the hub methods and the event kinds the agent relies on as the
/// backend serves them, and fail when that differs from the file. A change to
/// the file runs the agent's contract tests, which check the agent against it.
/// <para>
/// Payloads, arguments and answers are described from their types through the
/// hub's JSON settings, and checked against what a scenario's sends carried.
/// Where an event goes comes from the scenario: the teacher starts a session,
/// changes its bundles, grants one request and declines another, and ends it,
/// while a student's agent makes every call in the file the way the file says.
/// </para>
/// </summary>
public sealed class AgentHubContractTests : IClassFixture<SessionHubContractTests.ContractFactory>
{
    private readonly SessionHubContractTests.ContractFactory _factory;
    private readonly JsonObject _contract = ContractJson.Load("contracts/agent-hub.json");

    public AgentHubContractTests(SessionHubContractTests.ContractFactory factory)
    {
        _factory = factory;
    }

    private JsonObject Events => _contract["events"]!.AsObject();
    private JsonObject Methods => _contract["methods"]!.AsObject();

    [Fact]
    public async Task The_hub_serves_the_agent_what_the_contract_says()
    {
        _factory.Sends.Clear();
        var problems = new List<string>();
        var sessionId = await RunTheScenarioAsync(problems);

        var describer = new ContractShapeDescriber(WireOptions);
        var audiences = await AudiencesAsync(sessionId);
        var events = DescribeEvents(describer, audiences, problems);
        var served = new JsonObject
        {
            ["hub"] = SessionHub.Path,
            // The file explains each audience its events go to, and no other.
            ["audiences"] = ContractJson.Strings(events
                .SelectMany(e => e.Value?["audience"]?.AsArray() ?? [])
                .Select(a => a!.GetValue<string>())),
            ["events"] = events,
            ["methods"] = DescribeMethods(describer),
        };
        var inFile = new JsonObject
        {
            ["hub"] = _contract["hub"]!.DeepClone(),
            ["audiences"] = ContractJson.Strings(_contract["audiences"]!.AsObject().Select(a => a.Key)),
            ["events"] = Events.DeepClone(),
            ["methods"] = Methods.DeepClone(),
        };

        var differences = ContractJson.Differences(Flattened(inFile), Flattened(served)).ToList();
        if (differences.Count == 0 && problems.Count == 0)
            return;

        Assert.Fail($"""
            contracts/agent-hub.json no longer describes the backend (#573):
            {string.Join(Environment.NewLine, differences.Concat(problems))}

            If the backend change is intended, put what it serves in the file. That runs the agent's contract
            tests (Agent E2E), which check that the agent still handles it, and the agents already in the field
            have to survive it too. A null payload means the scenario in this test never sent the event; a
            kind left out of ReportEvent's kinds is one the hub no longer takes. The hub as the backend serves
            it, each payload and answer cut down to the fields the file lists:
            {ContractJson.Indented(served)}
            """);
    }

    // ------- The description -------

    /// <summary>
    /// The file's sections with each event and method on its own, so a
    /// difference names the one that differs.
    /// </summary>
    private static JsonObject Flattened(JsonObject contract) => new(contract.SelectMany(section =>
        section.Value is JsonObject entries && section.Key is "events" or "methods"
            ? entries.Select(e => KeyValuePair.Create($"{section.Key}.{e.Key}", e.Value?.DeepClone()))
            : [KeyValuePair.Create(section.Key, section.Value?.DeepClone())]));

    private JsonObject DescribeEvents(
        ContractShapeDescriber describer, IReadOnlyDictionary<string, string> audiences, List<string> problems)
    {
        var described = new JsonObject();
        foreach (var (name, node) in Events)
        {
            var parameters = typeof(ISessionHubClient).GetMethod(name)?.GetParameters();
            if (parameters is not { Length: 1 })
            {
                described[name] = parameters is null ? null : new JsonObject { ["argumentCount"] = parameters.Length };
                continue;
            }

            var shape = describer.Describe(parameters[0].ParameterType);
            var sends = _factory.Sends.Where(s => s.Method == name).ToList();
            foreach (var send in sends)
                describer.Check(shape, JsonSerializer.SerializeToNode(send.Args[0], WireOptions), $"{name} payload", problems);

            var audience = sends
                .SelectMany(s => s.Targets)
                .Select(t => audiences.TryGetValue(t, out var a) ? a : $"elsewhere: {t}")
                .Distinct()
                .Order(StringComparer.Ordinal);
            described[name] = new JsonObject
            {
                ["payload"] = sends.Count == 0 ? null : ContractJson.Project(shape, node!["payload"]),
                ["audience"] = ContractJson.Strings(audience),
            };
        }
        return described;
    }

    private JsonObject DescribeMethods(ContractShapeDescriber describer)
    {
        var described = new JsonObject();
        foreach (var (name, node) in Methods)
        {
            var method = SessionHubContractTests.HubMethods().SingleOrDefault(m => m.Name == name);
            if (method is null)
            {
                described[name] = null;
                continue;
            }

            var entry = new JsonObject();
            var parameters = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).ToArray();
            if (parameters.Length == 1)
                entry["argument"] = describer.Describe(parameters[0].ParameterType);
            else if (parameters.Length > 1)
                entry["argumentCount"] = parameters.Length;

            var answer = method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                ? method.ReturnType.GetGenericArguments()[0]
                : null;
            if (node!["returns"] is { } read)
                entry["returns"] = answer is null ? null : ContractJson.Project(describer.Describe(answer), read);
            if (answer is not null && !answer.IsValueType && describer.IsNullable(method.ReturnParameter))
                entry["returnsNull"] = true;

            // A note for the agent's side (#569): the hub still takes the
            // method, which is all the backend can show for it.
            if (node["retired"] is { } retired)
                entry["retired"] = retired.DeepClone();

            if (node["kinds"] is JsonArray kinds)
            {
                // The kinds the hub takes: ReportEvent parses each by its EventKind name.
                var names = Enum.GetNames<EventKind>().ToHashSet(StringComparer.Ordinal);
                entry["kinds"] = ContractJson.Strings(kinds.Select(k => k!.GetValue<string>()).Where(names.Contains));
            }

            described[name] = entry;
        }
        return described;
    }

    /// <summary>The hub's JSON protocol settings, which name every field on the wire.</summary>
    private JsonSerializerOptions WireOptions
    {
        get
        {
            if (_wireOptions is not null)
                return _wireOptions;
            var options = new JsonSerializerOptions(
                _factory.Services.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions);
            options.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver();
            return _wireOptions = options;
        }
    }

    private JsonSerializerOptions? _wireOptions;

    /// <summary>
    /// The audience each group a send can target stands for, for one session:
    /// its session group, its teacher's user group, its participants' user
    /// groups. A send anywhere else shows up under its raw target.
    /// </summary>
    private async Task<Dictionary<string, string>> AudiencesAsync(Guid sessionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        var participantIds = await db.SessionParticipants.AsNoTracking()
            .Where(p => p.SessionId == sessionId)
            .Select(p => p.UserId)
            .ToListAsync();

        var audiences = participantIds.ToDictionary(SessionHub.UserGroupName, _ => "participants");
        audiences[SessionHub.UserGroupName(session.TeacherId)] = "teacher";
        audiences[SessionHub.GroupName(sessionId)] = "session";
        return audiences;
    }

    // ------- The scenario -------

    /// <summary>
    /// The teacher starts a session with a bundle, changes its bundles, grants
    /// one request and declines another, and ends it. The student's agent joins
    /// and makes every call in the file, each argument built from the file, as
    /// the agent sends it; a classmate's agent declines.
    /// </summary>
    private async Task<Guid> RunTheScenarioAsync(List<string> problems)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        var (student, classmate) = (scenario.Students[0], scenario.Students[1]);
        var bundle = await TestSeed.AddBundleAsync(_factory, $"Agent hub {suffix}");
        await TestSeed.AddBundleEntryAsync(_factory, bundle.Id, BundleEntryKind.Domain, "example.com", BundleEntryMatchType.Suffix);
        await TestSeed.AddBundleEntryAsync(_factory, bundle.Id, BundleEntryKind.App, "winword", BundleEntryMatchType.Exact);

        using var teacher = _factory.CreateClient();
        TestAuth.SetTeacher(teacher, scenario.Teacher);
        var started = await teacher.PostAsJsonAsync("/sessions", new StartSessionRequest(scenario.Class.Id, [bundle.Id]));
        started.EnsureSuccessStatusCode();
        var sessionId = (await started.Content.ReadFromJsonAsync<StartSessionResponse>())!.Id;

        await using var agent = Connect(student);
        await using var classmateAgent = Connect(classmate);
        await agent.StartAndAwaitOnConnectedAsync();
        await classmateAgent.StartAndAwaitOnConnectedAsync();

        async Task CallAsync(HubConnection connection, string method, string? kind = null)
        {
            if (Methods[method] is not JsonObject entry)
            {
                problems.Add($"  {method}: the scenario calls it, but the file doesn't list it");
                return;
            }
            var args = entry["argument"] is { } shape ? new[] { ArgumentFor(shape, sessionId, kind) } : [];
            try
            {
                var answer = await connection.InvokeCoreAsync<JsonElement>(method, args);
                if (entry["returns"] is not null && entry["returnsNull"] is null && answer.ValueKind == JsonValueKind.Null)
                    problems.Add($"  {method}: answered null, which the file doesn't allow");
            }
            catch (HubException ex)
            {
                problems.Add($"  {method}{(kind is null ? "" : $" ({kind})")}: refused as the file says to send it: {ex.Message}");
            }
        }

        await CallAsync(agent, "JoinSession");
        await CallAsync(agent, "Heartbeat");
        await CallAsync(agent, "ExtensionHeartbeat");
        await CallAsync(agent, "IsInSession");
        await CallAsync(agent, "GetStartedSession");
        await CallAsync(agent, "GetSessionAllowlist");

        (await teacher.PutAsJsonAsync($"/sessions/{sessionId}/bundles", new UpdateSessionBundlesRequest([bundle.Id])))
            .EnsureSuccessStatusCode();
        (await teacher.PostAsJsonAsync($"/sessions/{sessionId}/unblock", new UnblockGrantRequest(student.Id, "granted.example")))
            .EnsureSuccessStatusCode();
        (await teacher.PostAsJsonAsync($"/sessions/{sessionId}/unblock-requests/deny", new UnblockDenialRequest(student.Id, "denied.example")))
            .EnsureSuccessStatusCode();

        // Every kind the agent reports; AgentKilled last, as it takes the
        // student out of the session.
        var kinds = Methods["ReportEvent"]?["kinds"]?.AsArray().Select(k => k!.GetValue<string>()).ToList() ?? [];
        foreach (var kind in kinds.OrderBy(k => k == nameof(EventKind.AgentKilled)))
            await CallAsync(agent, "ReportEvent", kind);
        await CallAsync(agent, "LeaveSession");
        await CallAsync(classmateAgent, "DeclineSession");

        (await teacher.PostAsync($"/sessions/{sessionId}/end", content: null)).EnsureSuccessStatusCode();
        return sessionId;
    }

    /// <summary>
    /// An argument as the agent sends it per the file: the session's id where
    /// it names one, the kind it reports, an empty payload, and null for the
    /// rest, which the file allows wherever the agent may leave a field empty.
    /// </summary>
    private static object? ArgumentFor(JsonNode shape, Guid sessionId, string? kind) => shape switch
    {
        JsonObject fields => fields.ToDictionary(
            f => f.Key,
            f => f.Key switch
            {
                "sessionId" => (object?)sessionId,
                "kind" => kind,
                "payloadJson" => "{}",
                _ => null,
            }),
        _ => sessionId,
    };

    private HubConnection Connect(User user)
    {
        var server = _factory.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, _contract["hub"]!.GetValue<string>().TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[FakeJwtBearerHandler.HeaderOid] = user.EntraOid.ToString();
                options.Headers[FakeJwtBearerHandler.HeaderRole] = "Student";
                options.Headers[FakeJwtBearerHandler.HeaderName] = user.DisplayName;
            })
            .Build();
    }
}
