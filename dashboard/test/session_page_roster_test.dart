import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/pages/session_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      const [];
}

SessionParticipantInfo _participant(
  String name,
  ParticipantLiveState state,
) =>
    SessionParticipantInfo(
      userId: name,
      displayName: name,
      joinedAt: null,
      declinedAt: null,
      leftAt: null,
      state: state,
    );

class _FakeSessions extends SessionsApi {
  _FakeSessions({required this.participants}) : super(_dummyClient());

  final List<SessionParticipantInfo> participants;

  @override
  Future<SessionDetail> getSession(String sessionId) async => SessionDetail(
    id: sessionId,
    classId: 'c1',
    className: 'Class',
    joinCode: '',
    startedAt: DateTime(2026, 5, 26, 9, 15),
    endedAt: null,
    summaries: const [],
    recentEvents: const [],
    participants: participants,
    bundles: const [],
    grants: const [],
  );

  @override
  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async =>
      const [];
}

Future<void> _pumpSession(
  WidgetTester tester,
  List<SessionParticipantInfo> participants,
) async {
  await tester.pumpWidget(
    MaterialApp(
      home: SessionPage(
        sessionId: 'a1',
        tokens: AuthTokenStore(),
        sessions: _FakeSessions(participants: participants),
        bundles: _FakeBundles(),
        apiBaseUrl: Uri.parse('http://localhost'),
      ),
    ),
  );
  // Two pumps flush initState's getSession + the setState that stores the
  // detail. We avoid pumpAndSettle: the real hub client would keep retrying
  // localhost and stall the test.
  await tester.pump();
  await tester.pump();
}

void main() {
  testWidgets('roster renders every member with its per-state label (#100)',
      (tester) async {
    await _pumpSession(tester, [
      _participant('Ada', ParticipantLiveState.joined),
      _participant('Bo', ParticipantLiveState.heartbeatStale),
      _participant('Cy', ParticipantLiveState.declined),
      _participant('Di', ParticipantLiveState.left),
      _participant('Ed', ParticipantLiveState.neverJoined),
    ]);

    expect(find.text('Agent stopped reporting'), findsOneWidget);
    expect(find.text('In session'), findsOneWidget);
    expect(find.text('Declined'), findsOneWidget);
    expect(find.text('Left'), findsOneWidget);
    expect(find.text('Not joined'), findsOneWidget);
    // Header counts joined-in-session over total.
    expect(find.text('Students (1/5 in session)'), findsOneWidget);
  });

  testWidgets('roster sorts attention-first: stale above joined (#100)',
      (tester) async {
    await _pumpSession(tester, [
      _participant('Ada', ParticipantLiveState.joined),
      _participant('Zoe', ParticipantLiveState.heartbeatStale),
    ]);

    // Zoe (stale) must render above Ada (joined) despite the alphabetical
    // disadvantage — state ranks before name.
    final staleY = tester.getTopLeft(find.text('Zoe')).dy;
    final joinedY = tester.getTopLeft(find.text('Ada')).dy;
    expect(staleY, lessThan(joinedY));
  });
}
