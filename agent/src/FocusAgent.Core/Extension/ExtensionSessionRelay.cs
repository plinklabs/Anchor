using System.Text.Json;
using System.Text.Json.Serialization;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using FocusAgent.Core.Tamper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Extension;

/// <summary>
/// Makes the agent the extension's only identity (#489). The extension used to
/// learn its session over a hub connection of its own, signed in through the
/// browser; when that sign-in went wrong (another account, a personal one, a
/// closed sign-in window) it filtered nothing and nobody saw it. Now the agent,
/// which signs in silently with the Windows account, hands the session down over
/// the witness link and reports for the extension with its own connection:
///
/// <list type="bullet">
/// <item>agent → extension: <see cref="ExtensionMessages.SessionStarted"/> once
/// the student has joined (so a declined session never reaches the extension),
/// <see cref="ExtensionMessages.AllowlistReplaced"/> on a bundle change,
/// <see cref="ExtensionMessages.AllowlistAmended"/> on an approved unblock
/// request, <see cref="ExtensionMessages.UnblockDenied"/> on a denied one (#523),
/// <see cref="ExtensionMessages.SessionEnded"/> when the session ends or
/// the student leaves it, and on every witness (re)connect the current session
/// again, or <see cref="ExtensionMessages.NoSession"/>. Every Edge profile runs
/// its own copy of the extension over its own connection (#517): each gets the
/// state when it connects, and every change goes to all of them.</item>
/// <item>extension → agent: blocked pages (by their host only, #527), unblock
/// requests (answered with <see cref="ExtensionMessages.UnblockRequestResult"/>,
/// to the connection that asked) and the extension's own
/// tamper signals, sent on as <c>ReportEvent</c> calls for the joined session.
/// What the extension says about its own settings
/// (<see cref="ExtensionMessages.SelfCheck"/>, #524) is
/// <see cref="ExtensionSelfCheckWatch"/>'s to read.</item>
/// <item>The extension's liveness ping (<c>ExtensionHeartbeat</c>, #149), sent
/// for it while at least one witness link is up, so <c>extension_silent</c> keeps
/// working.</item>
/// </list>
///
/// Every state change and the message it causes happen under one lock, and the
/// channel queues each connection's messages in order, so no extension can see a
/// stale session after a newer one (a connect snapshot racing a join, say).
/// </summary>
public sealed class ExtensionSessionRelay : IAsyncDisposable
{
    // Parse (case-insensitive) to Anchor.Domain.Events.EventKind on the backend's
    // ReportEvent, as the extension's hub client sent them before #489.
    private const string BlockedUrlKind = "BlockedUrl";
    private const string UnblockRequestKind = "UnblockRequest";
    private const string TamperDetectedKind = "TamperDetected";

    // The in-browser tamper kinds the extension witnesses itself (#105). Anything
    // else from the link is dropped rather than shown on the teacher's roster.
    // Since #524 the extension hands its site access to the agent as a self-check
    // instead (ExtensionSelfCheckWatch); one older than that still reports
    // host_permission_revoked itself, and it still reaches the teacher.
    private static readonly HashSet<string> ExtensionTamperKinds =
        new(StringComparer.Ordinal) { TamperKinds.InPrivateOpened, TamperKinds.HostPermissionRevoked };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IExtensionWitnessChannel _witness;
    private readonly SessionCoordinator _coordinator;
    private readonly ISessionHubConnection _hub;
    private readonly TimeProvider _clock;
    private readonly SessionSettings _settings;
    private readonly ILogger<ExtensionSessionRelay> _log;
    private readonly object _gate = new();

    private SessionStartedPayload? _session;
    private List<AllowedDomainDto> _domains = new();
    // Ids of the open witness connections, one per Edge profile.
    private readonly HashSet<long> _connections = new();
    private ITimer? _heartbeat;

