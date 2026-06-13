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
/// </summary>
public sealed class HeartbeatMonitor : BackgroundService
{
    private readonly HeartbeatTracker _tracker;
    private readonly ISessionBroadcaster _broadcaster;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _clock;
    private readonly IOptionsMonitor<HeartbeatOptions> _options;
    private readonly ILogger<HeartbeatMonitor> _log;

    public HeartbeatMonitor(
        HeartbeatTracker tracker,
        ISessionBroadcaster broadcaster,
        IServiceScopeFactory scopeFactory,
        TimeProvider clock,
        IOptionsMonitor<HeartbeatOptions> options,
        ILogger<HeartbeatMonitor> log)
    {
        _tracker = tracker;
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
            var isStale = age > timeout;

            if (isStale && !p.Reported)
            {
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
        => p.Source switch
        {
            // The extension witness going silent is the absence-net (#149):
            // surface it as a tamper flag, not an agent HeartbeatLost.
            WitnessSource.Extension => EmitExtensionSilentAsync(db, p, now, ct),
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
