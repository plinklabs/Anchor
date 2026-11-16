using System.Text.Json;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Extension;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using FocusAgent.Core.Tamper;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FocusAgent.Core.Tests;

/// <summary>
/// The agent as the extension's only identity (#489): what the agent hands the
/// extension over the witness link, and what it reports for it.
/// </summary>
public class ExtensionSessionRelayTests
{
    private static readonly AllowedDomainDto Office = new("Suffix", "office.com");
    private static readonly AllowedDomainDto Wiki = new("Exact", "wikipedia.org");

    [Fact]
    public async Task Joining_a_session_hands_it_to_the_connected_extension_with_its_domains()
    {
        var h = new Harness();
        h.Channel.RaiseConnected();
        h.Channel.ClearSent();

        var sessionId = await h.JoinAsync(Office);

        var message = Assert.Single(h.Channel.Messages());
        Assert.Equal("session_started", Type(message));
        Assert.Equal(sessionId, message.GetProperty("sessionId").GetGuid());
        Assert.Equal("ABC123", message.GetProperty("joinCode").GetString());
        var domain = Assert.Single(message.GetProperty("domains").EnumerateArray());
        Assert.Equal("Suffix", domain.GetProperty("matchType").GetString());
        Assert.Equal("office.com", domain.GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_declined_session_never_reaches_the_extension()
    {
        var h = new Harness(decision: JoinDecision.Declined);
        h.Channel.RaiseConnected();
        h.Channel.ClearSent();

        await h.JoinAsync(Office);

        Assert.Empty(h.Channel.Messages());
        Assert.Null(h.Relay.SessionId);
    }

    [Fact]
    public async Task Every_reconnect_hands_the_extension_the_current_session_or_no_session()
    {
        var h = new Harness();

        // No session: an extension that missed an end while its link was down
        // must stop enforcing it.
        h.Channel.RaiseConnected();
        Assert.Equal("no_session", Type(Assert.Single(h.Channel.Messages())));

        var sessionId = await h.JoinAsync(Office);
        h.Hub.RaiseBundlesUpdated(new SessionBundlesUpdatedPayload(sessionId, [], [Wiki]));
        h.Hub.RaiseAllowlistAmended(new AllowlistAmendedPayload(sessionId, Guid.NewGuid(), [Office]));
        h.Channel.RaiseDisconnected();
        h.Channel.ClearSent();

        // A revived service worker, Edge restarted, the extension back on: it gets
        // the session as it stands now, grants and bundle changes included.
        h.Channel.RaiseConnected();

        var snapshot = Assert.Single(h.Channel.Messages());
        Assert.Equal("session_started", Type(snapshot));
        Assert.Equal(sessionId, snapshot.GetProperty("sessionId").GetGuid());
        Assert.Equal(
            new[] { "wikipedia.org", "office.com" },
            snapshot.GetProperty("domains").EnumerateArray().Select(d => d.GetProperty("value").GetString()));
    }

    [Fact]
    public async Task A_session_joined_while_no_extension_is_connected_is_handed_down_when_one_connects()
    {
        var h = new Harness();

        var sessionId = await h.JoinAsync(Office);
        Assert.Empty(h.Channel.Messages());

        h.Channel.RaiseConnected();

        var message = Assert.Single(h.Channel.Messages());
        Assert.Equal("session_started", Type(message));
        Assert.Equal(sessionId, message.GetProperty("sessionId").GetGuid());
    }

    [Fact]
    public async Task Bundle_changes_and_grants_for_the_joined_session_are_handed_down()
    {
        var h = new Harness();
        h.Channel.RaiseConnected();
        var sessionId = await h.JoinAsync(Office);
        h.Channel.ClearSent();

        h.Hub.RaiseBundlesUpdated(new SessionBundlesUpdatedPayload(sessionId, [], [Wiki]));
        h.Hub.RaiseAllowlistAmended(new AllowlistAmendedPayload(sessionId, Guid.NewGuid(), [Office]));

        var messages = h.Channel.Messages();
        Assert.Equal(new[] { "allowlist_replaced", "allowlist_amended" }, messages.Select(Type));
        Assert.Equal("wikipedia.org", Assert.Single(messages[0].GetProperty("domains").EnumerateArray()).GetProperty("value").GetString());
        Assert.Equal("office.com", Assert.Single(messages[1].GetProperty("addedDomains").EnumerateArray()).GetProperty("value").GetString());
        Assert.Equal(new[] { Wiki, Office }, h.Relay.Domains);
    }

    [Fact]
    public async Task Changes_for_another_session_are_not_handed_down()
    {
        var h = new Harness();
        h.Channel.RaiseConnected();
        await h.JoinAsync(Office);
        h.Channel.ClearSent();

        var other = Guid.NewGuid();
        h.Hub.RaiseBundlesUpdated(new SessionBundlesUpdatedPayload(other, [], [Wiki]));
        h.Hub.RaiseAllowlistAmended(new AllowlistAmendedPayload(other, Guid.NewGuid(), [Wiki]));

        Assert.Empty(h.Channel.Messages());
        Assert.Equal(new[] { Office }, h.Relay.Domains);
    }

    [Fact]
    public async Task The_end_of_the_session_is_handed_down()
    {
        var h = new Harness();
        h.Channel.RaiseConnected();
        var sessionId = await h.JoinAsync(Office);
        h.Channel.ClearSent();

        h.Hub.RaiseSessionEnded(sessionId);

        var message = Assert.Single(h.Channel.Messages());
        Assert.Equal("session_ended", Type(message));
        Assert.Equal(sessionId, message.GetProperty("sessionId").GetGuid());
        Assert.Null(h.Relay.SessionId);
    }

    [Fact]
    public async Task Leaving_the_session_hands_its_end_down_too()
    {
        // #462: a student who leaves is out of the session; the extension stops
        // enforcing it at once, not when the teacher ends it.
        var h = new Harness();
        h.Channel.RaiseConnected();
        var sessionId = await h.JoinAsync(Office);
        h.Channel.ClearSent();

        await h.Coordinator.LeaveSessionManuallyAsync();

        var message = Assert.Single(h.Channel.Messages());
        Assert.Equal("session_ended", Type(message));
        Assert.Equal(sessionId, message.GetProperty("sessionId").GetGuid());
    }

    [Fact]
    public async Task A_blocked_url_from_the_extension_is_reported_as_a_BlockedUrl_event()
    {
        var h = new Harness();
        h.Channel.RaiseConnected();
        var sessionId = await h.JoinAsync(Office);

        h.Channel.Receive(new
        {
            type = "blocked_url",
            sessionId,
            url = "https://games.example/play",
            host = "games.example",
            tabId = 7,
            occurredAt = "2026-11-11T09:30:00.000Z",
        });

        var report = await h.Hub.NextReportAsync();
        Assert.Equal(sessionId, report.SessionId);
        Assert.Equal("BlockedUrl", report.Kind);
        Assert.Equal(DateTimeOffset.Parse("2026-11-11T09:30:00Z"), report.OccurredAt);
        using var payload = JsonDocument.Parse(report.PayloadJson);
        Assert.Equal("https://games.example/play", payload.RootElement.GetProperty("url").GetString());
        Assert.Equal("games.example", payload.RootElement.GetProperty("host").GetString());
        Assert.Equal(7, payload.RootElement.GetProperty("tabId").GetInt32());
    }

    [Fact]
    public async Task An_unblock_request_is_reported_and_answered()
    {
        var h = new Harness();
        h.Channel.RaiseConnected();
        var sessionId = await h.JoinAsync(Office);
        h.Channel.ClearSent();

        h.Channel.Receive(new
        {
            type = "unblock_request",
            requestId = "r1",
            sessionId,
            url = "https://docs.example/a",
            host = "docs.example",
        });

        var report = await h.Hub.NextReportAsync();
        Assert.Equal("UnblockRequest", report.Kind);
        using (var payload = JsonDocument.Parse(report.PayloadJson))
        {
            Assert.Equal("docs.example", payload.RootElement.GetProperty("host").GetString());
            Assert.Equal("https://docs.example/a", payload.RootElement.GetProperty("url").GetString());
        }
        var answer = await h.Channel.NextMessageAsync();
        Assert.Equal("unblock_request_result", Type(answer));
        Assert.Equal("r1", answer.GetProperty("requestId").GetString());
        Assert.True(answer.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task An_unblock_request_the_backend_rejects_is_answered_with_the_error()
    {
        var h = new Harness();
        h.Channel.RaiseConnected();
        var sessionId = await h.JoinAsync(Office);
        h.Channel.ClearSent();
        h.Hub.ReportThrows = new InvalidOperationException("hub down");

        h.Channel.Receive(new { type = "unblock_request", requestId = "r2", sessionId, url = "https://docs.example/a" });

        var answer = await h.Channel.NextMessageAsync();
        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Equal("hub down", answer.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_unblock_request_for_a_session_the_student_is_not_in_is_refused_without_a_report()
    {
        var h = new Harness();
        h.Channel.RaiseConnected();
        await h.JoinAsync(Office);
        h.Channel.ClearSent();

        h.Channel.Receive(new { type = "unblock_request", requestId = "r3", sessionId = Guid.NewGuid(), url = "https://docs.example/a" });

        var answer = await h.Channel.NextMessageAsync();
        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Empty(h.Hub.Reports);
    }

    [Fact]
    public async Task The_extensions_tamper_signals_are_reported_and_unknown_kinds_are_dropped()
    {
        var h = new Harness();
        h.Channel.RaiseConnected();
        var sessionId = await h.JoinAsync(Office);

        h.Channel.Receive(new { type = "tamper", sessionId, kind = "made_up" });
        h.Channel.Receive(new { type = "tamper", sessionId, kind = "inprivate_opened" });

        var report = await h.Hub.NextReportAsync();
        Assert.Equal("TamperDetected", report.Kind);
        Assert.Equal("{\"kind\":\"inprivate_opened\"}", report.PayloadJson);
        Assert.Single(h.Hub.Reports);
    }

    [Fact]
    public async Task Reports_outside_a_session_or_for_another_session_are_dropped()
    {
        var h = new Harness();
        h.Channel.RaiseConnected();

        h.Channel.Receive(new { type = "blocked_url", sessionId = Guid.NewGuid(), url = "https://a.example/" });
        await h.JoinAsync(Office);
        h.Channel.Receive(new { type = "blocked_url", sessionId = Guid.NewGuid(), url = "https://a.example/" });
        h.Channel.ReceiveLine("not json");
        h.Channel.ReceiveLine("{\"type\":\"ping\"}");

        await Task.Delay(50);
        Assert.Empty(h.Hub.Reports);
    }

    [Fact]
    public async Task The_agent_pings_for_the_extension_only_while_in_a_session_with_the_link_up()
    {
        var h = new Harness();
        var interval = TimeSpan.FromSeconds(10);

        h.Channel.RaiseConnected();
        h.Clock.Advance(interval * 2);
        Assert.Empty(h.Hub.ExtensionPings);

        var sessionId = await h.JoinAsync(Office);
        h.Clock.Advance(interval);
        Assert.Equal(new[] { sessionId }, h.Hub.ExtensionPings);

        // The extension is off: no ping, so the backend's extension_silent can fire.
        h.Channel.RaiseDisconnected();
        h.Clock.Advance(interval * 3);
        Assert.Single(h.Hub.ExtensionPings);

        h.Channel.RaiseConnected();
        h.Clock.Advance(interval);
        Assert.Equal(2, h.Hub.ExtensionPings.Count);

        h.Hub.RaiseSessionEnded(sessionId);
        h.Clock.Advance(interval * 3);
        Assert.Equal(2, h.Hub.ExtensionPings.Count);
    }

    private static string? Type(JsonElement message) => message.GetProperty("type").GetString();

    private sealed class Harness
    {
        public FakeHub Hub { get; } = new();
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public FakeChannel Channel { get; } = new();
        public SessionCoordinator Coordinator { get; }
        public ExtensionSessionRelay Relay { get; }

        public Harness(JoinDecision decision = JoinDecision.Confirmed)
        {
            Coordinator = new SessionCoordinator(
                Hub,
                new AnsweringUi(decision),
                Options.Create(new RealtimeSettings { JoinConfirmationDuration = TimeSpan.FromSeconds(5) }),
                Clock);
            Relay = new ExtensionSessionRelay(
                Channel,
                Coordinator,
                Hub,
                Options.Create(new SessionSettings { HeartbeatIntervalSeconds = 10 }),
                Clock);
        }

        public async Task<Guid> JoinAsync(params AllowedDomainDto[] domains)
        {
            var payload = new SessionStartedPayload(
                Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch, "ABC123", [], domains);
            await Coordinator.HandleSessionStartedAsync(payload);
            return payload.SessionId;
        }
    }

    private sealed class AnsweringUi(JoinDecision decision) : ISessionUiHost
    {
        public Task<JoinDecision> ShowJoinConfirmationAsync(JoinConfirmation confirmation, CancellationToken ct = default)
            => Task.FromResult(decision);
        public void DismissJoinConfirmation() { }
    }

    private sealed class FakeChannel : IExtensionWitnessChannel
    {
        private readonly object _gate = new();
        private bool _connected;
        private int _read;

        private readonly List<string> _sent = new();
        public event EventHandler? WitnessConnected;
        public event EventHandler? WitnessDisconnected;
        public event EventHandler<string>? MessageReceived;

        public void RaiseConnected()
        {
            lock (_gate) _connected = true;
            WitnessConnected?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseDisconnected()
        {
            lock (_gate) _connected = false;
            WitnessDisconnected?.Invoke(this, EventArgs.Empty);
        }

        public void Receive(object message) => ReceiveLine(JsonSerializer.Serialize(message));
        public void ReceiveLine(string line) => MessageReceived?.Invoke(this, line);

        public bool TrySend(string line)
        {
            lock (_gate)
            {
                if (!_connected) return false;
                _sent.Add(line);
                return true;
            }
        }

        public List<JsonElement> Messages()
        {
            lock (_gate) return _sent.Select(Parse).ToList();
        }

        public void ClearSent()
        {
            lock (_gate)
            {
                _sent.Clear();
                _read = 0;
            }
        }

        /// <summary>The next message sent after the last one this returned (or after a Clear).</summary>
        public async Task<JsonElement> NextMessageAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_read < _sent.Count) return Parse(_sent[_read++]);
                }
                await Task.Delay(10);
            }
            throw new TimeoutException("No message was sent to the extension.");
        }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

        private static JsonElement Parse(string line)
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Clone();
        }
    }

    private sealed class FakeHub : ISessionHubConnection
    {
        private readonly object _gate = new();

        public AgentConnectionState State => AgentConnectionState.Connected;
#pragma warning disable CS0067 // Not raised by these tests.
        public event EventHandler<AgentConnectionState>? StateChanged;
        public event EventHandler<SessionStartedPayload>? SessionStarted;
#pragma warning restore CS0067
        public event EventHandler<Guid>? SessionEnded;
        public event EventHandler<SessionBundlesUpdatedPayload>? SessionBundlesUpdated;
        public event EventHandler<AllowlistAmendedPayload>? AllowlistAmended;

        public List<(Guid SessionId, string Kind, string PayloadJson, DateTimeOffset? OccurredAt)> Reports { get; } = new();
        public List<Guid> ExtensionPings { get; } = new();
        public Exception? ReportThrows { get; set; }

        public void RaiseSessionEnded(Guid sessionId) => SessionEnded?.Invoke(this, sessionId);
        public void RaiseBundlesUpdated(SessionBundlesUpdatedPayload payload) => SessionBundlesUpdated?.Invoke(this, payload);
        public void RaiseAllowlistAmended(AllowlistAmendedPayload payload) => AllowlistAmended?.Invoke(this, payload);

        public async Task<(Guid SessionId, string Kind, string PayloadJson, DateTimeOffset? OccurredAt)> NextReportAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (Reports.Count > 0) return Reports[0];
                }
                await Task.Delay(10);
            }
            throw new TimeoutException("Nothing was reported.");
        }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task JoinSessionAsync(Guid sessionId, string? joinCode, CancellationToken ct = default) => Task.CompletedTask;
        public Task LeaveSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclineSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => Task.CompletedTask;

        public Task ReportEventAsync(Guid sessionId, string kind, string payloadJson, DateTimeOffset? occurredAt = null, CancellationToken ct = default)
        {
            // The agent's own ManualLeave on a leave is not the extension's report.
            if (kind == "ManualLeave") return Task.CompletedTask;
            if (ReportThrows is not null) return Task.FromException(ReportThrows);
            lock (_gate) Reports.Add((sessionId, kind, payloadJson, occurredAt));
            return Task.CompletedTask;
        }

        public Task<bool> HeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> ExtensionHeartbeatAsync(Guid sessionId, CancellationToken ct = default)
        {
            lock (_gate) ExtensionPings.Add(sessionId);
            return Task.FromResult(true);
        }

        public Task<bool> IsInSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<SessionStartedPayload?> GetStartedSessionAsync(CancellationToken ct = default) =>
            Task.FromResult<SessionStartedPayload?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
