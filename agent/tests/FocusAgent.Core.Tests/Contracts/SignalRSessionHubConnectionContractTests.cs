using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FocusAgent.Core.Auth;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Tests.Contracts;

/// <summary>
/// The agent's side of contracts/agent-hub.json (#573): the real
/// <see cref="SignalRSessionHubConnection"/> against a hub that serves the
/// file. Each event the file lists, sent with a payload built from it, has to
/// reach the agent with every field read; each method the agent calls has to
/// arrive in exactly the shape the file gives, with every event kind the agent
/// reports, and the agent has to read the answers the file describes. Backend
/// CI holds the backend to the same file (AgentHubContractTests), so a change
/// on either side fails until both agree.
/// </summary>
public sealed class SignalRSessionHubConnectionContractTests : IAsyncLifetime
{
    private static readonly JsonObject Contract = ContractFiles.Load("agent-hub.json");
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(10);

    private static JsonObject Events => Contract["events"]!.AsObject();
    private static JsonObject Methods => Contract["methods"]!.AsObject();

    private readonly HubCalls _calls = new();
    private WebApplication _server = null!;
    private SignalRSessionHubConnection _agent = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_calls);
        _server = builder.Build();
        _server.MapHub<ContractHub>(Contract["hub"]!.GetValue<string>());
        await _server.StartAsync();
        var address = _server.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        _agent = new SignalRSessionHubConnection(
            Options.Create(new BackendSettings { BaseUrl = address, HubPath = Contract["hub"]!.GetValue<string>() }),
            Options.Create(new RealtimeSettings()),
            Options.Create(new DevSettings()),
            new NoTokens(),
            NullLogger<SignalRSessionHubConnection>.Instance);
        await _agent.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _agent.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public void The_agent_connects_to_the_hub_the_contract_names()
    {
        Assert.Equal(Contract["hub"]!.GetValue<string>(), new BackendSettings().HubPath);
    }

    [Fact]
    public async Task Every_event_in_the_contract_reaches_the_agent_with_every_field_read()
    {
        var read = new ConcurrentDictionary<string, JsonNode?>();
        var handlers = new Dictionary<string, Action>
        {
            ["SessionStarted"] = () => _agent.SessionStarted += (_, p) => read["SessionStarted"] = ContractFiles.AsRead(p),
            ["SessionEnded"] = () => _agent.SessionEnded += (_, id) => read["SessionEnded"] = JsonValue.Create(id.ToString()),
            ["SessionBundlesUpdated"] = () => _agent.SessionBundlesUpdated += (_, p) => read["SessionBundlesUpdated"] = ContractFiles.AsRead(p),
            ["AllowlistAmended"] = () => _agent.AllowlistAmended += (_, p) => read["AllowlistAmended"] = ContractFiles.AsRead(p),
            ["UnblockDenied"] = () => _agent.UnblockDenied += (_, p) => read["UnblockDenied"] = ContractFiles.AsRead(p),
        };

        // The events the agent raises are the ones the file lists, and this
        // test listens to each of them.
        var raised = typeof(ISessionHubConnection).GetEvents()
            .Select(e => e.Name)
            .Where(n => n != nameof(ISessionHubConnection.StateChanged));
        Assert.Equal(Events.Select(e => e.Key).Order(), raised.Order());
        Assert.Equal(Events.Select(e => e.Key).Order(), handlers.Keys.Order());
        foreach (var subscribe in handlers.Values)
            subscribe();

        var hub = _server.Services.GetRequiredService<IHubContext<ContractHub>>();
        var problems = new List<string>();
        foreach (var (name, entry) in Events)
        {
            var sample = ContractFiles.Sample(entry!["payload"]!);
            await hub.Clients.All.SendAsync(name, sample);
            var deadline = DateTime.UtcNow + Arrival;
            while (!read.ContainsKey(name) && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            if (read.TryGetValue(name, out var got))
                problems.AddRange(ContractFiles.ReadDifferences(sample, got, name));
            else
                problems.Add($"{name}: sent as the contract shapes it, but the agent never raised it");
        }

        Assert.True(problems.Count == 0, Report("The agent no longer reads the events contracts/agent-hub.json lists", problems));
    }

    [Fact]
    public async Task Every_method_in_the_contract_reaches_the_hub_in_the_shape_it_gives()
    {
        var sessionId = Guid.NewGuid();
        var kinds = ReportedKinds();
        var calls = new Dictionary<string, Func<Task>>
        {
            ["JoinSession"] = async () =>
            {
                await _agent.JoinSessionAsync(sessionId, null);
                await _agent.JoinSessionAsync(sessionId, "123456");
            },
            ["LeaveSession"] = () => _agent.LeaveSessionAsync(sessionId),
            ["DeclineSession"] = () => _agent.DeclineSessionAsync(sessionId, "user_cancelled"),
            ["Heartbeat"] = async () => Assert.True(await _agent.HeartbeatAsync(sessionId)),
            ["IsInSession"] = async () => Assert.True(await _agent.IsInSessionAsync(sessionId)),
            ["GetStartedSession"] = async () => await _agent.GetStartedSessionAsync(),
            ["GetSessionAllowlist"] = async () => await _agent.GetSessionAllowlistAsync(sessionId),
            ["ReportEvent"] = async () =>
            {
                foreach (var kind in kinds)
                    await _agent.ReportEventAsync(sessionId, kind, "{}", DateTimeOffset.UtcNow);
                await _agent.ReportEventAsync(sessionId, kinds[0], "{}");
            },
        };
        // A retired method is one the agent no longer calls (#569); the
        // backend still takes it for the agents in the field.
        var called = Methods.Where(m => m.Value?["retired"] is null).ToList();
        Assert.Equal(called.Select(m => m.Key).Order(), calls.Keys.Order());
        Assert.DoesNotContain(
            typeof(ISessionHubConnection).GetMethods().Select(m => m.Name),
            n => Methods.Any(m => m.Value?["retired"] is not null && n == $"{m.Key}Async"));

        var problems = new List<string>();
        foreach (var (name, entry) in called)
        {
            _calls.Clear();
            if (entry!["returns"] is { } returns)
                _calls.Answer = ContractFiles.Sample(returns);
            await calls[name]();

            var received = _calls.Received(name);
            if (received.Count == 0)
                problems.Add($"{name}: the agent called it, but the hub never got it");
            foreach (var args in received)
            {
                if (entry["argument"] is { } argument)
                {
                    if (args.Length == 1)
                        problems.AddRange(ContractFiles.Mismatches(argument, args[0], $"{name} argument"));
                    else
                        problems.Add($"{name}: the agent sent {args.Length} arguments, the contract one");
                }
                else if (args.Length != 0)
                {
                    problems.Add($"{name}: the agent sent {args.Length} arguments, the contract none");
                }
            }
        }

        // The kinds it sent are the ones it reports, and the ones the file lists.
        var sent = _calls.Kinds.Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(kinds.Order(StringComparer.Ordinal), sent);
        var listed = Methods["ReportEvent"]!["kinds"]!.AsArray().Select(k => k!.GetValue<string>()).Order(StringComparer.Ordinal);
        if (!listed.SequenceEqual(sent))
            problems.Add($"ReportEvent kinds: the contract lists [{string.Join(", ", listed)}], the agent reports [{string.Join(", ", sent)}]");

        Assert.True(problems.Count == 0, Report("The agent no longer calls the hub the way contracts/agent-hub.json says", problems));
    }

    [Fact]
    public async Task The_agent_reads_every_answer_the_contract_describes()
    {
        var sessionId = Guid.NewGuid();
        var problems = new List<string>();

        _calls.Answer = ContractFiles.Sample(Methods["GetStartedSession"]!["returns"]!);
        var started = await _agent.GetStartedSessionAsync();
        problems.AddRange(ContractFiles.ReadDifferences(_calls.Answer, ContractFiles.AsRead(started), "GetStartedSession answer"));

        // returnsNull: no running session.
        Assert.Equal(true, Methods["GetStartedSession"]!["returnsNull"]?.GetValue<bool>());
        _calls.Answer = null;
        Assert.Null(await _agent.GetStartedSessionAsync());

        _calls.Answer = ContractFiles.Sample(Methods["GetSessionAllowlist"]!["returns"]!);
        var allowlist = await _agent.GetSessionAllowlistAsync(sessionId);
        problems.AddRange(ContractFiles.ReadDifferences(_calls.Answer, ContractFiles.AsRead(allowlist), "GetSessionAllowlist answer"));

        // returnsNull: the student is not in the session.
        Assert.Equal(true, Methods["GetSessionAllowlist"]!["returnsNull"]?.GetValue<bool>());
        _calls.Answer = null;
        Assert.Null(await _agent.GetSessionAllowlistAsync(sessionId));

        Assert.Equal("boolean", Methods["IsInSession"]!["returns"]!.GetValue<string>());
        _calls.InSession = false;
        Assert.False(await _agent.IsInSessionAsync(sessionId));
        _calls.InSession = true;
        Assert.True(await _agent.IsInSessionAsync(sessionId));

        Assert.True(problems.Count == 0, Report("The agent no longer reads the hub's answers as contracts/agent-hub.json says", problems));
    }

    /// <summary>
    /// Every event kind the agent reports: the <c>…Kind</c> constants its
    /// reporters pass to <c>ReportEventAsync</c>, in the app and in Core.
    /// </summary>
    private static List<string> ReportedKinds()
    {
        var src = Path.Combine(RepoRoot(), "agent", "src");
        var constant = new Regex("""const string \w*Kind\s*=\s*"(\w+)"\s*;""");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(d => d is "bin" or "obj"))
            .SelectMany(f => constant.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "contracts")) && Directory.Exists(Path.Combine(dir.FullName, "agent", "src")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException($"No repository root above {AppContext.BaseDirectory}.");
    }

    private static string Report(string headline, List<string> problems) =>
        $"""
        {headline} (#573):
          {string.Join(Environment.NewLine + "  ", problems)}

        If the change is intended, change the file to match. That runs Backend CI (AgentHubContractTests), which
        holds the backend to it, and the agents already in the field have to survive it too.
        """;

    /// <summary>What the hub received, and what it answers.</summary>
    public sealed class HubCalls
    {
        private readonly ConcurrentQueue<(string Method, JsonNode?[] Args)> _received = new();

        public JsonNode? Answer { get; set; }
        public bool InSession { get; set; } = true;

        public void Add(string method, params JsonElement[] args) =>
            _received.Enqueue((method, args.Select(a => JsonNode.Parse(a.GetRawText())).ToArray()));

        public List<JsonNode?[]> Received(string method) =>
            _received.Where(r => r.Method == method).Select(r => r.Args).ToList();

        public IEnumerable<string> Kinds => _received
            .Where(r => r.Method == "ReportEvent" && r.Args.Length == 1)
            .Select(r => r.Args[0]?["kind"]?.GetValue<string>() ?? "");

        public void Clear() => _received.Clear();
    }

    /// <summary>
    /// A hub with the methods the file lists, each recording the argument as it
    /// arrived. SignalR binds a JsonElement as the raw JSON.
    /// </summary>
    public sealed class ContractHub(HubCalls calls) : Hub
    {
        public void JoinSession(JsonElement request) => calls.Add(nameof(JoinSession), request);
        public void LeaveSession(JsonElement sessionId) => calls.Add(nameof(LeaveSession), sessionId);
        public void DeclineSession(JsonElement request) => calls.Add(nameof(DeclineSession), request);
        public void Heartbeat(JsonElement sessionId) => calls.Add(nameof(Heartbeat), sessionId);
        public void ReportEvent(JsonElement request) => calls.Add(nameof(ReportEvent), request);

        public bool IsInSession(JsonElement sessionId)
        {
            calls.Add(nameof(IsInSession), sessionId);
            return calls.InSession;
        }

        public JsonNode? GetStartedSession()
        {
            calls.Add(nameof(GetStartedSession));
            return calls.Answer?.DeepClone();
        }

        public JsonNode? GetSessionAllowlist(JsonElement sessionId)
        {
            calls.Add(nameof(GetSessionAllowlist), sessionId);
            return calls.Answer?.DeepClone();
        }
    }

    private sealed class NoTokens : IAuthTokenProvider
    {
        public Task<AuthResult> AcquireTokenAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("The contract hub takes no token.");

        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("");
    }
}
