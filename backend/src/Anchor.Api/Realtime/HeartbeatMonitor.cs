using System.Text.Json;
using Anchor.Domain.Events;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Anchor.Api.Realtime;

/// <summary>
/// Periodically walks the in-memory <see cref="HeartbeatTracker"/> and, for
/// participants whose last heartbeat is older than the configured timeout,
/// persists exactly one staleness event per outage and broadcasts it on the
/// session group. The event depends on the witness <see cref="WitnessSource"/>:
/// an <see cref="WitnessSource.Agent"/> going stale is a
/// <see cref="EventKind.HeartbeatLost"/> (cleared by an
/// <see cref="EventKind.AgentReconnected"/> when it resumes), while an
/// <see cref="WitnessSource.Extension"/> going stale is the witness-independent
/// absence-net (#149): a <see cref="EventKind.TamperDetected"/> with kind
/// <see cref="TamperKinds.ExtensionSilent"/>. The tamper flag is sticky, so a
/// returning extension re-arms silently rather than emitting a reconnect.
///
/// <para>
/// An agent that never pings at all is caught the same way (#444): the hub
/// starts tracking it, as awaiting its first ping, when its student joins, and
/// once <see cref="HeartbeatOptions.AgentJoinGrace"/> passes without one the
/// monitor records an <see cref="EventKind.AgentMissing"/> and tells the
/// teacher's roster the student is
/// <see cref="ParticipantLiveState.AgentMissing"/>. That is how an extension
/// that joined without its agent shows up. An agent that turns up late clears
/// it with an <see cref="EventKind.AgentReconnected"/>, as one that comes back
/// after a <see cref="EventKind.HeartbeatLost"/> does.
/// </para>
///
/// <para>
/// Only an active participant can go missing. Every way of stopping (leaving,
/// declining, quitting the agent, the session ending) drops the participant's
/// tracking, but a ping that passed the hub's active check just before the
/// participant left can still land after that. So before reporting, the
/// monitor checks the participant is still active, and drops the tracking of
/// one who isn't instead (#448).
/// </para>
/// </summary>
public sealed class HeartbeatMonitor : BackgroundService
{
    private readonly HeartbeatTracker _tracker;
    private readonly ActiveParticipantCache _activeParticipants;
    private readonly ISessionBroadcaster _broadcaster;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _clock;
    private readonly IOptionsMonitor<HeartbeatOptions> _options;
    private readonly ILogger<HeartbeatMonitor> _log;

