using System.Text.Json;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using FocusAgent.Core.Tamper;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FocusAgent.Core.Tests;

/// <summary>
/// What the extension says about its own settings (#524): which checks count
/// (the joined session's, from extensions still connected), when the teacher is
/// told that site access was taken away (only when the student doesn't put it
/// back in time, once per outage), and who is asked to open the settings page.
/// </summary>
public class ExtensionSelfCheckWatchTests
{
    private static readonly TimeSpan ReportAfter = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_check_without_site_access_in_the_joined_session_is_lost_until_one_passes()
    {
        var h = new Harness();
        var session = await h.JoinAsync();

        h.Channel.Check(session, allSites: false);
        Assert.True(h.Watch.SiteAccessLost);
        Assert.Equal(new[] { true }, h.Changes);

        h.Channel.Check(session, allSites: true);
        Assert.False(h.Watch.SiteAccessLost);
        Assert.Equal(new[] { true, false }, h.Changes);
    }

    [Fact]
    public async Task A_repeated_failing_check_raises_no_second_change()
    {
        var h = new Harness();
        var session = await h.JoinAsync();

        h.Channel.Check(session, allSites: false);
        h.Channel.Check(session, allSites: false, inPrivate: true);

        Assert.Equal(new[] { true }, h.Changes);
    }

    [Fact]
    public async Task A_check_about_another_session_or_outside_one_counts_for_nothing()
    {
        var h = new Harness();
        h.Channel.Check(Guid.NewGuid(), allSites: false);
        Assert.False(h.Watch.SiteAccessLost);

        await h.JoinAsync();
        h.Channel.Check(Guid.NewGuid(), allSites: false);

        Assert.False(h.Watch.SiteAccessLost);
        Assert.Empty(h.Changes);
    }

    [Fact]
    public async Task A_check_that_beats_the_join_counts_once_the_session_is_joined()
    {
        // The extension answers the session the agent hands it; should that answer
        // arrive before every subscriber has heard of the join, it still counts.
        var h = new Harness();
        var session = Guid.NewGuid();
        h.Channel.Check(session, allSites: false);
        Assert.False(h.Watch.SiteAccessLost);

        await h.JoinAsync(session);

        Assert.True(h.Watch.SiteAccessLost);
    }

    [Fact]
    public async Task A_malformed_check_is_no_check()
    {
        var h = new Harness();
        var session = await h.JoinAsync();

        h.Channel.Receive(new { type = "self_check", sessionId = session });
        h.Channel.Receive(new { type = "self_check", sessionId = session, allSites = "no" });
        h.Channel.Receive(new { type = "self_check", allSites = false });
        h.Channel.ReceiveLine("not json");

        Assert.False(h.Watch.SiteAccessLost);
        Assert.Null(h.Watch.InPrivateAllowed);
    }

    [Fact]
    public async Task The_extension_that_lost_site_access_closing_ends_the_outage()
    {
        // Its profile closed, or it restarted: whatever it says next comes with
        // the session handed to its new connection.
        var h = new Harness();
        var session = await h.JoinAsync();
        h.Channel.Check(session, allSites: false);

        h.Channel.RaiseDisconnected();

        Assert.False(h.Watch.SiteAccessLost);
        h.Clock.Advance(ReportAfter * 2);
        Assert.Empty(h.Reporter.Calls);
    }

    [Fact]
    public async Task With_two_profiles_one_without_site_access_is_enough_and_only_it_is_asked_to_open_its_page()
    {
        var h = new Harness();
        var first = new WitnessConnection(1);
        var second = new WitnessConnection(2);
        h.Channel.RaiseConnected(first);
        h.Channel.RaiseConnected(second);
        var session = await h.JoinAsync();

        h.Channel.Check(session, allSites: true, from: first);
        h.Channel.Check(session, allSites: false, from: second);
        Assert.True(h.Watch.SiteAccessLost);

        Assert.Equal(1, h.Watch.OpenExtensionPage());
        var asked = Assert.Single(h.Channel.Sent);
        Assert.Equal(second.Id, asked.To);
        using var line = JsonDocument.Parse(asked.Line);
        Assert.Equal("open_extension_page", line.RootElement.GetProperty("type").GetString());
        Assert.Equal(session, line.RootElement.GetProperty("sessionId").GetGuid());

        h.Channel.Check(session, allSites: true, from: second);
        Assert.False(h.Watch.SiteAccessLost);
        Assert.Equal(0, h.Watch.OpenExtensionPage());
    }

