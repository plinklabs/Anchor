using FocusAgent.Core.Dtos;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FocusAgent.Core.Tests;

public class SessionCoordinatorTests
{
    private static SessionStartedPayload Payload(Guid? sessionId = null) =>
        new(SessionId: sessionId ?? Guid.NewGuid(),
            ClassId: Guid.NewGuid(),
            StartedAt: DateTimeOffset.UnixEpoch,
            JoinCode: "123456",
            Apps: Array.Empty<AllowedAppDto>(),
            Domains: Array.Empty<AllowedDomainDto>());

    [Fact]
    public async Task Confirmed_decision_calls_JoinSession_with_null_join_code()
    {
        var hub = new FakeHub();
        var ui = new FakeUi { NextDecision = JoinDecision.Confirmed };
        var coordinator = NewCoordinator(hub, ui);

        var payload = Payload();
        await coordinator.HandleSessionStartedAsync(payload);

        Assert.Single(hub.JoinCalls);
        Assert.Equal(payload.SessionId, hub.JoinCalls[0].SessionId);
        Assert.Null(hub.JoinCalls[0].JoinCode);
        Assert.Empty(hub.LeaveCalls);
        Assert.Single(ui.Shown);
        Assert.Equal(payload, ui.Shown[0].Payload);
    }

    [Fact]
    public async Task Declined_decision_calls_DeclineSession_with_user_cancelled_reason()
    {
        var hub = new FakeHub();
        var ui = new FakeUi { NextDecision = JoinDecision.Declined };
        var coordinator = NewCoordinator(hub, ui);

        var payload = Payload();
        await coordinator.HandleSessionStartedAsync(payload);

        Assert.Empty(hub.JoinCalls);
        Assert.Empty(hub.LeaveCalls);
        Assert.Single(hub.DeclineCalls);
        Assert.Equal(payload.SessionId, hub.DeclineCalls[0].SessionId);
        Assert.Equal("user_cancelled", hub.DeclineCalls[0].Reason);
    }

    [Fact]
    public async Task Aborted_decision_calls_no_hub_methods()
    {
        var hub = new FakeHub();
        var ui = new FakeUi { NextDecision = JoinDecision.Aborted };
        var coordinator = NewCoordinator(hub, ui);

        await coordinator.HandleSessionStartedAsync(Payload());

        Assert.Empty(hub.JoinCalls);
        Assert.Empty(hub.LeaveCalls);
    }

    [Fact]
    public async Task SessionEnded_for_matching_session_aborts_active_confirmation()
    {
        var hub = new FakeHub();
        var ui = new FakeUi { ManualResolve = true };
        var coordinator = NewCoordinator(hub, ui);

        var payload = Payload();
        var task = coordinator.HandleSessionStartedAsync(payload);
        await ui.WaitForShownAsync();

        coordinator.HandleSessionEnded(payload.SessionId);

        await task;
        Assert.True(ui.Dismissed);
        Assert.Empty(hub.JoinCalls);
        Assert.Empty(hub.LeaveCalls);
        Assert.Equal(JoinDecision.Aborted, ui.Shown[0].Decision);
        Assert.Null(coordinator.ActiveSessionId);
    }

    [Fact]
    public async Task SessionEnded_for_unrelated_session_leaves_active_alone()
    {
        var hub = new FakeHub();
        var ui = new FakeUi { ManualResolve = true };
        var coordinator = NewCoordinator(hub, ui);

        var payload = Payload();
        var task = coordinator.HandleSessionStartedAsync(payload);
        await ui.WaitForShownAsync();

        coordinator.HandleSessionEnded(Guid.NewGuid());

        Assert.False(ui.Dismissed);
        ui.Resolve(JoinDecision.Confirmed);
        await task;
        Assert.Single(hub.JoinCalls);
    }

    [Fact]
    public async Task SessionJoined_fires_after_confirmation_and_JoinSession_succeeds()
    {
        var hub = new FakeHub();
        var ui = new FakeUi { NextDecision = JoinDecision.Confirmed };
        var coordinator = NewCoordinator(hub, ui);

        SessionStartedPayload? joined = null;
        coordinator.SessionJoined += (_, p) => joined = p;
        Guid? left = null;
        coordinator.SessionLeft += (_, id) => left = id;

        var payload = Payload();
        await coordinator.HandleSessionStartedAsync(payload);

        Assert.Equal(payload, joined);
        Assert.Equal(payload.SessionId, coordinator.JoinedSessionId);
        Assert.Null(left);

        coordinator.HandleSessionEnded(payload.SessionId);

        Assert.Equal(payload.SessionId, left);
        Assert.Null(coordinator.JoinedSessionId);
    }

