import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
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
  ParticipantLiveState state, {
  bool tampered = false,
}) => SessionParticipantInfo(
  userId: name,
  displayName: name,
  joinedAt: null,
  declinedAt: null,
  leftAt: null,
  state: state,
  tampered: tampered,
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
  List<SessionParticipantInfo> participants, {
  Locale? locale,
}) async {
  await tester.pumpWidget(
    MaterialApp(
      locale: locale,
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
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
  testWidgets('roster renders every member with its per-state label (#100)', (
    tester,
  ) async {
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

  testWidgets('roster sorts attention-first: stale above joined (#100)', (
    tester,
  ) async {
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

  test('the AgentMissing wire state parses (#444)', () {
    expect(
      ParticipantLiveState.parse('AgentMissing'),
      ParticipantLiveState.agentMissing,
    );
  });

  testWidgets(
    'a student whose agent never connected is flagged and explained (#444)',
    (tester) async {
      await _pumpSession(tester, [
        _participant('Ada', ParticipantLiveState.joined),
        _participant('Zed', ParticipantLiveState.agentMissing),
        _participant('Ed', ParticipantLiveState.neverJoined),
      ]);

      expect(find.text('Agent not connected'), findsOneWidget);
      // The label says what it means for the class, on that row only.
      expect(
        find.byTooltip(
          "Only the browser extension joined. Apps aren't being blocked on "
          'this laptop.',
        ),
        findsOneWidget,
      );
      // Not counted as in session: nothing enforces apps on that laptop.
      expect(find.text('Students (1/3 in session)'), findsOneWidget);
      // Attention-first, above the joined student despite the name.
      expect(
        tester.getTopLeft(find.text('Zed')).dy,
        lessThan(tester.getTopLeft(find.text('Ada')).dy),
      );
      // Painted in the error colour, as a stopped agent is: icon and label.
      final error = Theme.of(
        tester.element(find.text('Zed')),
      ).colorScheme.error;
      expect(
        tester.widget<Icon>(find.byIcon(Icons.desktop_access_disabled)).color,
        error,
      );
      expect(
        tester.widget<Text>(find.text('Agent not connected')).style?.color,
        error,
      );
    },
  );

  testWidgets('the missing-agent state reads in Dutch (#444)', (tester) async {
    await _pumpSession(tester, [
      _participant('Zed', ParticipantLiveState.agentMissing),
    ], locale: const Locale('nl'));

    expect(find.text('Agent niet verbonden'), findsOneWidget);
    expect(
      find.byTooltip(
        'Alleen de browserextensie is aangesloten. Apps worden op deze laptop '
        'niet geblokkeerd.',
      ),
      findsOneWidget,
    );
  });

  test('the NotConnected wire state parses (#493)', () {
    expect(
      ParticipantLiveState.parse('NotConnected'),
      ParticipantLiveState.notConnected,
    );
  });

  testWidgets(
    'a class member whose laptop never joined is flagged, sorted to the top '
    'and explained (#493)',
    (tester) async {
      await _pumpSession(tester, [
        _participant('Ada', ParticipantLiveState.joined),
        _participant('Bo', ParticipantLiveState.left),
        _participant('Zed', ParticipantLiveState.notConnected),
        _participant('Ed', ParticipantLiveState.neverJoined),
      ]);

      expect(find.text('Not connected'), findsOneWidget);
      // The label says what it means for the class, on that row only.
      expect(
        find.byTooltip(
          "This laptop hasn't joined the session. Nothing is being blocked "
          "for this student, or they're absent.",
        ),
        findsOneWidget,
      );
      expect(find.text('Students (1/4 in session)'), findsOneWidget);
      // Attention-first: above everyone who answered or may still join,
      // despite the name.
      final zedY = tester.getTopLeft(find.text('Zed')).dy;
      for (final other in ['Ada', 'Bo', 'Ed']) {
        expect(
          zedY,
          lessThan(tester.getTopLeft(find.text(other)).dy),
          reason: other,
        );
      }
      // Painted in the error colour, as a missing agent is: icon and label.
      // Still within the join grace, a member is calm grey "Not joined".
      final error = Theme.of(
        tester.element(find.text('Zed')),
      ).colorScheme.error;
      expect(tester.widget<Icon>(find.byIcon(Icons.link_off)).color, error);
      expect(
        tester.widget<Text>(find.text('Not connected')).style?.color,
        error,
      );
      expect(
        tester.widget<Text>(find.text('Not joined')).style?.color,
        isNot(error),
      );
    },
  );

  testWidgets('the not-connected state reads in Dutch (#493)', (tester) async {
    await _pumpSession(tester, [
      _participant('Zed', ParticipantLiveState.notConnected),
    ], locale: const Locale('nl'));

    expect(find.text('Niet verbonden'), findsOneWidget);
    expect(
      find.byTooltip(
        'Deze laptop heeft zich niet aangesloten bij de sessie. Voor deze '
        'leerling wordt niets geblokkeerd, of de leerling is afwezig.',
      ),
      findsOneWidget,
    );
  });

  testWidgets('roster flags only tampered students, on the right row (#105)', (
    tester,
  ) async {
    await _pumpSession(tester, [
      _participant('Ada', ParticipantLiveState.joined, tampered: true),
      _participant('Bo', ParticipantLiveState.joined),
    ]);

    // Exactly one flag — Bo (not tampered) carries none.
    expect(find.byTooltip('Tampering detected'), findsOneWidget);

    // …and it sits in Ada's row, not Bo's.
    final flagY = tester.getCenter(find.byTooltip('Tampering detected')).dy;
    final adaY = tester.getCenter(find.text('Ada')).dy;
    final boY = tester.getCenter(find.text('Bo')).dy;
    expect((flagY - adaY).abs(), lessThan((flagY - boY).abs()));
  });
}
