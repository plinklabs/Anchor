using System.Text.Json;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Extension;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Tamper;

/// <summary>
/// What the extension says about its own settings during a joined session (#524).
/// A student can change Anchor's settings on its page in Edge before the session
/// starts. Nothing fires then, the extension stays enabled and its witness link
/// stays up, so the links alone count it as healthy. So the extension reads its
/// settings on every session it is handed (a join, and every reconnect) and
/// whenever its permissions change, and sends them as
/// <see cref="ExtensionMessages.SelfCheck"/>:
/// <list type="bullet">
/// <item><c>allSites</c>: its "Site access" is "On all sites". A student can only
/// change that on an extension installed from the store; Edge locks it on a
/// policy-installed one. While a connected extension says false,
/// <see cref="SiteAccessLost"/> is true, <see cref="ExtensionGuard"/> pauses Edge,
/// and this watch tells the teacher
/// (<see cref="TamperKinds.HostPermissionRevoked"/>) once the student hasn't put it
/// back within <see cref="SessionSettings.ExtensionSiteAccessReportSeconds"/>.
/// The pause's button has the extension open its own page
/// (<see cref="OpenExtensionPage"/>), and the next check that passes lifts it.</item>
/// <item><c>inPrivate</c>: "Allow in InPrivate" is on. It is off by default on
/// every install, policy ones included, so a laptop nobody touched fails it, and
/// pausing Edge for it would pause every laptop. Without it InPrivate windows
/// aren't filtered; those are handled on their own (the InPrivate witness, #148,
/// and the school profile during a session, #525). Kept here
/// (<see cref="InPrivateAllowed"/>) for what reads the agent's view of the
/// extension.</item>
/// </list>
///
/// Every Edge profile runs its own extension over its own connection (#517), so
/// each connection's latest check is kept until it drops, and a check counts only
/// for the session it names, so an answer about a session that just ended or one
/// that arrives before the join is fully handled can't stick.
/// </summary>
public sealed class ExtensionSelfCheckWatch : IDisposable
{
    private readonly IExtensionWitnessChannel _channel;
    private readonly SessionCoordinator _sessions;
    private readonly ITamperReporter _reporter;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _reportAfter;
    private readonly ILogger<ExtensionSelfCheckWatch> _log;
    private readonly object _gate = new();

    // Held while a check is applied and its change announced, so two connections
    // reporting at once (each on its own read thread) can't deliver Changed out
    // of order.
    private readonly object _announceGate = new();

    private readonly Dictionary<long, Check> _checks = new();
    private Guid? _sessionId;
    private bool _siteAccessLost;
    private ITimer? _reportTimer;
    // Bumped whenever site access comes back or the session changes, so a report
    // timer that was already queued recognises itself as stale.
    private long _generation;

    private sealed record Check(WitnessConnection Connection, Guid SessionId, bool AllSites, bool? InPrivate);

    public ExtensionSelfCheckWatch(
        IExtensionWitnessChannel channel,
        SessionCoordinator sessions,
        ITamperReporter reporter,
        IOptions<SessionSettings> settings,
        TimeProvider? clock = null,
        ILogger<ExtensionSelfCheckWatch>? log = null)
    {
        _channel = channel;
        _sessions = sessions;
        _reporter = reporter;
        _clock = clock ?? TimeProvider.System;
        _reportAfter = TimeSpan.FromSeconds(Math.Max(1, settings.Value.ExtensionSiteAccessReportSeconds));
        _log = log ?? NullLogger<ExtensionSelfCheckWatch>.Instance;

        _channel.MessageReceived += OnMessageReceived;
        _channel.WitnessDisconnected += OnDisconnected;
        _sessions.SessionJoined += OnSessionJoined;
        _sessions.SessionLeft += OnSessionLeft;
    }

    /// <summary>
    /// True while, in the joined session, a connected extension says it may not
    /// work on all sites.
    /// </summary>
    public bool SiteAccessLost
    {
        get { lock (_gate) return _siteAccessLost; }
    }

    /// <summary>
    /// Whether the connected extensions are allowed in InPrivate, as they said
    /// for the joined session: false when any of them isn't, null before any has
    /// said.
    /// </summary>
    public bool? InPrivateAllowed
    {
        get
        {
            lock (_gate)
            {
                var said = CurrentLocked().Where(c => c.InPrivate is not null).ToArray();
                return said.Length == 0 ? null : said.All(c => c.InPrivate == true);
            }
        }
    }

    /// <summary>
    /// Raised when <see cref="SiteAccessLost"/> changes, on the thread that
    /// changed it (a witness read thread, or the session's), never two at once
    /// and in the order the changes happened.
    /// </summary>
    public event EventHandler<bool>? Changed;

    /// <summary>
    /// Asks every connected extension that may not work on all sites to open its
    /// own settings page, where the student puts "Site access" back. Returns how
    /// many were asked.
    /// </summary>
    public int OpenExtensionPage()
    {
        WitnessConnection[] lacking;
        Guid? sessionId;
        lock (_gate)
        {
            sessionId = _sessionId;
            lacking = CurrentLocked().Where(c => !c.AllSites).Select(c => c.Connection).ToArray();
        }
        if (sessionId is null) return 0;

        var line = JsonSerializer.Serialize(new { type = ExtensionMessages.OpenExtensionPage, sessionId });
        var sent = lacking.Count(connection => _channel.TrySend(connection, line));
        _log.LogInformation(
            "Asked {Sent} extension(s) to open their settings page so site access can be put back.", sent);
        return sent;
    }