    [Fact]
    public async Task RejoinAsync_invokes_hub_JoinSession_with_no_join_code_and_fires_SessionJoined()
    {
        var hub = new FakeHub();
        var ui = new FakeUi();
        var coordinator = NewCoordinator(hub, ui);

        SessionStartedPayload? joined = null;
        coordinator.SessionJoined += (_, p) => joined = p;

        var payload = Payload();
        await coordinator.RejoinAsync(payload);

        // Rehydration must not go through the toast UI.
        Assert.Empty(ui.Shown);

        Assert.Single(hub.JoinCalls);
        Assert.Equal(payload.SessionId, hub.JoinCalls[0].SessionId);
        Assert.Null(hub.JoinCalls[0].JoinCode);

        Assert.Equal(payload, joined);
        Assert.Equal(payload.SessionId, coordinator.JoinedSessionId);
    }

    [Fact]
    public async Task RejoinAsync_is_idempotent_when_session_already_joined()
    {
        var hub = new FakeHub();
        var ui = new FakeUi();
        var coordinator = NewCoordinator(hub, ui);

        var joinedFireCount = 0;
        coordinator.SessionJoined += (_, _) => joinedFireCount++;

        var payload = Payload();
        await coordinator.RejoinAsync(payload);
        await coordinator.RejoinAsync(payload);

        Assert.Single(hub.JoinCalls);
        Assert.Equal(1, joinedFireCount);
    }

    [Fact]
    public async Task SessionStarted_for_already_joined_session_skips_toast_and_re_join()
    {
        // Race scenario from #54: rehydration just rejoined session X, then a
        // fresh SessionStarted broadcast for X arrives. The coordinator must
        // not show the toast or call JoinSession again.
        var hub = new FakeHub();
        var ui = new FakeUi { NextDecision = JoinDecision.Confirmed };
        var coordinator = NewCoordinator(hub, ui);

        var payload = Payload();
        await coordinator.RejoinAsync(payload);
        Assert.Single(hub.JoinCalls);
        Assert.Empty(ui.Shown);

        var joinedAgain = 0;
        coordinator.SessionJoined += (_, _) => joinedAgain++;

        await coordinator.HandleSessionStartedAsync(payload);

        Assert.Empty(ui.Shown);
        Assert.Single(hub.JoinCalls);
        Assert.Equal(0, joinedAgain);
    }

    [Fact]
    public async Task SessionJoined_does_not_fire_when_decline_or_abort()
    {
        var hub = new FakeHub();
        var ui = new FakeUi { NextDecision = JoinDecision.Declined };
        var coordinator = NewCoordinator(hub, ui);

        var joinedFired = false;
        coordinator.SessionJoined += (_, _) => joinedFired = true;

        await coordinator.HandleSessionStartedAsync(Payload());

        Assert.False(joinedFired);
        Assert.Null(coordinator.JoinedSessionId);
    }

    [Fact]
    public async Task SessionLeft_does_not_fire_for_session_that_was_never_joined()
    {
        var hub = new FakeHub();
        var ui = new FakeUi { NextDecision = JoinDecision.Declined };
        var coordinator = NewCoordinator(hub, ui);

        var leftFired = false;
        coordinator.SessionLeft += (_, _) => leftFired = true;

        var payload = Payload();
        await coordinator.HandleSessionStartedAsync(payload);
        coordinator.HandleSessionEnded(payload.SessionId);

        Assert.False(leftFired);
    }

    [Fact]
    public async Task LeaveSessionManually_reports_ManualLeave_then_leaves_and_ends_locally()
    {
        var hub = new FakeHub();
        var ui = new FakeUi { NextDecision = JoinDecision.Confirmed };
        var coordinator = NewCoordinator(hub, ui);

        Guid? left = null;
        coordinator.SessionLeft += (_, id) => left = id;

        var payload = Payload();
        await coordinator.HandleSessionStartedAsync(payload);

        await coordinator.LeaveSessionManuallyAsync();

        // The event must be reported, with the exact kind the backend parses,
        // and *before* LeaveSession — which sets LeftAt and would otherwise make
        // the backend reject the event.
        var report = Assert.Single(hub.ReportCalls);
        Assert.Equal(payload.SessionId, report.SessionId);
        Assert.Equal("ManualLeave", report.Kind);
        Assert.Equal(payload.SessionId, Assert.Single(hub.LeaveCalls));
        Assert.Equal(new[] { "report:ManualLeave", "leave" }, hub.Operations);

        // Ended locally: SessionLeft fired and the session state is cleared so
        // enforcement/heartbeat stop and join-by-code is re-enabled.
        Assert.Equal(payload.SessionId, left);
        Assert.Null(coordinator.JoinedSessionId);
        Assert.Null(coordinator.ActiveSessionId);
    }

