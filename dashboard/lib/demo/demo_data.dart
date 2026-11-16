// Stable, deterministic demo data for the website screenshot generator (#250).
//
// Every value here is fixed (no `DateTime.now()`, no randomness) so the
// generated PNGs are byte-stable across runs — a teacher "Ms Rivera", a single
// class "3B", a small sample allowlist, and a handful of events. This is the
// single source of truth the fake APIs in `main_demo.dart` read from, so the
// home / session / bundles / classes / history surfaces all tell one coherent
// story rather than drifting apart.
//
// This file is demo-only: it is never imported by `main.dart` (the production
// entrypoint) and ships no behaviour — just constant data.

import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';

/// The signed-in teacher shown across every surface.
const String demoTeacherName = 'Ms Rivera';
const String demoTeacherUpn = 'rivera@school.example';

/// The single demo class. Picked to read well on the website: a short,
/// recognisable secondary-school class code.
const String demoClassId = 'class-3b';
const String demoClassName = '3B';
const String demoSchoolYear = '2025-2026';
const String demoSchoolTag = 'SSM';

/// The live demo session (home resume card + live session view).
const String demoSessionId = 'demo-session-3b';
const String demoJoinCode = 'PLINK-3B';

/// Fixed clock anchors — never `DateTime.now()`, so output stays deterministic.
final DateTime demoSessionStartedAt = DateTime(2026, 6, 16, 9, 5);

/// The end of the demo session's lesson period, and of the one after it
/// (#447): what Home offers, and when the live session is planned to end.
final DateTime demoSessionEndsAt = DateTime(2026, 6, 16, 9, 55);
final DateTime demoNextPeriodEndsAt = DateTime(2026, 6, 16, 10, 45);
final DateTime demoPastSessionStartedAt = DateTime(2026, 6, 12, 9, 15);
final DateTime demoPastSessionEndedAt = DateTime(2026, 6, 12, 10, 5);
const String demoPastSessionId = 'demo-past-session-3b';

/// The class roster — a representative handful of students plus a co-teacher.
List<ClassMember> demoRoster() => [
  ClassMember(
    userId: 'u1',
    entraOid: '00000000-0000-0000-0000-000000000001',
    displayName: 'Ada Lovelace',
    userRole: 'Student',
    membershipRole: '0',
    joinedAt: DateTime.utc(2025, 9, 1),
  ),
  ClassMember(
    userId: 'u2',
    entraOid: '00000000-0000-0000-0000-000000000002',
    displayName: 'Alan Turing',
    userRole: 'Student',
    membershipRole: '0',
    joinedAt: DateTime.utc(2025, 9, 1),
  ),
  ClassMember(
    userId: 'u3',
    entraOid: '00000000-0000-0000-0000-000000000003',
    displayName: 'Grace Hopper',
    userRole: 'Student',
    membershipRole: '0',
    joinedAt: DateTime.utc(2025, 9, 1),
  ),
  ClassMember(
    userId: 'u4',
    entraOid: '00000000-0000-0000-0000-000000000004',
    displayName: 'Katherine Johnson',
    userRole: 'Student',
    membershipRole: '0',
    joinedAt: DateTime.utc(2025, 9, 2),
  ),
  ClassMember(
    userId: 'co1',
    entraOid: '00000000-0000-0000-0000-0000000000c1',
    displayName: 'Mr Okafor',
    userRole: 'Teacher',
    membershipRole: '1',
    joinedAt: DateTime.utc(2025, 9, 1),
  ),
];

