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
public partial class ExtensionSessionRelayTests
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
    public async Task A_denied_request_is_handed_down_with_its_host_and_leaves_the_allowlist_alone()
    {
        var h = new Harness();
        h.Channel.RaiseConnected();
        var sessionId = await h.JoinAsync(Office);
        h.Channel.ClearSent();

        h.Hub.RaiseUnblockDenied(new UnblockDeniedPayload(sessionId, Guid.NewGuid(), "games.example", DateTimeOffset.UtcNow));

        var message = Assert.Single(h.Channel.Messages());
        Assert.Equal("unblock_denied", Type(message));
        Assert.Equal(sessionId, message.GetProperty("sessionId").GetGuid());
        Assert.Equal("games.example", message.GetProperty("host").GetString());
        Assert.Equal(new[] { Office }, h.Relay.Domains);

        // Nothing to catch up on: an extension that connects later has no block
        // page still waiting on that request.
        h.Channel.ClearSent();
        h.Channel.RaiseConnected();
        Assert.Equal("session_started", Type(Assert.Single(h.Channel.Messages())));
    }

    [Fact]
    public async Task A_denial_for_another_session_is_not_handed_down()
    {
        var h = new Harness();
        h.Channel.RaiseConnected();
        await h.JoinAsync(Office);
        h.Channel.ClearSent();

        h.Hub.RaiseUnblockDenied(new UnblockDeniedPayload(Guid.NewGuid(), Guid.NewGuid(), "games.example", DateTimeOffset.UtcNow));

        Assert.Empty(h.Channel.Messages());
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

        // The extension reports the host only (#527).
        h.Channel.Receive(new
        {
            type = "blocked_url",
            sessionId,
            host = "games.example",
            tabId = 7,
            occurredAt = "2026-11-11T09:30:00.000Z",
        });

        var report = await h.Hub.NextReportAsync();
        Assert.Equal(sessionId, report.SessionId);
        Assert.Equal("BlockedUrl", report.Kind);
        Assert.Equal(DateTimeOffset.Parse("2026-11-11T09:30:00Z"), report.OccurredAt);
        using var payload = JsonDocument.Parse(report.PayloadJson);
        Assert.Equal("games.example", payload.RootElement.GetProperty("host").GetString());
        Assert.Equal(7, payload.RootElement.GetProperty("tabId").GetInt32());
        Assert.False(payload.RootElement.TryGetProperty("url", out _));
    }

    [Theory]
    [InlineData("games.example")]
    [InlineData("")]
    public async Task A_blocked_url_from_an_older_extension_is_reported_by_its_host_without_the_url(string sentHost)
    {
        // #527: an extension the store hasn't updated yet still sends the whole
        // URL, path and query string included. The agent sends the teacher the
        // host only, and works it out from the URL when the report has none.
        var h = new Harness();
        h.Channel.RaiseConnected();
        var sessionId = await h.JoinAsync(Office);

        h.Channel.Receive(new
        {
            type = "blocked_url",
            sessionId,
            url = "https://games.example/play?q=my+search",
            host = sentHost,
            tabId = 7,
            occurredAt = "2026-11-11T09:30:00.000Z",
        });

        var report = await h.Hub.NextReportAsync();
        Assert.Equal("BlockedUrl", report.Kind);
        Assert.DoesNotContain("play", report.PayloadJson);
        Assert.DoesNotContain("search", report.PayloadJson);
        using var payload = JsonDocument.Parse(report.PayloadJson);
        Assert.False(payload.RootElement.TryGetProperty("url", out _));
        Assert.Equal("games.example", payload.RootElement.GetProperty("host").GetString());
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
        // An extension older than #524 still reports its site access itself.
        h.Channel.Receive(new { type = "tamper", sessionId, kind = "host_permission_revoked" });

        var report = await h.Hub.NextReportAsync();
        Assert.Equal("TamperDetected", report.Kind);
        Assert.Equal("{\"kind\":\"host_permission_revoked\"}", report.PayloadJson);
        await Task.Delay(50);
        Assert.Single(h.Hub.Reports);
    }

    [Fact]
    public async Task The_extensions_own_inprivate_signal_goes_no_further()
    {
        // #535: the agent's own InPrivate witness reports an InPrivate window only
        // when the student leaves it open; the extension (where it is allowed in
        // InPrivate) says so the moment the window opens. Passing that on would
        // tell the teacher of a window closed in time, and twice of one left open.
        var h = new Harness();
        h.Channel.RaiseConnected();
        var sessionId = await h.JoinAsync(Office);

        h.Channel.Receive(new { type = "tamper", sessionId, kind = "inprivate_opened" });

        await Task.Delay(100);
        Assert.Empty(h.Hub.Reports);
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
    public async Task A_second_profile_connecting_mid_session_is_handed_the_session_and_the_first_hears_nothing_new()
    {
        // #517: the second Edge profile's extension used to get no session at all
        // and filtered nothing.
        var h = new Harness();
        var first = new WitnessConnection(1);
        var second = new WitnessConnection(2);
        h.Channel.RaiseConnected(first);
        var sessionId = await h.JoinAsync(Office);
        h.Channel.ClearSent();

        h.Channel.RaiseConnected(second);

        var snapshot = Assert.Single(h.Channel.Messages(second));
        Assert.Equal("session_started", Type(snapshot));
        Assert.Equal(sessionId, snapshot.GetProperty("sessionId").GetGuid());
        Assert.Equal("office.com", Assert.Single(snapshot.GetProperty("domains").EnumerateArray()).GetProperty("value").GetString());
        Assert.Empty(h.Channel.Messages(first));
    }

    [Fact]
    public async Task Each_profile_is_told_no_session_when_it_connects_outside_one()
    {
        var h = new Harness();
        var first = new WitnessConnection(1);
        var second = new WitnessConnection(2);

        h.Channel.RaiseConnected(first);
        h.Channel.RaiseConnected(second);

        Assert.Equal("no_session", Type(Assert.Single(h.Channel.Messages(first))));
        Assert.Equal("no_session", Type(Assert.Single(h.Channel.Messages(second))));
    }

    [Fact]
    public async Task The_session_its_allowlist_changes_grants_and_end_reach_every_connected_profile()
    {
        var h = new Harness();
        var first = new WitnessConnection(1);
        var second = new WitnessConnection(2);
        h.Channel.RaiseConnected(first);
        h.Channel.RaiseConnected(second);
        h.Channel.ClearSent();

        var sessionId = await h.JoinAsync(Office);
        h.Hub.RaiseBundlesUpdated(new SessionBundlesUpdatedPayload(sessionId, [], [Wiki]));
        h.Hub.RaiseAllowlistAmended(new AllowlistAmendedPayload(sessionId, Guid.NewGuid(), [Office]));
        h.Hub.RaiseSessionEnded(sessionId);

        var expected = new[] { "session_started", "allowlist_replaced", "allowlist_amended", "session_ended" };
        Assert.Equal(expected, h.Channel.Messages(first).Select(Type));
        Assert.Equal(expected, h.Channel.Messages(second).Select(Type));
    }

    [Fact]
    public async Task A_profile_that_closed_is_no_longer_sent_anything_while_the_other_still_is()
    {
        var h = new Harness();
        var first = new WitnessConnection(1);
        var second = new WitnessConnection(2);
        h.Channel.RaiseConnected(first);
        h.Channel.RaiseConnected(second);
        var sessionId = await h.JoinAsync(Office);
        h.Channel.RaiseDisconnected(first);
        h.Channel.ClearSent();

        h.Hub.RaiseSessionEnded(sessionId);

        Assert.Empty(h.Channel.Messages(first));
        Assert.Equal("session_ended", Type(Assert.Single(h.Channel.Messages(second))));
    }

    [Fact]
    public async Task An_unblock_request_is_answered_only_on_the_connection_that_asked()
    {
        var h = new Harness();
        var first = new WitnessConnection(1);
        var second = new WitnessConnection(2);
        h.Channel.RaiseConnected(first);
        h.Channel.RaiseConnected(second);
        var sessionId = await h.JoinAsync(Office);
        h.Channel.ClearSent();

        h.Channel.Receive(
            new { type = "unblock_request", requestId = "r1", sessionId, url = "https://docs.example/a" },
            from: second);

        var answer = await h.Channel.NextMessageAsync();
        Assert.Equal("unblock_request_result", Type(answer));
        Assert.Equal("r1", Assert.Single(h.Channel.Messages(second)).GetProperty("requestId").GetString());
        Assert.Empty(h.Channel.Messages(first));
    }

    // ---- a restarted agent, before it has asked the backend (#571) -----------

    [Fact]
    public void An_extension_that_connects_before_the_agent_has_asked_the_backend_is_sent_nothing()
    {
        // The agent was ended mid-session and started again, with the network
        // down: it holds no session yet, but that isn't "the session is over".
        var h = new Harness(askedBackend: false);

        h.Channel.RaiseConnected();

        Assert.Empty(h.Channel.Messages());
        Assert.Null(h.Relay.SessionId);
    }

    [Fact]
    public async Task Once_the_agent_has_asked_and_the_student_is_in_no_session_the_extension_is_told_so()
    {
        var h = new Harness(askedBackend: false);
        h.Channel.RaiseConnected();

        await h.HubConnectedAsync();

        Assert.Equal("no_session", Type(Assert.Single(h.Channel.Messages())));
    }

    [Fact]
    public async Task Once_the_agent_has_rejoined_the_session_the_extension_is_handed_it_once()
    {
        var h = new Harness(askedBackend: false);
        var session = new SessionStartedPayload(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch, "ABC123", [], [Office]);
        h.Backend.Sessions.Add(session);
        h.Channel.RaiseConnected();

        await h.HubConnectedAsync();

        var message = Assert.Single(h.Channel.Messages());
        Assert.Equal("session_started", Type(message));
        Assert.Equal(session.SessionId, message.GetProperty("sessionId").GetGuid());
        Assert.Equal(session.SessionId, h.Relay.SessionId);
    }

    [Fact]
    public async Task A_pass_that_could_not_reach_the_backend_still_sends_nothing_and_the_next_one_tells()
    {
        var h = new Harness(askedBackend: false);
        h.Channel.RaiseConnected();

        // The hub came up, the network dropped before /sessions/rejoinable.
        h.Backend.Throws = new HttpRequestException("No route to host.");
        await h.HubConnectedAsync();
        Assert.Empty(h.Channel.Messages());

        h.Backend.Throws = null;
        await h.HubConnectedAsync();
        Assert.Equal("no_session", Type(Assert.Single(h.Channel.Messages())));

        // Asked once is known for good: a later connect hears at once.
        h.Channel.RaiseDisconnected();
        h.Channel.ClearSent();
        h.Channel.RaiseConnected();
        Assert.Equal("no_session", Type(Assert.Single(h.Channel.Messages())));
    }

    [Fact]
    public async Task Every_profile_waiting_on_the_agent_is_told_and_one_that_closed_meanwhile_is_not()
    {
        var h = new Harness(askedBackend: false);
        var first = new WitnessConnection(1);
        var second = new WitnessConnection(2);
        var closed = new WitnessConnection(3);
        h.Channel.RaiseConnected(first);
        h.Channel.RaiseConnected(second);
        h.Channel.RaiseConnected(closed);
        h.Channel.RaiseDisconnected(closed);

        await h.HubConnectedAsync();

        Assert.Equal("no_session", Type(Assert.Single(h.Channel.Messages(first))));
        Assert.Equal("no_session", Type(Assert.Single(h.Channel.Messages(second))));
        Assert.Empty(h.Channel.Messages(closed));
    }

    [Fact]
    public async Task A_session_joined_before_the_agent_has_asked_is_handed_down_as_it_comes_and_not_again()
    {
        var h = new Harness(askedBackend: false);
        h.Channel.RaiseConnected();

        // A session the agent hears of before its first pass finishes (a
        // SessionStarted broadcast, a join by code).
        var sessionId = await h.JoinAsync(Office);
        var started = Assert.Single(h.Channel.Messages());
        Assert.Equal("session_started", Type(started));
        Assert.Equal(sessionId, started.GetProperty("sessionId").GetGuid());

        await h.HubConnectedAsync();

        Assert.Single(h.Channel.Messages());
    }

    private static string? Type(JsonElement message) => message.GetProperty("type").GetString();

    private sealed class Harness
    {
        public FakeHub Hub { get; } = new();
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public FakeChannel Channel { get; } = new();
        public RejoinableSessions Backend { get; } = new();
        public SessionCoordinator Coordinator { get; }
        public SessionRehydrationService Rehydration { get; }
        public ExtensionSessionRelay Relay { get; }
        public RecordingLogger<ExtensionSessionRelay> Log { get; } = new();

        /// <param name="askedBackend">
        /// Whether the agent has been through its first pass against the backend
        /// (#571) before the test starts; most tests are about an agent that has.
        /// </param>
        public Harness(JoinDecision decision = JoinDecision.Confirmed, bool askedBackend = true)
        {
            Coordinator = new SessionCoordinator(
                Hub,
                new AnsweringUi(decision),
                Options.Create(new RealtimeSettings { JoinConfirmationDuration = TimeSpan.FromSeconds(5) }),
                Clock);
            Rehydration = new SessionRehydrationService(Backend, Coordinator);
            Relay = new ExtensionSessionRelay(
                Channel,
                Coordinator,
                Rehydration,
                Hub,
                Clock,
                Log);
            if (askedBackend)
                Rehydration.NotifyConnectedAsync().GetAwaiter().GetResult();
        }

        /// <summary>The hub connecting: the agent's pass against the backend.</summary>
        public Task HubConnectedAsync() => Rehydration.NotifyConnectedAsync();

        public async Task<Guid> JoinAsync(params AllowedDomainDto[] domains)
        {
            var payload = new SessionStartedPayload(
                Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch, "ABC123", [], domains);
            await Coordinator.HandleSessionStartedAsync(payload);
            return payload.SessionId;
        }
    }

    /// <summary>The backend's <c>GET /sessions/rejoinable</c>, as the restarted agent asks it.</summary>
    private sealed class RejoinableSessions : ISessionRehydrationClient
    {
        public List<SessionStartedPayload> Sessions { get; } = new();
        public Exception? Throws { get; set; }

        public Task<IReadOnlyList<SessionStartedPayload>> GetRejoinableSessionsAsync(CancellationToken ct = default) =>
            Throws is not null
                ? Task.FromException<IReadOnlyList<SessionStartedPayload>>(Throws)
                : Task.FromResult<IReadOnlyList<SessionStartedPayload>>(Sessions.ToArray());
    }

    private sealed class AnsweringUi(JoinDecision decision) : ISessionUiHost
    {
        public Task<JoinDecision> ShowJoinConfirmationAsync(JoinConfirmation confirmation, CancellationToken ct = default)
            => Task.FromResult(decision);
        public void DismissJoinConfirmation() { }
    }

    private sealed class FakeChannel : IExtensionWitnessChannel
    {
        // One Edge profile's extension, unless a test names another.
        private static readonly WitnessConnection Only = new(1);

        private readonly object _gate = new();
        private readonly List<WitnessConnection> _connected = new();
        private int _read;

        // Everything sent, in order, with the connection it went to.
        private readonly List<(long To, string Line)> _sent = new();
        public event EventHandler<WitnessConnection>? WitnessConnected;
        public event EventHandler<WitnessConnection>? WitnessDisconnected;
        public event EventHandler<WitnessMessage>? MessageReceived;

        public void RaiseConnected(WitnessConnection? connection = null)
        {
            connection ??= Only;
            lock (_gate) _connected.Add(connection);
            WitnessConnected?.Invoke(this, connection);
        }

        public void RaiseDisconnected(WitnessConnection? connection = null)
        {
            connection ??= Only;
            lock (_gate) _connected.Remove(connection);
            WitnessDisconnected?.Invoke(this, connection);
        }

        public void Receive(object message, WitnessConnection? from = null) =>
            ReceiveLine(JsonSerializer.Serialize(message), from);

        public void ReceiveLine(string line, WitnessConnection? from = null) =>
            MessageReceived?.Invoke(this, new WitnessMessage(from ?? Only, line));

        public bool TrySend(WitnessConnection connection, string line)
        {
            lock (_gate)
            {
                if (!_connected.Contains(connection)) return false;
                _sent.Add((connection.Id, line));
                return true;
            }
        }

        public int Broadcast(string line)
        {
            lock (_gate)
            {
                foreach (var connection in _connected)
                    _sent.Add((connection.Id, line));
                return _connected.Count;
            }
        }

        /// <summary>Everything sent, to whichever connection.</summary>
        public List<JsonElement> Messages()
        {
            lock (_gate) return _sent.Select(s => Parse(s.Line)).ToList();
        }

        /// <summary>What was sent to one connection.</summary>
        public List<JsonElement> Messages(WitnessConnection to)
        {
            lock (_gate) return _sent.Where(s => s.To == to.Id).Select(s => Parse(s.Line)).ToList();
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
                    if (_read < _sent.Count) return Parse(_sent[_read++].Line);
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
        public event EventHandler<UnblockDeniedPayload>? UnblockDenied;

        public List<(Guid SessionId, string Kind, string PayloadJson, DateTimeOffset? OccurredAt)> Reports { get; } = new();
        public Exception? ReportThrows { get; set; }

        public void RaiseSessionEnded(Guid sessionId) => SessionEnded?.Invoke(this, sessionId);
        public void RaiseBundlesUpdated(SessionBundlesUpdatedPayload payload) => SessionBundlesUpdated?.Invoke(this, payload);
        public void RaiseAllowlistAmended(AllowlistAmendedPayload payload) => AllowlistAmended?.Invoke(this, payload);
        public void RaiseUnblockDenied(UnblockDeniedPayload payload) => UnblockDenied?.Invoke(this, payload);

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

        public Task<bool> IsInSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<SessionStartedPayload?> GetStartedSessionAsync(CancellationToken ct = default) =>
            Task.FromResult<SessionStartedPayload?>(null);
        public Task<SessionAllowlistPayload?> GetSessionAllowlistAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult<SessionAllowlistPayload?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