    [Fact]
    public async Task LeaveSessionManually_when_not_in_a_session_is_a_no_op()
    {
        var hub = new FakeHub();
        var ui = new FakeUi();
        var coordinator = NewCoordinator(hub, ui);

        var leftFired = false;
        coordinator.SessionLeft += (_, _) => leftFired = true;

        await coordinator.LeaveSessionManuallyAsync();

        Assert.Empty(hub.ReportCalls);
        Assert.Empty(hub.LeaveCalls);
        Assert.False(leftFired);
    }

    [Fact]
    public async Task LeaveSessionManually_fires_SessionLeft_once_even_if_called_twice()
    {
        var hub = new FakeHub();
        var ui = new FakeUi { NextDecision = JoinDecision.Confirmed };
        var coordinator = NewCoordinator(hub, ui);

        var leftCount = 0;
        coordinator.SessionLeft += (_, _) => leftCount++;

        await coordinator.HandleSessionStartedAsync(Payload());

        await coordinator.LeaveSessionManuallyAsync();
        await coordinator.LeaveSessionManuallyAsync();

        Assert.Equal(1, leftCount);
        Assert.Single(hub.ReportCalls);
        Assert.Single(hub.LeaveCalls);
    }

    [Fact]
    public async Task ReportAgentKilled_when_joined_posts_AgentKilled_without_leaving()
    {
        var hub = new FakeHub();
        var ui = new FakeUi { NextDecision = JoinDecision.Confirmed };
        var coordinator = NewCoordinator(hub, ui);

        var leftFired = false;
        coordinator.SessionLeft += (_, _) => leftFired = true;

        var payload = Payload();
        await coordinator.HandleSessionStartedAsync(payload);

        await coordinator.ReportAgentKilledAsync();

        // Exactly one AgentKilled, for the joined session, with the kind the
        // backend parses to EventKind.AgentKilled.
        var report = Assert.Single(hub.ReportCalls);
        Assert.Equal(payload.SessionId, report.SessionId);
        Assert.Equal("AgentKilled", report.Kind);

        // Unlike a manual leave this does not call LeaveSession, fire SessionLeft,
        // or clear local state — the process is exiting; the backend marks the
        // participant left off the event itself.
        Assert.Empty(hub.LeaveCalls);
        Assert.False(leftFired);
        Assert.Equal(payload.SessionId, coordinator.JoinedSessionId);
    }

    [Fact]
    public async Task ReportAgentKilled_when_not_in_a_session_is_a_no_op()
    {
        var hub = new FakeHub();
        var ui = new FakeUi();
        var coordinator = NewCoordinator(hub, ui);

        await coordinator.ReportAgentKilledAsync();

        Assert.Empty(hub.ReportCalls);
    }

    [Fact]
    public async Task ReportAgentKilled_swallows_a_failed_post()
    {
        var hub = new FakeHub { ReportThrows = new InvalidOperationException("network down") };
        var ui = new FakeUi { NextDecision = JoinDecision.Confirmed };
        var coordinator = NewCoordinator(hub, ui);

        await coordinator.HandleSessionStartedAsync(Payload());

        // A flaky network must never block Quit: the post is attempted but its
        // failure must not surface as an exception to the caller.
        await coordinator.ReportAgentKilledAsync();

        Assert.Single(hub.ReportCalls);
    }

    [Fact]
    public async Task Payload_propagates_to_ui_with_placeholder_teacher_name()
    {
        var hub = new FakeHub();
        var ui = new FakeUi { NextDecision = JoinDecision.Confirmed };
        var coordinator = NewCoordinator(hub, ui);

        var payload = Payload();
        await coordinator.HandleSessionStartedAsync(payload);

        var shown = ui.Shown[0];
        Assert.Equal(payload, shown.Payload);
        Assert.False(string.IsNullOrWhiteSpace(shown.TeacherDisplayName));
        Assert.Equal(TimeSpan.FromSeconds(5), shown.Duration);
    }

    [Fact]
    public async Task SessionBundlesUpdated_for_joined_session_raises_SessionAllowlistUpdated()
    {
        var hub = new FakeHub();
        var ui = new FakeUi { NextDecision = JoinDecision.Confirmed };
        var coordinator = NewCoordinator(hub, ui);

        SessionBundlesUpdatedPayload? updated = null;
        coordinator.SessionAllowlistUpdated += (_, p) => updated = p;

        var payload = Payload();
        await coordinator.HandleSessionStartedAsync(payload);

        var update = new SessionBundlesUpdatedPayload(
            payload.SessionId,
            new[] { new AllowedAppDto("ProcessName", "notepad") },
            Array.Empty<AllowedDomainDto>());
        hub.RaiseBundlesUpdated(update);

        Assert.Equal(update, updated);
    }

