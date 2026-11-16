import 'dart:convert';

import '../alerts/live_alerts.dart';
import '../api/sessions_api.dart';
import '../l10n/app_localizations.dart';
import '../realtime/session_hub_client.dart';
import 'live_feed.dart';

/// The stored kinds (the backend's `EventKind`) the live feed has a row for:
/// those [feedEventOfStored] reads as a hub event. The live page asks the
/// backend for these only (#492): the agent reports every app switch, which
/// would crowd them out.
const List<String> feedStoredKinds = <String>[
  'UnblockRequest',
  'TamperDetected',
  'HeartbeatLost',
  'AgentReconnected',
  'AgentMissing',
  'NotConnected',
  'JoinDeclined',
  'ManualLeave',
  'AgentKilled',
];

/// The most stored events the live page loads for its feed (#492).
const int feedHistoryLimit = 200;

/// [stored], an event the backend stored, as the hub broadcast it to the
/// teacher's live page (contracts/session-hub.json), so the feed reads it as
/// it reads the live ones (#492). [name] is the student's, which the broadcast
/// carries.
///
/// Null for a kind the hub doesn't broadcast to the teacher (an app switch, a
/// blocked site, an approval), and for an access request that names no site,
/// whose broadcast the backend skips.
SessionEvent? feedEventOfStored(
  SessionRecentEvent stored, {
  required String sessionId,
  String? name,
}) {
  final Map<String, dynamic> payload = _decode(stored.payloadJson);
  final DateTime at = stored.occurredAt;
  final Map<String, dynamic> about = <String, dynamic>{
    'sessionId': sessionId,
    'userId': stored.userId,
  };
  switch (stored.kind) {
    case 'UnblockRequest':
      // As the backend reads it for the broadcast.
      final Object? rawHost = payload['host'];
      final String host = rawHost is String ? rawHost.trim().toLowerCase() : '';
      if (host.isEmpty) return null;
      final Object? url = payload['url'];
      return SessionEvent(
        kind: 'UnblockRequested',
        payload: <String, dynamic>{
          ...about,
          'userDisplayName': ?name,
          'host': host,
          'url': url is String ? url : '',
          'requestedAt': _wire(at),
        },
        at: at,
      );
    case 'TamperDetected':
      final Object? kind = payload['kind'];
      return SessionEvent(
        kind: 'TamperDetected',
        payload: <String, dynamic>{
          ...about,
          'userDisplayName': ?name,
          'kind': kind is String && kind.trim().isNotEmpty
              ? kind.trim()
              : 'unknown',
          'detectedAt': _wire(at),
        },
        at: at,
      );
    case 'HeartbeatLost':
      final Object? lastSeenAt = payload['lastSeenAt'];
      return SessionEvent(
        kind: 'HeartbeatLost',
        payload: <String, dynamic>{
          ...about,
          'lastSeenAt': lastSeenAt is String ? lastSeenAt : _wire(at),
        },
        at: at,
      );
    case 'AgentReconnected':
      return SessionEvent(
        kind: 'AgentReconnected',
        payload: <String, dynamic>{...about, 'reconnectedAt': _wire(at)},
        at: at,
      );
    case 'AgentMissing':
      return _stateChanged(sessionId, stored.userId, name, 'AgentMissing', at);
    case 'NotConnected':
      return _stateChanged(sessionId, stored.userId, name, 'NotConnected', at);
    case 'JoinDeclined':
      return _stateChanged(sessionId, stored.userId, name, 'Declined', at);
    case 'ManualLeave':
    case 'AgentKilled':
      return _stateChanged(sessionId, stored.userId, name, 'Left', at);
  }
  return null;
}

/// [stored], an event the backend stored, as one plain sentence the teacher
/// reads (#498): the past session page's event log reads the session the way
/// the live feed does (#491). A kind the hub broadcasts reads as the feed
/// reads its broadcast ([feedEventOfStored], [feedSentence]); the kinds only
/// the backend keeps, an app switch, a blocked website and an approval, get
/// sentences of their own. [name] is the student's, from the roster.
///
/// Never the event's kind or its payload: an event the dashboard doesn't know
/// gets a sentence too.
String storedEventSentence(
  AppLocalizations l10n,
  SessionRecentEvent stored, {
  required String sessionId,
  String? name,
}) {
  final SessionEvent? event = feedEventOfStored(
    stored,
    sessionId: sessionId,
    name: name,
  );
  if (event != null) return feedSentence(l10n, event, nameOf: (_) => name);

  final String who = name == null || name.isEmpty ? 'unknown' : 'named';
  final String n = name ?? '';
  final Map<String, dynamic> payload = _decode(stored.payloadJson);
  switch (stored.kind) {
    case 'ForegroundChange':
      final String app = _nonEmpty(payload['processName']) ?? l10n.feedSomeApp;
      return payload['blocked'] == true
          ? l10n.feedAppBlocked(who, n, app)
          : l10n.feedSwitchedApp(who, n, app);
    case 'BlockedUrl':
      return l10n.feedSiteBlocked(
        who,
        n,
        _hostOf(payload) ?? l10n.feedSomeSite,
      );
    case 'UnblockRequest':
      // One that names no host, which the hub doesn't broadcast: the site of
      // its page, if it names that.
      return l10n.feedAsks(who, n, _hostOf(payload) ?? l10n.feedSomeSite);
    case 'UnblockApproved':
      final String host = _hostOf(payload) ?? l10n.feedSomeSite;
      // An approval for the whole class is the teacher's, under their id.
      return payload['scope'] == 'class'
          ? l10n.feedApprovedForClass(host)
          : l10n.feedApprovedFor(who, n, host);
  }
  return l10n.feedOther;
}

