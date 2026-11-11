import 'dart:convert';

import '../alerts/live_alerts.dart';
import '../api/sessions_api.dart';
import '../realtime/session_hub_client.dart';

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
    case 'JoinDeclined':
      return _stateChanged(sessionId, stored.userId, name, 'Declined', at);
    case 'ManualLeave':
    case 'AgentKilled':
      return _stateChanged(sessionId, stored.userId, name, 'Left', at);
  }
  return null;
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

/// What the feed shows (#492): the events the page heard live since it opened,
/// newest first, then the session's [history] from before that, less those it
/// heard live too. The page loads the history once it has joined the session,
/// so an event between the join and the load is in both, and shows once.
List<SessionEvent> feedTimeline(
  List<SessionEvent> live,
  List<SessionEvent> history,
) {
  if (history.isEmpty) return live;
  final Set<String> heard = <String>{
    for (final SessionEvent e in live) ?feedEventIdentity(e),
  };
  return <SessionEvent>[
    ...live,
    for (final SessionEvent e in history)
      if (!heard.contains(feedEventIdentity(e))) e,
  ];
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