    public HeartbeatMonitor(
        HeartbeatTracker tracker,
        ActiveParticipantCache activeParticipants,
        ISessionBroadcaster broadcaster,
        IServiceScopeFactory scopeFactory,
        TimeProvider clock,
        IOptionsMonitor<HeartbeatOptions> options,
        ILogger<HeartbeatMonitor> log)
    {
        _tracker = tracker;
        _activeParticipants = activeParticipants;
        _broadcaster = broadcaster;
        _scopeFactory = scopeFactory;
        _clock = clock;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "HeartbeatMonitor scan failed");
            }

            try
            {
                await Task.Delay(_options.CurrentValue.ScanInterval, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task ScanOnceAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var now = _clock.GetUtcNow();
        var timeout = opts.Timeout;

        var snapshot = _tracker.Snapshot();
        if (snapshot.Count == 0) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();

        foreach (var p in snapshot)
        {
            var age = now - p.LastSeenAt;
            // An agent that hasn't pinged yet gets the join grace: it may still
            // be on its join toast, and its first ping comes an interval after
            // it joins (#444).
            var isStale = age > (p.AwaitingFirstPing ? opts.AgentJoinGrace : timeout);

            if (isStale && !p.Reported)
            {
                if (!await _activeParticipants
                        .IsActiveAsync(db.SessionParticipants.AsNoTracking(), p.SessionId, p.UserId, ct)
                        .ConfigureAwait(false))
                {
                    // Left, or the session ended: a stopped participant's
                    // silence is expected, not an outage to report.
                    _tracker.Clear(p.SessionId, p.UserId);
                    _log.LogDebug(
                        "Dropped heartbeat tracking of inactive participant session={SessionId} user={UserId}",
                        p.SessionId, p.UserId);
                    continue;
                }

                await EmitStaleAsync(db, p, now, ct).ConfigureAwait(false);
                _tracker.MarkReported(p.SessionId, p.UserId, p.Source);
            }
            else if (!isStale && p.Reported)
            {
                await EmitResumedAsync(db, p, now, ct).ConfigureAwait(false);
                _tracker.ClearReported(p.SessionId, p.UserId, p.Source);
            }
        }
    }

    private Task EmitStaleAsync(AnchorDbContext db, TrackedParticipant p, DateTimeOffset now, CancellationToken ct)
        => p switch
        {
            // The extension witness going silent is the absence-net (#149):
            // surface it as a tamper flag, not an agent HeartbeatLost.
            { Source: WitnessSource.Extension } => EmitExtensionSilentAsync(db, p, now, ct),
            // An agent that never pinged was never there to be lost (#444).
            { AwaitingFirstPing: true } => EmitAgentMissingAsync(db, p, now, ct),
            _ => EmitLostAsync(db, p, now, ct),
        };

    private Task EmitResumedAsync(AnchorDbContext db, TrackedParticipant p, DateTimeOffset now, CancellationToken ct)
        => p.Source switch
        {
            // A tamper flag is sticky by design (soft enforcement, §5.4) — there
            // is no "un-tamper" event — so a returning extension just re-arms
            // silently: clear Reported (done by the caller) and emit nothing,
            // so a *second* outage flags again.
            WitnessSource.Extension => Task.CompletedTask,
            _ => EmitReconnectedAsync(db, p, now, ct),
        };

    private async Task EmitExtensionSilentAsync(AnchorDbContext db, TrackedParticipant p, DateTimeOffset now, CancellationToken ct)
    {
        var displayName = await db.Users.AsNoTracking()
            .Where(u => u.Id == p.UserId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? string.Empty;

        db.Events.Add(new Event
        {
            SessionId = p.SessionId,
            UserId = p.UserId,
            Kind = EventKind.TamperDetected,
            PayloadJson = JsonSerializer.Serialize(new { kind = TamperKinds.ExtensionSilent }),
            OccurredAt = now,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _broadcaster.TamperDetectedAsync(
            new TamperDetectedPayload(p.SessionId, p.UserId, displayName, TamperKinds.ExtensionSilent, now),
            ct).ConfigureAwait(false);
        _log.LogInformation(
            "TamperDetected(extension_silent) session={SessionId} user={UserId} lastSeen={LastSeenAt:o}",
            p.SessionId, p.UserId, p.LastSeenAt);
    }

    private async Task EmitAgentMissingAsync(AnchorDbContext db, TrackedParticipant p, DateTimeOffset now, CancellationToken ct)
    {
        var displayName = await db.Users.AsNoTracking()
            .Where(u => u.Id == p.UserId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? string.Empty;

        db.Events.Add(new Event
        {
            SessionId = p.SessionId,
            UserId = p.UserId,
            Kind = EventKind.AgentMissing,
            PayloadJson = JsonSerializer.Serialize(new { expectedSince = p.LastSeenAt }),
            OccurredAt = now,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        // A state of its own on the roster, so it travels the way the roster's
        // other state changes do; the dashboard re-reads the roster on it.
        await _broadcaster.ParticipantStateChangedAsync(
            new ParticipantStateChangedPayload(
                p.SessionId, p.UserId, displayName, nameof(ParticipantLiveState.AgentMissing), now),
            ct).ConfigureAwait(false);
        _log.LogInformation(
            "AgentMissing session={SessionId} user={UserId} expectedSince={ExpectedSince:o}",
            p.SessionId, p.UserId, p.LastSeenAt);
    }

    private async Task EmitLostAsync(AnchorDbContext db, TrackedParticipant p, DateTimeOffset now, CancellationToken ct)
    {
        var payload = new HeartbeatLostPayload(p.SessionId, p.UserId, p.LastSeenAt);
        db.Events.Add(new Event
        {
            SessionId = p.SessionId,
            UserId = p.UserId,
            Kind = EventKind.HeartbeatLost,
            PayloadJson = JsonSerializer.Serialize(new { lastSeenAt = p.LastSeenAt }),
            OccurredAt = now,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _broadcaster.HeartbeatLostAsync(payload, ct).ConfigureAwait(false);
        _log.LogInformation(
            "HeartbeatLost session={SessionId} user={UserId} lastSeen={LastSeenAt:o}",
            p.SessionId, p.UserId, p.LastSeenAt);
    }

    private async Task EmitReconnectedAsync(AnchorDbContext db, TrackedParticipant p, DateTimeOffset now, CancellationToken ct)
    {
        var payload = new AgentReconnectedPayload(p.SessionId, p.UserId, now);
        db.Events.Add(new Event
        {
            SessionId = p.SessionId,
            UserId = p.UserId,
            Kind = EventKind.AgentReconnected,
            PayloadJson = JsonSerializer.Serialize(new { reconnectedAt = now }),
            OccurredAt = now,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _broadcaster.AgentReconnectedAsync(payload, ct).ConfigureAwait(false);
        _log.LogInformation(
            "AgentReconnected session={SessionId} user={UserId} at={At:o}",
            p.SessionId, p.UserId, now);
    }
}