/// What a student did in a session, as the past session page's activity
/// summary lists it after their name (#498): [summaries], the backend's count
/// of each kind of event the student has, as plain phrases ("12 app
/// switches", "3 blocked websites"), the most pressing first. Kinds the
/// teacher reads the same count as one: closing the Anchor app is leaving the
/// session, as the feed says, and kinds the dashboard doesn't know are "other
/// events". Never an event's kind.
List<String> storedSummaryPhrases(
  AppLocalizations l10n,
  Iterable<SessionEventSummary> summaries,
) {
  final Map<_SummaryTopic, int> counts = <_SummaryTopic, int>{};
  for (final SessionEventSummary s in summaries) {
    if (s.count <= 0) continue;
    final _SummaryTopic topic = _summaryTopicOf(s.kind);
    counts[topic] = (counts[topic] ?? 0) + s.count;
  }
  return <String>[
    for (final _SummaryTopic topic in _SummaryTopic.values)
      if (counts[topic] case final int count)
        switch (topic) {
          _SummaryTopic.tamper => l10n.pastSummaryTamper(count),
          _SummaryTopic.agentStopped => l10n.pastSummaryAgentStopped(count),
          _SummaryTopic.agentMissing => l10n.pastSummaryAgentMissing(count),
          _SummaryTopic.notConnected => l10n.pastSummaryNotConnected(count),
          _SummaryTopic.left => l10n.pastSummaryLeft(count),
          _SummaryTopic.declined => l10n.pastSummaryDeclined(count),
          _SummaryTopic.accessRequests => l10n.pastSummaryAccessRequests(count),
          _SummaryTopic.approved => l10n.pastSummaryApproved(count),
          _SummaryTopic.blockedSites => l10n.pastSummaryBlockedSites(count),
          _SummaryTopic.appSwitches => l10n.pastSummaryAppSwitches(count),
          _SummaryTopic.agentBack => l10n.pastSummaryAgentBack(count),
          _SummaryTopic.other => l10n.pastSummaryOther(count),
        },
  ];
}

/// What the activity summary counts, in the order it lists them: what needs
/// the teacher's attention first, as the feed ranks it (#491).
enum _SummaryTopic {
  tamper,
  agentStopped,
  agentMissing,
  notConnected,
  left,
  declined,
  accessRequests,
  approved,
  blockedSites,
  appSwitches,
  agentBack,
  other,
}

_SummaryTopic _summaryTopicOf(String kind) => switch (kind) {
  'TamperDetected' => _SummaryTopic.tamper,
  'HeartbeatLost' => _SummaryTopic.agentStopped,
  'AgentMissing' => _SummaryTopic.agentMissing,
  'NotConnected' => _SummaryTopic.notConnected,
  'ManualLeave' || 'AgentKilled' => _SummaryTopic.left,
  'JoinDeclined' => _SummaryTopic.declined,
  'UnblockRequest' => _SummaryTopic.accessRequests,
  'UnblockApproved' => _SummaryTopic.approved,
  'BlockedUrl' => _SummaryTopic.blockedSites,
  'ForegroundChange' => _SummaryTopic.appSwitches,
  'AgentReconnected' => _SummaryTopic.agentBack,
  _ => _SummaryTopic.other,
};

/// The site a stored event names: its `host`, or the host of its `url`.
String? _hostOf(Map<String, dynamic> payload) {
  final String? host = _nonEmpty(payload['host']);
  if (host != null) return host.toLowerCase();
  final String? url = _nonEmpty(payload['url']);
  final String? fromUrl = url == null ? null : Uri.tryParse(url)?.host;
  return fromUrl == null || fromUrl.isEmpty ? null : fromUrl;
}

String? _nonEmpty(Object? value) {
  if (value is! String) return null;
  final String trimmed = value.trim();
  return trimmed.isEmpty ? null : trimmed;
}