    [Fact]
    public async Task SessionBundlesUpdated_for_unjoined_session_is_dropped()
    {
        var hub = new FakeHub();
        var ui = new FakeUi { NextDecision = JoinDecision.Confirmed };
        var coordinator = NewCoordinator(hub, ui);

        var fired = false;
        coordinator.SessionAllowlistUpdated += (_, _) => fired = true;

        await coordinator.HandleSessionStartedAsync(Payload());

        // Update targets a different session than the one we joined.
        hub.RaiseBundlesUpdated(new SessionBundlesUpdatedPayload(
            Guid.NewGuid(),
            Array.Empty<AllowedAppDto>(),
            Array.Empty<AllowedDomainDto>()));

        Assert.False(fired);
    }

    private static SessionCoordinator NewCoordinator(FakeHub hub, FakeUi ui)
    {
        var settings = Options.Create(new RealtimeSettings { JoinConfirmationDuration = TimeSpan.FromSeconds(5) });
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        return new SessionCoordinator(hub, ui, settings, clock);
    }

    private sealed class FakeHub : ISessionHubConnection
    {
        public AgentConnectionState State => AgentConnectionState.Connected;
#pragma warning disable CS0067 // Interface events not used by these tests.
        public event EventHandler<AgentConnectionState>? StateChanged;
        public event EventHandler<SessionStartedPayload>? SessionStarted;
        public event EventHandler<Guid>? SessionEnded;
#pragma warning restore CS0067
        public event EventHandler<SessionBundlesUpdatedPayload>? SessionBundlesUpdated;

        public void RaiseBundlesUpdated(SessionBundlesUpdatedPayload payload) =>
            SessionBundlesUpdated?.Invoke(this, payload);

        public List<(Guid SessionId, string? JoinCode)> JoinCalls { get; } = new();
        public List<Guid> LeaveCalls { get; } = new();
        public List<(Guid SessionId, string Reason)> DeclineCalls { get; } = new();
        public List<(Guid SessionId, string Kind, string PayloadJson)> ReportCalls { get; } = new();
        // Ordered record of report/leave calls so tests can assert ManualLeave
        // is reported *before* the participant leaves (the backend rejects the
        // event once LeftAt is set).
        public List<string> Operations { get; } = new();

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task JoinSessionAsync(Guid sessionId, string? joinCode, CancellationToken ct = default)
        {
            JoinCalls.Add((sessionId, joinCode));
            return Task.CompletedTask;
        }
        public Task LeaveSessionAsync(Guid sessionId, CancellationToken ct = default)
        {
            LeaveCalls.Add(sessionId);
            Operations.Add("leave");
            return Task.CompletedTask;
        }
        public Task DeclineSessionAsync(Guid sessionId, string reason, CancellationToken ct = default)
        {
            DeclineCalls.Add((sessionId, reason));
            return Task.CompletedTask;
        }
        // When set, ReportEventAsync records the call then faults — lets the
        // AgentKilled-on-quit test prove a failed post is swallowed.
        public Exception? ReportThrows { get; set; }

        public Task ReportEventAsync(Guid sessionId, string kind, string payloadJson, DateTimeOffset? occurredAt = null, CancellationToken ct = default)
        {
            ReportCalls.Add((sessionId, kind, payloadJson));
            Operations.Add($"report:{kind}");
            return ReportThrows is null ? Task.CompletedTask : Task.FromException(ReportThrows);
        }
        public Task<bool> HeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeUi : ISessionUiHost
    {
        private readonly TaskCompletionSource _shownSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<JoinDecision>? _pending;

        public List<JoinConfirmation> Shown { get; } = new();
        public bool Dismissed { get; private set; }
        public JoinDecision NextDecision { get; set; } = JoinDecision.Confirmed;
        public bool ManualResolve { get; set; }

        public Task<JoinDecision> ShowJoinConfirmationAsync(JoinConfirmation confirmation, CancellationToken ct = default)
        {
            Shown.Add(confirmation);

            if (ManualResolve)
            {
                _pending = new TaskCompletionSource<JoinDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
                confirmation.Finished += (_, d) => _pending.TrySetResult(d);
                _shownSignal.TrySetResult();
                return _pending.Task;
            }

            _shownSignal.TrySetResult();
            return Task.FromResult(NextDecision);
        }

        public void DismissJoinConfirmation() => Dismissed = true;

        public Task WaitForShownAsync() => _shownSignal.Task;

        public void Resolve(JoinDecision decision) => _pending?.TrySetResult(decision);
    }
}