    public ExtensionSessionRelay(
        IExtensionWitnessChannel witness,
        SessionCoordinator coordinator,
        ISessionHubConnection hub,
        IOptions<SessionSettings> settings,
        TimeProvider? clock = null,
        ILogger<ExtensionSessionRelay>? log = null)
    {
        _witness = witness;
        _coordinator = coordinator;
        _hub = hub;
        _settings = settings.Value;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<ExtensionSessionRelay>.Instance;

        _coordinator.SessionJoined += OnSessionJoined;
        _coordinator.SessionLeft += OnSessionLeft;
        _coordinator.SessionAllowlistUpdated += OnAllowlistUpdated;
        _hub.AllowlistAmended += OnAllowlistAmended;
        _hub.UnblockDenied += OnUnblockDenied;
        _witness.WitnessConnected += OnWitnessConnected;
        _witness.WitnessDisconnected += OnWitnessDisconnected;
        _witness.MessageReceived += OnMessageReceived;
    }

    /// <summary>The session the extension is told it is in, or null.</summary>
    public Guid? SessionId
    {
        get { lock (_gate) return _session?.SessionId; }
    }

    /// <summary>The domains the extension is told to allow, as last handed down.</summary>
    public IReadOnlyList<AllowedDomainDto> Domains
    {
        get { lock (_gate) return _domains.ToArray(); }
    }

    // ---- agent → extension ---------------------------------------------------