/// What happened in the session before the live page opened, newest first, in
/// the hub's shapes (#492), so the feed reads it as it reads the live events.
///
/// [stored] are the session's stored events, newest first, as
/// [SessionsApi.sessionEvents] returns them. A student joining the backend
/// keeps only in [participants], the roster: when they first joined (one who
/// comes back keeps that first join). A student who is out of the session left
/// at their `leftAt`, the server's time the live page heard: it stands for
/// their latest stored leave, which the agent reports by its own clock, and
/// for a leave whose report never arrived. The roster names the students too.
///
/// The session starting is left out: the teacher's page hears it only when
/// they take part themselves (#410), and its header says when it started.
///
/// [complete] is false when the backend cut [stored] at its limit: then what
/// the roster adds from before the oldest of them stays out, rather than
/// standing next to a gap.
List<SessionEvent> feedHistory({
  required String sessionId,
  required List<SessionRecentEvent> stored,
  List<SessionParticipantInfo> participants = const [],
  bool complete = true,
}) {
  final Map<String, SessionParticipantInfo> byId = {
    for (final SessionParticipantInfo p in participants) p.userId: p,
  };

  // The latest stored leave of each student, which their leftAt stands for.
  final Map<String, SessionRecentEvent> latestLeave = {};
  for (final SessionRecentEvent e in stored) {
    if (e.kind != 'ManualLeave' && e.kind != 'AgentKilled') continue;
    final SessionRecentEvent? known = latestLeave[e.userId];
    if (known == null || e.occurredAt.isAfter(known.occurredAt)) {
      latestLeave[e.userId] = e;
    }
  }

  final List<SessionEvent> events = <SessionEvent>[];
  for (final SessionRecentEvent e in stored) {
    final SessionParticipantInfo? p = byId[e.userId];
    final DateTime? leftAt = p?.leftAt;
    if (leftAt != null && identical(latestLeave[e.userId], e)) {
      events.add(
        _stateChanged(sessionId, e.userId, p!.displayName, 'Left', leftAt),
      );
      continue;
    }
    final SessionEvent? event = feedEventOfStored(
      e,
      sessionId: sessionId,
      name: p?.displayName,
    );
    if (event != null) events.add(event);
  }

  final List<SessionEvent> rebuilt = <SessionEvent>[
    for (final SessionParticipantInfo p in participants) ...<SessionEvent>[
      if (p.joinedAt case final DateTime joinedAt)
        _stateChanged(sessionId, p.userId, p.displayName, 'Joined', joinedAt),
      if (p.leftAt case final DateTime leftAt
          when !latestLeave.containsKey(p.userId))
        _stateChanged(sessionId, p.userId, p.displayName, 'Left', leftAt),
    ],
  ];
  final DateTime? oldest = complete || stored.isEmpty
      ? null
      : stored.last.occurredAt;
  for (final SessionEvent e in rebuilt) {
    if (oldest == null || !e.at.isBefore(oldest)) events.add(e);
  }

  // Newest first; at the same moment, as listed above (the stored events
  // before what the roster adds).
  final List<int> order = List<int>.generate(events.length, (i) => i)
    ..sort((a, b) {
      final int byTime = events[b].at.compareTo(events[a].at);
      return byTime != 0 ? byTime : a.compareTo(b);
    });
  return <SessionEvent>[for (final int i in order) events[i]];
}

/// What the feed shows (#492, #499), newest first: the events the page heard
/// live, in the order it heard them, and in their place in time among them
/// the session's [history], less what the page heard live too.
///
/// The page hears nothing from before it joined the session, nor while its
/// hub connection is down. It loads the history once it has joined, and again
/// after each reconnect, so an event between a join and the load is in both,
/// and shows once. What it missed while offline is newer than what it heard
/// before the drop, and older than what it hears after.
///
/// The history's times are the server's; a live event's is when the browser
/// received it, by its own clock. So a live event takes the server's time of
/// it: the history's, when the history has it too, or the time its payload
/// carries. And since the page heard the live events in order, each happened
/// no earlier than any it heard before it. A history event goes above the
/// first live event that is older; the live events keep the order they came
/// in. One whose payload carries no time (the session ending) stays above the
/// history events next to it.
List<SessionEvent> feedTimeline(
  List<SessionEvent> live,
  List<SessionEvent> history,
) {
  if (history.isEmpty) return live;
  final Map<String, DateTime> stored = <String, DateTime>{
    for (final SessionEvent e in history)
      if (feedEventIdentity(e) case final String id) id: e.at,
  };
  final Set<String> heard = <String>{};
  // Each live event's place on the server's clock, from the oldest up: no
  // earlier than the one heard before it.
  final List<DateTime?> placed = List<DateTime?>.filled(live.length, null);
  DateTime? floor;
  for (int i = live.length - 1; i >= 0; i--) {
    final String? id = feedEventIdentity(live[i]);
    if (id != null) heard.add(id);
    final DateTime? at = (id == null ? null : stored[id]) ?? _sentAt(live[i]);
    if (at == null) continue;
    if (floor == null || at.isAfter(floor)) floor = at;
    placed[i] = floor;
  }

  final List<SessionEvent> unheard = <SessionEvent>[
    for (final SessionEvent e in history)
      if (!heard.contains(feedEventIdentity(e))) e,
  ];
  final List<SessionEvent> timeline = <SessionEvent>[];
  int next = 0;
  for (int i = 0; i < live.length; i++) {
    final DateTime? at = placed[i];
    while (at != null &&
        next < unheard.length &&
        unheard[next].at.isAfter(at)) {
      timeline.add(unheard[next++]);
    }
    timeline.add(live[i]);
  }
  return timeline..addAll(unheard.skip(next));
}