/// Live roster for the running session — most in session, one declined, so the
/// "students in session" counter and the state chips both have something to say.
List<SessionParticipantInfo> demoLiveRoster() => [
  SessionParticipantInfo(
    userId: 'u1',
    displayName: 'Ada Lovelace',
    joinedAt: demoSessionStartedAt,
    declinedAt: null,
    leftAt: null,
    state: ParticipantLiveState.joined,
  ),
  SessionParticipantInfo(
    userId: 'u2',
    displayName: 'Alan Turing',
    joinedAt: demoSessionStartedAt,
    declinedAt: null,
    leftAt: null,
    state: ParticipantLiveState.joined,
  ),
  SessionParticipantInfo(
    userId: 'u3',
    displayName: 'Grace Hopper',
    joinedAt: demoSessionStartedAt,
    declinedAt: null,
    leftAt: null,
    state: ParticipantLiveState.heartbeatStale,
  ),
  SessionParticipantInfo(
    userId: 'u4',
    displayName: 'Katherine Johnson',
    joinedAt: null,
    declinedAt: demoSessionStartedAt,
    leftAt: null,
    state: ParticipantLiveState.declined,
  ),
];

/// The bundle catalogue. Two bundles, one opened into the editor view.
List<BundleSummary> demoBundleSummaries() => [
  BundleSummary(
    id: 'b-exam',
    name: 'Exam apps',
    version: 3,
    isArchived: false,
    hasBeenUsed: true,
  ),
  BundleSummary(
    id: 'b-research',
    name: 'Research reading',
    version: 1,
    isArchived: false,
    hasBeenUsed: false,
  ),
];

/// The detail behind the "Exam apps" bundle — a small, readable allowlist.
BundleDetail demoBundleDetail() => BundleDetail(
  id: 'b-exam',
  name: 'Exam apps',
  version: 3,
  isArchived: false,
  hasBeenUsed: true,
  entries: [
    BundleEntry(
      kind: BundleEntryKind.domain,
      value: '*.geogebra.org',
      matchType: BundleEntryMatchType.wildcard,
    ),
    BundleEntry(
      kind: BundleEntryKind.domain,
      value: 'wikipedia.org',
      matchType: BundleEntryMatchType.suffix,
    ),
    BundleEntry(
      kind: BundleEntryKind.domain,
      value: 'smartschool.be',
      matchType: BundleEntryMatchType.suffix,
    ),
  ],
);

/// The bundles currently applied to the live session.
List<SessionBundleInfo> demoSessionBundles() => [
  SessionBundleInfo(id: 'b-exam', name: 'Exam apps'),
];

/// A pending unblock request on the live session, so the live view paints its
/// "Pending requests" panel rather than the empty state.
List<UnblockRequestSummary> demoPendingRequests() => [
  UnblockRequestSummary(
    host: 'docs.google.com',
    count: 2,
    firstRequestedAt: DateTime(2026, 6, 16, 9, 18),
    latestRequestedAt: DateTime(2026, 6, 16, 9, 20),
    requesters: [
      UnblockRequestRequester(
        userId: 'u1',
        displayName: 'Ada Lovelace',
        requestedAt: DateTime(2026, 6, 16, 9, 18),
      ),
      UnblockRequestRequester(
        userId: 'u2',
        displayName: 'Alan Turing',
        requestedAt: DateTime(2026, 6, 16, 9, 20),
      ),
    ],
  ),
];

