using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Extension;
using FocusAgent.Core.Settings;
using FocusAgent.Core.Tamper;
using FocusAgent.Core.Tests.Contracts;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Tests;

/// <summary>
/// The agent's side of contracts/witness.json (#573): every message it sends
/// the extension has exactly the fields the file lists, and it acts on every
/// message the extension sends as the file shapes it. The extension's unit
/// tests hold the extension to the same file, so a change on either end fails
/// until both agree.
/// </summary>
public partial class ExtensionSessionRelayTests
{
    private static readonly JsonObject Witness = ContractFiles.Load("witness.json");

    private static JsonObject ToExtension => Witness["toExtension"]!.AsObject();

    /// <summary>What the extension sends, and what older ones still may.</summary>
    private static IEnumerable<KeyValuePair<string, JsonNode?>> FromExtension =>
        Witness["toAgent"]!.AsObject().Concat(Witness["legacy"]!.AsObject());

    [Fact]
    public void ExtensionMessages_names_every_message_the_witness_contract_lists()
    {
        var named = typeof(ExtensionMessages)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            ToExtension.Select(m => m.Key).Concat(FromExtension.Select(m => m.Key)).Order(StringComparer.Ordinal),
            named);
    }

    [Fact]
    public async Task Every_message_the_agent_sends_the_extension_has_the_fields_the_witness_contract_lists()
    {
        var h = new Harness();
        using var watch = SelfCheckWatch(h);

        h.Channel.RaiseConnected();                                                   // no_session
        var sessionId = await h.JoinAsync(Office);                                    // session_started
        h.Hub.RaiseBundlesUpdated(new SessionBundlesUpdatedPayload(sessionId, [], [Wiki]));          // allowlist_replaced
        h.Hub.RaiseAllowlistAmended(new AllowlistAmendedPayload(sessionId, Guid.NewGuid(), [Office])); // allowlist_amended
        h.Hub.RaiseUnblockDenied(new UnblockDeniedPayload(sessionId, Guid.NewGuid(), "denied.example", DateTimeOffset.UnixEpoch));
        h.Channel.Receive(FromTheExtension("unblock_request", sessionId));            // unblock_request_result, ok
        await WaitForAsync(() => Types(h).Contains("unblock_request_result"));
        h.Hub.ReportThrows = new InvalidOperationException("The hub is down.");
        h.Channel.Receive(FromTheExtension("unblock_request", sessionId));            // unblock_request_result, error
        await WaitForAsync(() => Types(h).Count(t => t == "unblock_request_result") == 2);
        h.Channel.Receive(FromTheExtension("self_check", sessionId, allSites: false));
        Assert.Equal(1, watch.OpenExtensionPage());                                   // open_extension_page
        h.Hub.RaiseSessionEnded(sessionId);                                           // session_ended

        var problems = new List<string>();
        foreach (var message in h.Channel.Messages())
        {
            var node = JsonNode.Parse(message.GetRawText())!.AsObject();
            var type = node["type"]?.GetValue<string>() ?? "(no type)";
            node.Remove("type");
            if (ToExtension[type] is { } shape)
                problems.AddRange(ContractFiles.Mismatches(shape, node, type, mayLeaveOut: true));
            else
                problems.Add($"{type}: sent to the extension, but not in the contract");
        }
        var unsent = ToExtension.Select(m => m.Key).Except(Types(h)).ToList();
        Assert.True(unsent.Count == 0, $"This test never got the agent to send: {string.Join(", ", unsent)}");

        Assert.True(problems.Count == 0, WitnessReport("What the agent sends the extension no longer matches", problems));
    }

    [Fact]
    public async Task The_agent_acts_on_every_message_the_extension_sends_as_the_witness_contract_shapes_it()
    {
        var h = new Harness();
        using var watch = SelfCheckWatch(h);
        h.Channel.RaiseConnected();
        var sessionId = await h.JoinAsync(Office);
        h.Channel.ClearSent();

        var checks = new Dictionary<string, Func<JsonObject, Task>>
        {
            ["ping"] = async _ =>
            {
                await Task.Delay(50);
                Assert.Empty(h.Hub.Reports);
                Assert.Empty(h.Channel.Messages());
            },
            ["blocked_url"] = async sent =>
            {
                var report = await h.Hub.NextReportAsync();
                Assert.Equal((sessionId, "BlockedUrl"), (report.SessionId, report.Kind));
                var payload = JsonNode.Parse(report.PayloadJson)!;
                Assert.Equal(sent["host"]!.GetValue<string>(), payload["host"]!.GetValue<string>());
                Assert.Equal(sent["tabId"]!.GetValue<int>(), payload["tabId"]!.GetValue<int>());
                Assert.Equal((DateTimeOffset?)DateTimeOffset.Parse(sent["occurredAt"]!.GetValue<string>()), report.OccurredAt);
            },
            ["unblock_request"] = async sent =>
            {
                var report = await h.Hub.NextReportAsync();
                Assert.Equal((sessionId, "UnblockRequest"), (report.SessionId, report.Kind));
                var payload = JsonNode.Parse(report.PayloadJson)!;
                Assert.Equal(sent["url"]!.GetValue<string>(), payload["url"]!.GetValue<string>());
                Assert.Equal(sent["host"]!.GetValue<string>(), payload["host"]!.GetValue<string>());
                Assert.Equal(sent["reason"]!.GetValue<string>(), payload["reason"]!.GetValue<string>());
                var answer = await h.Channel.NextMessageAsync();
                Assert.Equal("unblock_request_result", Type(answer));
                Assert.Equal(sent["requestId"]!.GetValue<string>(), answer.GetProperty("requestId").GetString());
                Assert.True(answer.GetProperty("ok").GetBoolean());
            },
            ["self_check"] = _ =>
            {
                Assert.True(watch.SiteAccessLost);
                Assert.False(watch.InPrivateAllowed);
                return Task.CompletedTask;
            },
            ["tamper"] = async sent =>
            {
                var report = await h.Hub.NextReportAsync();
                Assert.Equal((sessionId, "TamperDetected"), (report.SessionId, report.Kind));
                Assert.Equal(sent["kind"]!.GetValue<string>(), JsonNode.Parse(report.PayloadJson)!["kind"]!.GetValue<string>());
            },
        };
        Assert.Equal(FromExtension.Select(m => m.Key).Order(StringComparer.Ordinal), checks.Keys.Order(StringComparer.Ordinal));

        foreach (var (type, check) in checks)
        {
            h.Hub.Reports.Clear();
            h.Channel.ClearSent();
            // Booleans false, so a self-check that is read shows; a tamper of the
            // one kind that still reaches the teacher.
            var sent = FromTheExtension(type, sessionId, allSites: false, inPrivate: false);
            h.Channel.Receive(sent);
            await check(sent);
        }
    }

    /// <summary>
    /// A message as the extension sends it per the contract, about
    /// <paramref name="sessionId"/>, every field set.
    /// </summary>
    private static JsonObject FromTheExtension(string type, Guid sessionId, bool allSites = true, bool inPrivate = true)
    {
        var shape = FromExtension.Single(m => m.Key == type).Value!;
        var message = ContractFiles.Sample(shape, name => name switch
        {
            "sessionId" => JsonValue.Create(sessionId.ToString()),
            "url" => JsonValue.Create("https://blocked.example/page?q=1"),
            "host" => JsonValue.Create("blocked.example"),
            "reason" => JsonValue.Create("For the assignment."),
            "kind" => JsonValue.Create(TamperKinds.HostPermissionRevoked),
            "allSites" => JsonValue.Create(allSites),
            "inPrivate" => JsonValue.Create(inPrivate),
            _ => null,
        }).AsObject();
        message.Insert(0, "type", type);
        return message;
    }

    private static ExtensionSelfCheckWatch SelfCheckWatch(Harness h) =>
        new(h.Channel, h.Coordinator, new NoTamperReports(), Options.Create(new SessionSettings()), h.Clock);

    private static List<string> Types(Harness h) => h.Channel.Messages().Select(m => Type(m) ?? "").ToList();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    private static string WitnessReport(string headline, List<string> problems) =>
        $"""
        {headline} contracts/witness.json (#573):
          {string.Join(Environment.NewLine + "  ", problems)}

        If the change is intended, change the file to match. That runs the extension's unit tests (Extension CI),
        which hold the extension to it, and the extensions already in the field have to survive it too.
        """;

    private sealed class NoTamperReports : ITamperReporter
    {
        public Task ReportAsync(Guid sessionId, string kind, CancellationToken ct = default) => Task.CompletedTask;
    }
}