    private void OnSessionJoined(object? sender, SessionStartedPayload payload)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _settings.HeartbeatIntervalSeconds));
        ITimer? previous;
        int sent;
        lock (_gate)
        {
            _session = payload;
            _domains = payload.Domains.ToList();
            previous = _heartbeat;
            var sessionId = payload.SessionId;
            _heartbeat = _clock.CreateTimer(_ => OnHeartbeatDue(sessionId), null, interval, interval);
            sent = _witness.Broadcast(SessionStartedMessage());
        }
        previous?.Dispose();
        _log.LogInformation(
            "Handing session {SessionId} ({DomainCount} domains) to the extension: {Outcome}.",
            payload.SessionId, payload.Domains.Count,
            sent > 0 ? $"sent to {sent}" : "no extension connected, sent when one connects");
    }

    private void OnSessionLeft(object? sender, Guid sessionId)
    {
        ITimer? heartbeat;
        int sent;
        lock (_gate)
        {
            if (_session?.SessionId != sessionId) return;
            _session = null;
            _domains = new List<AllowedDomainDto>();
            heartbeat = _heartbeat;
            _heartbeat = null;
            sent = _witness.Broadcast(Serialize(new { type = ExtensionMessages.SessionEnded, sessionId }));
        }
        heartbeat?.Dispose();
        _log.LogInformation(
            "Telling the extension session {SessionId} is over: {Outcome}.",
            sessionId, sent > 0 ? $"sent to {sent}" : "no extension connected");
    }

    private void OnAllowlistUpdated(object? sender, SessionBundlesUpdatedPayload payload)
    {
        lock (_gate)
        {
            if (_session?.SessionId != payload.SessionId) return;
            _domains = payload.Domains.ToList();
            _witness.Broadcast(Serialize(new
            {
                type = ExtensionMessages.AllowlistReplaced,
                sessionId = payload.SessionId,
                domains = _domains,
            }));
        }
        _log.LogInformation(
            "Handed the extension the new allowlist of session {SessionId} ({DomainCount} domains).",
            payload.SessionId, payload.Domains.Count);
    }

    private void OnAllowlistAmended(object? sender, AllowlistAmendedPayload payload)
    {
        lock (_gate)
        {
            if (_session?.SessionId != payload.SessionId)
            {
                _log.LogDebug("Ignoring AllowlistAmended for {SessionId}: not the joined session.", payload.SessionId);
                return;
            }
            var seen = _domains.Select(DomainKey).ToHashSet(StringComparer.Ordinal);
            foreach (var domain in payload.AddedDomains)
            {
                if (string.IsNullOrWhiteSpace(domain.Value)) continue;
                if (seen.Add(DomainKey(domain)))
                    _domains.Add(domain);
            }
            _witness.Broadcast(Serialize(new
            {
                type = ExtensionMessages.AllowlistAmended,
                sessionId = payload.SessionId,
                addedDomains = payload.AddedDomains,
            }));
        }
        _log.LogInformation(
            "Handed the extension {DomainCount} domains granted in session {SessionId}.",
            payload.AddedDomains.Count, payload.SessionId);
    }

    // Every profile: the student may have asked from any of them, and only the
    // block page for the host acts on it. Not part of the connect snapshot: a
    // block page opened after the denial has nothing pending to decline.
    private void OnUnblockDenied(object? sender, UnblockDeniedPayload payload)
    {
        int sent;
        lock (_gate)
        {
            if (_session?.SessionId != payload.SessionId)
            {
                _log.LogDebug("Ignoring UnblockDenied for {SessionId}: not the joined session.", payload.SessionId);
                return;
            }
            sent = _witness.Broadcast(Serialize(new
            {
                type = ExtensionMessages.UnblockDenied,
                sessionId = payload.SessionId,
                host = payload.Host,
            }));
        }
        _log.LogInformation(
            "Told the extension the teacher declined {Host} in session {SessionId}: {Outcome}.",
            payload.Host, payload.SessionId, sent > 0 ? $"sent to {sent}" : "no extension connected");
    }

    private void OnWitnessConnected(object? sender, WitnessConnection connection)
    {
        Guid? sessionId;
        lock (_gate)
        {
            _connections.Add(connection.Id);
            sessionId = _session?.SessionId;
            // The extension may have missed anything sent while its link was down
            // (a revived service worker, Edge restarted, the extension switched
            // back on), or never had one (a second profile opened mid-session), so
            // it gets the whole state on every connect. Only this connection: the
            // others already have it.
            _witness.TrySend(connection, _session is null
                ? Serialize(new { type = ExtensionMessages.NoSession })
                : SessionStartedMessage());
        }
        _log.LogInformation(
            "Extension connected (connection {Connection}); handed it {State}.",
            connection, sessionId is { } id ? $"session {id}" : "no session");
    }

    private void OnWitnessDisconnected(object? sender, WitnessConnection connection)
    {
        lock (_gate) _connections.Remove(connection.Id);
    }

    // Call under _gate.
    private string SessionStartedMessage()
    {
        var session = _session!;
        return Serialize(new
        {
            type = ExtensionMessages.SessionStarted,
            sessionId = session.SessionId,
            classId = session.ClassId,
            joinCode = session.JoinCode,
            startedAt = session.StartedAt,
            domains = _domains,
        });
    }

    // ---- the extension's liveness ping (#149) ---------------------------------

    private void OnHeartbeatDue(Guid sessionId)
    {
        lock (_gate)
        {
            if (_session?.SessionId != sessionId || _connections.Count == 0) return;
        }
        _ = PingForExtensionAsync(sessionId);
    }

    private async Task PingForExtensionAsync(Guid sessionId)
    {
        try
        {
            await _hub.ExtensionHeartbeatAsync(sessionId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sustained silence is what the backend's absence net watches for; a
            // single dropped ping needs no retry.
            _log.LogDebug(ex, "ExtensionHeartbeat failed for session {SessionId}", sessionId);
        }
    }

    // ---- extension → agent ---------------------------------------------------

    private void OnMessageReceived(object? sender, WitnessMessage received)
    {
        var line = received.Line;
        JsonElement message;
        string? type;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            message = doc.RootElement.Clone();
            type = GetString(message, "type");
        }
        catch (JsonException)
        {
            _log.LogDebug("Ignoring a witness line that isn't JSON.");
            return;
        }

        switch (type)
        {
            case ExtensionMessages.BlockedUrl:
                _ = ReportBlockedUrlAsync(message);
                break;
            case ExtensionMessages.UnblockRequest:
                _ = RelayUnblockRequestAsync(received.Connection, message);
                break;
            case ExtensionMessages.Tamper:
                _ = ReportTamperAsync(message);
                break;
            default:
                // hello / ping keepalives, and anything newer than this agent.
                break;
        }
    }

    private async Task ReportBlockedUrlAsync(JsonElement message)
    {
        var sessionId = JoinedSessionFor(message, ExtensionMessages.BlockedUrl);
        if (sessionId is null) return;

        var occurredAt = DateTimeOffset.TryParse(GetString(message, "occurredAt"), out var at) ? at : _clock.GetUtcNow();
        // The host only, never the URL (#527): a path or query string can hold
        // search terms and other personal detail the teacher doesn't need. An
        // extension the store hasn't updated yet still sends the URL; it goes no
        // further than this.
        var payload = Serialize(new
        {
            host = HostOf(message, GetString(message, "url")),
            tabId = message.TryGetProperty("tabId", out var tab) && tab.TryGetInt32(out var tabId) ? tabId : (int?)null,
            occurredAt,
        });
        try
        {
            await _hub.ReportEventAsync(sessionId.Value, BlockedUrlKind, payload, occurredAt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort like the extension's own report was: re-reporting a block
            // later would be noisier than useful.
            _log.LogWarning(ex, "Reporting BlockedUrl failed for session {SessionId}", sessionId);
        }
    }

    private async Task RelayUnblockRequestAsync(WitnessConnection from, JsonElement message)
    {
        var requestId = GetString(message, "requestId");
        var url = GetString(message, "url");
        var sessionId = JoinedSessionFor(message, ExtensionMessages.UnblockRequest);
        if (sessionId is null || string.IsNullOrWhiteSpace(url))
        {
            Answer(from, requestId, error: sessionId is null ? "Not in this session any more." : "No address to ask for.");
            return;
        }

        var payload = Serialize(new { url, host = HostOf(message, url), reason = GetString(message, "reason") });
        try
        {
            await _hub.ReportEventAsync(sessionId.Value, UnblockRequestKind, payload, _clock.GetUtcNow()).ConfigureAwait(false);
            _log.LogInformation("Forwarded an unblock request in session {SessionId}.", sessionId);
            Answer(from, requestId, error: null);
        }
        catch (Exception ex)
        {
            // The student is waiting on this one: say so on the block page rather
            // than let the request vanish.
            _log.LogWarning(ex, "Reporting UnblockRequest failed for session {SessionId}", sessionId);
            Answer(from, requestId, error: ex.Message);
        }
    }

    // The answer goes to the profile that asked: request ids are only unique
    // within one extension, and the others have no block page waiting on it.
    private void Answer(WitnessConnection to, string? requestId, string? error)
    {
        if (requestId is null) return;
        _witness.TrySend(to, Serialize(new
        {
            type = ExtensionMessages.UnblockRequestResult,
            requestId,
            ok = error is null,
            error,
        }));
    }

    private async Task ReportTamperAsync(JsonElement message)
    {
        var kind = GetString(message, "kind");
        var sessionId = JoinedSessionFor(message, ExtensionMessages.Tamper);
        if (sessionId is null || kind is null || !ExtensionTamperKinds.Contains(kind)) return;

        try
        {
            await _hub.ReportEventAsync(
                sessionId.Value, TamperDetectedKind, Serialize(new { kind }), _clock.GetUtcNow()).ConfigureAwait(false);
            _log.LogWarning("Extension witnessed {Kind} in session {SessionId}; reported.", kind, sessionId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reporting {Kind} failed for session {SessionId}", kind, sessionId);
        }
    }

    /// <summary>
    /// The joined session, when the message is about it. The hub accepts events
    /// only for the session the student is in; a message about another one comes
    /// from an extension that hasn't heard the change yet, and is dropped.
    /// </summary>
    private Guid? JoinedSessionFor(JsonElement message, string type)
    {
        Guid? joined;
        lock (_gate) joined = _session?.SessionId;
        if (joined is null || !Guid.TryParse(GetString(message, "sessionId"), out var claimed) || claimed != joined)
        {
            _log.LogDebug("Dropping {Type} from the extension: not about the joined session.", type);
            return null;
        }
        return joined;
    }

    private static string HostOf(JsonElement message, string? url)
    {
        var host = GetString(message, "host");
        if (!string.IsNullOrWhiteSpace(host)) return host;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string DomainKey(AllowedDomainDto domain) =>
        $"{domain.MatchType} {domain.Value.Trim().ToLowerInvariant()}";

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public ValueTask DisposeAsync()
    {
        _coordinator.SessionJoined -= OnSessionJoined;
        _coordinator.SessionLeft -= OnSessionLeft;
        _coordinator.SessionAllowlistUpdated -= OnAllowlistUpdated;
        _hub.AllowlistAmended -= OnAllowlistAmended;
        _hub.UnblockDenied -= OnUnblockDenied;
        _witness.WitnessConnected -= OnWitnessConnected;
        _witness.WitnessDisconnected -= OnWitnessDisconnected;
        _witness.MessageReceived -= OnMessageReceived;
        ITimer? heartbeat;
        lock (_gate)
        {
            heartbeat = _heartbeat;
            _heartbeat = null;
        }
        heartbeat?.Dispose();
        return ValueTask.CompletedTask;
    }
}