/// The activity feed for the live session view — a fixed, ordered set, oldest
/// first, the demo hub replays on connect so the feed is populated rather than
/// "Waiting for events…". In the hub's own shapes
/// (contracts/session-hub.json), so the feed reads them as its sentences
/// (#491), in step with [demoLiveRoster] and [demoPendingRequests]. By
/// default the feed shows what needs the teacher; the joins and the start
/// show with all activity. The joins carry the server times the roster has,
/// so the history the page rebuilds from the roster (#492) shows them once.
List<({String kind, Map<String, dynamic> payload})> demoLiveEvents() => [
  (
    kind: 'SessionStarted',
    payload: {
      'sessionId': demoSessionId,
      'classId': demoClassId,
      'joinCode': demoJoinCode,
      'apps': const <Object>[],
      'domains': const <Object>[],
    },
  ),
  for (final (id, name) in const [
    ('u1', 'Ada Lovelace'),
    ('u2', 'Alan Turing'),
    ('u3', 'Grace Hopper'),
  ])
    (
      kind: 'ParticipantStateChanged',
      payload: {
        'sessionId': demoSessionId,
        'userId': id,
        'displayName': name,
        'state': 'Joined',
        // Their joinedAt in [demoLiveRoster].
        'at': demoSessionStartedAt.toUtc().toIso8601String(),
      },
    ),
  (
    kind: 'ParticipantStateChanged',
    payload: {
      'sessionId': demoSessionId,
      'userId': 'u4',
      'displayName': 'Katherine Johnson',
      'state': 'Declined',
    },
  ),
  for (final (id, name) in const [
    ('u1', 'Ada Lovelace'),
    ('u2', 'Alan Turing'),
  ])
    (
      kind: 'UnblockRequested',
      payload: {
        'sessionId': demoSessionId,
        'userId': id,
        'userDisplayName': name,
        'host': 'docs.google.com',
      },
    ),
  (
    kind: 'HeartbeatLost',
    payload: {'sessionId': demoSessionId, 'userId': 'u3'},
  ),
];

/// History list — one ended session for the archive surface.
List<SessionHistoryEntry> demoHistory() => [
  SessionHistoryEntry(
    id: demoPastSessionId,
    classId: demoClassId,
    className: demoClassName,
    startedAt: demoPastSessionStartedAt,
    endedAt: demoPastSessionEndedAt,
  ),
];

/// The detail behind the past session opened from history.
SessionDetail demoPastSessionDetail() => SessionDetail(
  id: demoPastSessionId,
  classId: demoClassId,
  className: demoClassName,
  joinCode: demoJoinCode,
  startedAt: demoPastSessionStartedAt,
  endedAt: demoPastSessionEndedAt,
  summaries: [
    SessionEventSummary(
      userId: 'u1',
      kind: 'ForegroundChange',
      count: 12,
      firstAt: demoPastSessionStartedAt,
      lastAt: demoPastSessionEndedAt,
    ),
    SessionEventSummary(
      userId: 'u2',
      kind: 'BlockedUrl',
      count: 4,
      firstAt: demoPastSessionStartedAt,
      lastAt: demoPastSessionEndedAt,
    ),
  ],
  recentEvents: [
    SessionRecentEvent(
      id: 'e1',
      userId: 'u2',
      kind: 'BlockedUrl',
      payloadJson: '{"host":"youtube.com"}',
      occurredAt: DateTime(2026, 6, 12, 9, 30, 5),
    ),
    SessionRecentEvent(
      id: 'e2',
      userId: 'u1',
      kind: 'ForegroundChange',
      payloadJson: '{"app":"Notes"}',
      occurredAt: DateTime(2026, 6, 12, 9, 42, 0),
    ),
  ],
  participants: [
    SessionParticipantInfo(
      userId: 'u1',
      displayName: 'Ada Lovelace',
      joinedAt: demoPastSessionStartedAt,
      declinedAt: null,
      leftAt: demoPastSessionEndedAt,
      state: ParticipantLiveState.left,
    ),
    SessionParticipantInfo(
      userId: 'u2',
      displayName: 'Alan Turing',
      joinedAt: demoPastSessionStartedAt,
      declinedAt: null,
      leftAt: demoPastSessionEndedAt,
      state: ParticipantLiveState.left,
    ),
  ],
  bundles: [SessionBundleInfo(id: 'b-exam', name: 'Exam apps')],
  grants: const [],
);

/// The live session detail (running, no `endedAt`).
SessionDetail demoLiveSessionDetail() => SessionDetail(
  id: demoSessionId,
  classId: demoClassId,
  className: demoClassName,
  joinCode: demoJoinCode,
  startedAt: demoSessionStartedAt,
  endedAt: null,
  endsAt: demoSessionEndsAt,
  summaries: const [],
  recentEvents: const [],
  participants: demoLiveRoster(),
  bundles: demoSessionBundles(),
  grants: const [],
);