    private void OnMessageReceived(object? sender, WitnessMessage received)
    {
        if (!TryParse(received.Line, out var sessionId, out var allSites, out var inPrivate))
            return;

        _log.LogInformation(
            "Extension (connection {Connection}) checked its settings for session {SessionId}: site access {SiteAccess}, InPrivate {InPrivate}.",
            received.Connection, sessionId, allSites ? "on all sites" : "NOT on all sites",
            inPrivate switch { true => "allowed", false => "not allowed", null => "unknown" });

        Update(() => _checks[received.Connection.Id] = new Check(received.Connection, sessionId, allSites, inPrivate));
    }

    private void OnDisconnected(object? sender, WitnessConnection connection) =>
        Update(() => _checks.Remove(connection.Id));

    private void OnSessionJoined(object? sender, SessionStartedPayload payload) =>
        Update(() => _sessionId = payload.SessionId);

    private void OnSessionLeft(object? sender, Guid sessionId) =>
        Update(() =>
        {
            if (_sessionId == sessionId)
                _sessionId = null;
        });

    /// <summary>Applies <paramref name="change"/>, then follows where site access ends up.</summary>
    private void Update(Action change)
    {
        lock (_announceGate)
        {
            bool lost;
            Guid? reportFor = null;
            lock (_gate)
            {
                var before = _siteAccessLost;
                var sessionBefore = _sessionId;
                change();
                lost = CurrentLocked().Any(c => !c.AllSites);
                _siteAccessLost = lost;
                if (lost == before && _sessionId == sessionBefore)
                    return;

                // A new outage (or the same one in a new session) gets its own
                // report timer; site access back, or no session, cancels it.
                _generation++;
                _reportTimer?.Dispose();
                _reportTimer = null;
                if (lost && _sessionId is { } sessionId)
                {
                    reportFor = sessionId;
                    var generation = _generation;
                    _reportTimer = _clock.CreateTimer(
                        _ => OnReportDue(sessionId, generation), null, _reportAfter, Timeout.InfiniteTimeSpan);
                }
                if (lost == before)
                    return;
            }

            if (lost)
                _log.LogWarning(
                    "The extension may not work on all sites in session {SessionId}; the teacher is told if it isn't put back within {Seconds}s.",
                    reportFor, (int)_reportAfter.TotalSeconds);
            else
                _log.LogInformation("Every connected extension may work on all sites again.");
            try
            {
                Changed?.Invoke(this, lost);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "A self-check Changed subscriber threw (siteAccessLost={Lost})", lost);
            }
        }
    }

    private void OnReportDue(Guid sessionId, long generation)
    {
        lock (_gate)
        {
            if (generation != _generation || _sessionId != sessionId || !_siteAccessLost)
                return;
            _reportTimer?.Dispose();
            _reportTimer = null;
        }
        _log.LogWarning(
            "Site access still not back after {Seconds}s in session {SessionId}; reporting {Kind}.",
            (int)_reportAfter.TotalSeconds, sessionId, TamperKinds.HostPermissionRevoked);
        _ = ReportAsync(sessionId);
    }

    private async Task ReportAsync(Guid sessionId)
    {
        try
        {
            await _reporter.ReportAsync(sessionId, TamperKinds.HostPermissionRevoked).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort like every other tamper report: the pause stays either way.
            _log.LogWarning(ex, "Reporting {Kind} failed for session {SessionId}",
                TamperKinds.HostPermissionRevoked, sessionId);
        }
    }

    // The checks that count: the latest of each connected extension, about the
    // joined session. Call under _gate.
    private IEnumerable<Check> CurrentLocked() =>
        _sessionId is { } sessionId ? _checks.Values.Where(c => c.SessionId == sessionId) : Enumerable.Empty<Check>();

    private static bool TryParse(string line, out Guid sessionId, out bool allSites, out bool? inPrivate)
    {
        sessionId = Guid.Empty;
        allSites = true;
        inPrivate = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || type.GetString() != ExtensionMessages.SelfCheck)
                return false;
            if (!root.TryGetProperty("sessionId", out var session) || session.ValueKind != JsonValueKind.String
                || !Guid.TryParse(session.GetString(), out sessionId))
                return false;
            // A check that doesn't say whether it may work on all sites is no check.
            if (!root.TryGetProperty("allSites", out var all) || all.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;
            allSites = all.GetBoolean();
            if (root.TryGetProperty("inPrivate", out var incognito) && incognito.ValueKind is JsonValueKind.True or JsonValueKind.False)
                inPrivate = incognito.GetBoolean();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _channel.MessageReceived -= OnMessageReceived;
        _channel.WitnessDisconnected -= OnDisconnected;
        _sessions.SessionJoined -= OnSessionJoined;
        _sessions.SessionLeft -= OnSessionLeft;
        lock (_gate)
        {
            _generation++;
            _reportTimer?.Dispose();
            _reportTimer = null;
        }
    }
}