/// The feed's history once the page has loaded it again (#499): [fresh], what
/// the backend has now, newest first. If the backend cut that at its limit,
/// [cutAt] is the time of the oldest stored event it returned, and what the
/// [previous] load had from before then stays below it: the new load no longer
/// reaches it, and a busy session's feed would otherwise lose its oldest rows
/// on each reconnect.
List<SessionEvent> feedHistoryReloaded(
  List<SessionEvent> previous,
  List<SessionEvent> fresh, {
  DateTime? cutAt,
}) {
  if (cutAt == null) return fresh;
  return <SessionEvent>[
    ...fresh,
    for (final SessionEvent e in previous)
      if (e.at.isBefore(cutAt)) e,
  ];
}

/// The server's time of [event] as its payload carries it (#499): the field
/// the hub contract gives each kind. An agent that stopped reporting carries
/// when it was last seen, a little before the server noticed. Null for the
/// session ending, which carries none, and a kind the feed doesn't know.
DateTime? _sentAt(SessionEvent event) {
  final String? field = switch (event.kind) {
    'ParticipantStateChanged' => 'at',
    'UnblockRequested' => 'requestedAt',
    'TamperDetected' => 'detectedAt',
    'HeartbeatLost' => 'lastSeenAt',
    'AgentReconnected' => 'reconnectedAt',
    'SessionStarted' => 'startedAt',
    _ => null,
  };
  final Object? wire = field == null ? null : event.payload[field];
  return wire is String ? DateTime.tryParse(wire) : null;
}

/// What makes [event] the event it is, the same whether the hub broadcast it
/// or [feedHistory] rebuilt it from what the backend stored (#492): its kind,
/// who it is about, what it says, and the server's time of it, which both
/// carry. Null for a kind [feedHistory] never rebuilds (the session starting
/// or ending), which only ever comes live.
///
/// Reads only the fields the hub contract gives each kind.
String? feedEventIdentity(SessionEvent event) {
  final Map<String, dynamic> p = event.payload;
  final String? user = liveEventUserId(event);
  final List<Object?>? parts = switch (event.kind) {
    'ParticipantStateChanged' => <Object?>[user, p['state'], _instant(p['at'])],
    'UnblockRequested' => <Object?>[
      user,
      p['host'],
      _instant(p['requestedAt']),
    ],
    'TamperDetected' => <Object?>[user, p['kind'], _instant(p['detectedAt'])],
    'HeartbeatLost' => <Object?>[user, _instant(p['lastSeenAt'])],
    'AgentReconnected' => <Object?>[user, _instant(p['reconnectedAt'])],
    _ => null,
  };
  return parts == null ? null : <Object?>[event.kind, ...parts].join('|');
}

/// A server time from a payload, as the moment it names: the same in any
/// notation (`Z` or `+00:00`, seven fractional digits or three), to the
/// millisecond, which is all a browser keeps.
Object? _instant(Object? wire) {
  if (wire is! String) return wire;
  return DateTime.tryParse(wire)?.millisecondsSinceEpoch ?? wire;
}

SessionEvent _stateChanged(
  String sessionId,
  String userId,
  String? name,
  String state,
  DateTime at,
) => SessionEvent(
  kind: 'ParticipantStateChanged',
  payload: <String, dynamic>{
    'sessionId': sessionId,
    'userId': userId,
    'displayName': ?name,
    'state': state,
    'at': _wire(at),
  },
  at: at,
);

/// A time as the hub sends it.
String _wire(DateTime at) => at.toUtc().toIso8601String();

Map<String, dynamic> _decode(String json) {
  try {
    final Object? decoded = jsonDecode(json);
    return decoded is Map<String, dynamic>
        ? decoded
        : const <String, dynamic>{};
  } on FormatException {
    return const <String, dynamic>{};
  }
}
