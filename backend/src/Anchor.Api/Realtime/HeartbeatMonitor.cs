using System.Text.Json;
using Anchor.Domain.Events;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Anchor.Api.Realtime;

/// <summary>
/// Periodically walks the in-memory <see cref="HeartbeatTracker"/> and, for
/// participants whose last heartbeat is older than the configured timeout,
/// persists exactly one <see cref="EventKind.HeartbeatLost"/> per outage and
/// broadcasts it on the session group. When a previously-lost participant
/// resumes pinging, an <see cref="EventKind.AgentReconnected"/> event is
/// emitted so the dashboard can flip the indicator back.
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
                await EmitLostAsync(db, p, now, ct).ConfigureAwait(false);
                _tracker.MarkReported(p.SessionId, p.UserId);
            }
            else if (!isStale && p.Reported)
            {
                await EmitReconnectedAsync(db, p, now, ct).ConfigureAwait(false);
                _tracker.ClearReported(p.SessionId, p.UserId);
            }
        }
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