    [Fact]
    public async Task Site_access_not_put_back_in_time_is_reported_to_the_teacher_once()
    {
        var h = new Harness();
        var session = await h.JoinAsync();
        h.Channel.Check(session, allSites: false);

        h.Clock.Advance(ReportAfter - TimeSpan.FromSeconds(1));
        Assert.Empty(h.Reporter.Calls);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { (session, TamperKinds.HostPermissionRevoked) }, h.Reporter.Calls);

        // The same outage, checked again (a reconnect hands the session down
        // again), is not told twice.
        h.Channel.Check(session, allSites: false);
        h.Clock.Advance(ReportAfter * 3);
        Assert.Single(h.Reporter.Calls);
    }

    [Fact]
    public async Task Site_access_put_back_in_time_is_never_reported()
    {
        var h = new Harness();
        var session = await h.JoinAsync();
        h.Channel.Check(session, allSites: false);
        h.Clock.Advance(ReportAfter - TimeSpan.FromSeconds(5));

        h.Channel.Check(session, allSites: true);
        h.Clock.Advance(ReportAfter * 2);

        Assert.Empty(h.Reporter.Calls);
    }

    [Fact]
    public async Task A_second_outage_gets_its_own_time_and_its_own_report()
    {
        var h = new Harness();
        var session = await h.JoinAsync();
        h.Channel.Check(session, allSites: false);
        h.Clock.Advance(ReportAfter);
        h.Channel.Check(session, allSites: true);

        h.Channel.Check(session, allSites: false);
        h.Clock.Advance(ReportAfter - TimeSpan.FromSeconds(1));
        Assert.Single(h.Reporter.Calls);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, h.Reporter.Calls.Count);
    }

    [Fact]
    public async Task Leaving_the_session_ends_the_outage_without_a_report()
    {
        var h = new Harness();
        var session = await h.JoinAsync();
        h.Channel.Check(session, allSites: false);

        h.Hub.RaiseSessionEnded(session);

        Assert.False(h.Watch.SiteAccessLost);
        Assert.Equal(new[] { true, false }, h.Changes);
        h.Clock.Advance(ReportAfter * 2);
        Assert.Empty(h.Reporter.Calls);
        Assert.Equal(0, h.Watch.OpenExtensionPage());
    }

    [Fact]
    public async Task A_failed_report_leaves_the_outage_as_it_is()
    {
        var h = new Harness();
        h.Reporter.Throw = true;
        var session = await h.JoinAsync();
        h.Channel.Check(session, allSites: false);

        h.Clock.Advance(ReportAfter);

        Assert.Single(h.Reporter.Calls);
        Assert.True(h.Watch.SiteAccessLost);
    }

    [Fact]
    public async Task InPrivate_access_is_known_per_session_and_off_when_any_extension_says_so()
    {
        var h = new Harness();
        var first = new WitnessConnection(1);
        var second = new WitnessConnection(2);
        h.Channel.RaiseConnected(first);
        h.Channel.RaiseConnected(second);
        var session = await h.JoinAsync();
        Assert.Null(h.Watch.InPrivateAllowed);

        h.Channel.Check(session, allSites: true, inPrivate: true, from: first);
        Assert.True(h.Watch.InPrivateAllowed);

        h.Channel.Check(session, allSites: true, inPrivate: false, from: second);
        Assert.False(h.Watch.InPrivateAllowed);

        // It's off by default on every install, so it pauses nothing.
        Assert.False(h.Watch.SiteAccessLost);
        Assert.Empty(h.Changes);
        h.Clock.Advance(ReportAfter * 2);
        Assert.Empty(h.Reporter.Calls);
    }

    private sealed class Harness
    {
        public FakeHub Hub { get; } = new();
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public FakeChannel Channel { get; } = new();
        public RecordingReporter Reporter { get; } = new();
        public SessionCoordinator Coordinator { get; }
        public ExtensionSelfCheckWatch Watch { get; }
        public List<bool> Changes { get; } = new();

        public Harness()
        {
            Coordinator = new SessionCoordinator(
                Hub,
                new ConfirmingUi(),
                Options.Create(new RealtimeSettings { JoinConfirmationDuration = TimeSpan.FromSeconds(5) }),
                Clock);
            Watch = new ExtensionSelfCheckWatch(
                Channel,
                Coordinator,
                Reporter,
                Options.Create(new SessionSettings { ExtensionSiteAccessReportSeconds = (int)ReportAfter.TotalSeconds }),
                Clock);
            Watch.Changed += (_, lost) => Changes.Add(lost);
        }

        public async Task<Guid> JoinAsync(Guid? sessionId = null)
        {
            var payload = new SessionStartedPayload(
                SessionId: sessionId ?? Guid.NewGuid(),
                ClassId: Guid.NewGuid(),
                StartedAt: DateTimeOffset.UnixEpoch,
                JoinCode: "123456",
                Apps: new[] { new AllowedAppDto("ProcessName", "msedge") },
                Domains: Array.Empty<AllowedDomainDto>());
            await Coordinator.HandleSessionStartedAsync(payload);
            Assert.Equal(payload.SessionId, Coordinator.JoinedSessionId);
            return payload.SessionId;
        }
    }

    /// <summary>The witness link, as far as the watch uses it: one extension per connection.</summary>
    private sealed class FakeChannel : IExtensionWitnessChannel
    {
        // One Edge profile's extension, unless a test names another.
        private static readonly WitnessConnection Only = new(1);
        private readonly HashSet<long> _connected = new() { Only.Id };

        public event EventHandler<WitnessConnection>? WitnessConnected;
        public event EventHandler<WitnessConnection>? WitnessDisconnected;
        public event EventHandler<WitnessMessage>? MessageReceived;

        public List<(long To, string Line)> Sent { get; } = new();

        public void RaiseConnected(WitnessConnection connection)
        {
            _connected.Add(connection.Id);
            WitnessConnected?.Invoke(this, connection);
        }

        public void RaiseDisconnected(WitnessConnection? connection = null)
        {
            connection ??= Only;
            _connected.Remove(connection.Id);
            WitnessDisconnected?.Invoke(this, connection);
        }

        /// <summary>The extension's self-check, as background.ts sends it.</summary>
        public void Check(Guid sessionId, bool allSites, bool inPrivate = false, WitnessConnection? from = null) =>
            Receive(new { type = "self_check", sessionId, allSites, inPrivate }, from);

        public void Receive(object message, WitnessConnection? from = null) =>
            ReceiveLine(JsonSerializer.Serialize(message), from);

        public void ReceiveLine(string line, WitnessConnection? from = null) =>
            MessageReceived?.Invoke(this, new WitnessMessage(from ?? Only, line));

        public bool TrySend(WitnessConnection connection, string line)
        {
            if (!_connected.Contains(connection.Id)) return false;
            Sent.Add((connection.Id, line));
            return true;
        }

        public int Broadcast(string line)
        {
            foreach (var id in _connected)
                Sent.Add((id, line));
            return _connected.Count;
        }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingReporter : ITamperReporter
    {
        public List<(Guid SessionId, string Kind)> Calls { get; } = new();
        public bool Throw { get; set; }

        public Task ReportAsync(Guid sessionId, string kind, CancellationToken ct = default)
        {
            Calls.Add((sessionId, kind));
            if (Throw) throw new InvalidOperationException("simulated hub failure");
            return Task.CompletedTask;
        }
    }

    private sealed class ConfirmingUi : ISessionUiHost
    {
        public Task<JoinDecision> ShowJoinConfirmationAsync(JoinConfirmation confirmation, CancellationToken ct = default)
            => Task.FromResult(JoinDecision.Confirmed);
        public void DismissJoinConfirmation() { }
    }

    private sealed class FakeHub : ISessionHubConnection
    {
        public AgentConnectionState State => AgentConnectionState.Connected;
#pragma warning disable CS0067
        public event EventHandler<AgentConnectionState>? StateChanged;
        public event EventHandler<SessionStartedPayload>? SessionStarted;
        public event EventHandler<SessionBundlesUpdatedPayload>? SessionBundlesUpdated;
        public event EventHandler<AllowlistAmendedPayload>? AllowlistAmended;
        public event EventHandler<UnblockDeniedPayload>? UnblockDenied;
#pragma warning restore CS0067
        public event EventHandler<Guid>? SessionEnded;

        public void RaiseSessionEnded(Guid sessionId) => SessionEnded?.Invoke(this, sessionId);

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task JoinSessionAsync(Guid sessionId, string? joinCode, CancellationToken ct = default) => Task.CompletedTask;
        public Task LeaveSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclineSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReportEventAsync(Guid sessionId, string kind, string payloadJson, DateTimeOffset? occurredAt = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExtensionHeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> HeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> IsInSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<SessionStartedPayload?> GetStartedSessionAsync(CancellationToken ct = default) => Task.FromResult<SessionStartedPayload?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
