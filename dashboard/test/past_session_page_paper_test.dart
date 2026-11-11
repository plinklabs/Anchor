import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/past_session_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:plink_design_system/plink_design_system.dart';

// Paper redesign of the past-session review (AD7, #172). Verifies the brand
// contracts the redesign introduced:
//  - the review reads as paper instrument panels (no Material Card / Chip /
//    Divider), each section headed by a quiet mono label;
//  - it leads with a muted "Ended" outline badge — the archived counterpart of
//    the live page's magenta LIVE spark — and paints no magenta anywhere;
//  - the audit-trail sections (participants, summary, grants, unapproved
//    requests, event log) still render their data.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

final _startedAt = DateTime(2026, 6, 12, 9, 15);
final _endedAt = DateTime(2026, 6, 12, 10, 5);

class _FakeSessions extends SessionsApi {
  _FakeSessions({required this.detail, this.unapproved = const []})
    : super(_dummyClient());

  final SessionDetail detail;
  final List<UnblockRequestSummary> unapproved;

  @override
  Future<SessionDetail> getSession(String sessionId) async => detail;

  @override
  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async =>
      unapproved;
}

SessionDetail _detail() => SessionDetail(
  id: 's1',
  classId: 'c1',
  className: 'Math 101',
  joinCode: 'ABC123',
  startedAt: _startedAt,
  endedAt: _endedAt,
  summaries: [
    SessionEventSummary(
      userId: 'u1',
      kind: 'ForegroundChange',
      count: 12,
      firstAt: _startedAt,
      lastAt: _endedAt,
    ),
  ],
  recentEvents: [
    SessionRecentEvent(
      id: 'e1',
      userId: 'u1',
      kind: 'BlockedUrl',
      payloadJson: '{"host":"chat.example.com"}',
      occurredAt: DateTime(2026, 6, 12, 9, 30, 5),
    ),
  ],
  participants: [
    SessionParticipantInfo(
      userId: 'u1',
      displayName: 'Ada Lovelace',
      joinedAt: _startedAt,
      declinedAt: null,
      leftAt: null,
      state: ParticipantLiveState.left,
    ),
  ],
  bundles: [SessionBundleInfo(id: 'b1', name: 'Math')],
  grants: [
    SessionUnblockGrantInfo(
      userId: 'u1',
      displayName: 'Ada Lovelace',
      host: 'wikipedia.org',
      grantedAt: DateTime(2026, 6, 12, 9, 45),
    ),
  ],
);

void main() {
  Widget host(SessionsApi sessions) => MaterialApp(
    theme: PlinkTheme.paper,
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: PastSessionPage(sessionId: 's1', sessions: sessions),
  );

  testWidgets(
    'the review reads as paper instrument panels — no Material Card / Chip / '
    'Divider — and leads with a muted "Ended" badge, no magenta',
    (tester) async {
      tester.view.physicalSize = const Size(1200, 1200);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(host(_FakeSessions(detail: _detail())));
      await tester.pumpAndSettle();

      // No Material chrome — the system uses hairlines, never shadows/cards.
      expect(find.byType(Card), findsNothing);
      expect(find.byType(Chip), findsNothing);
      expect(find.byType(Divider), findsNothing);

      // Leads with the calm "Ended" outline badge (PlinkBadge upper-cases) —
      // the archived counterpart of the live page's magenta LIVE spark. There
      // is no LIVE badge anywhere in the archive.
      expect(find.widgetWithText(PlinkBadge, 'ENDED'), findsOneWidget);
      expect(find.widgetWithText(PlinkBadge, 'LIVE'), findsNothing);
      // No constructive/magenta ElevatedButton in a read-only review.
      expect(find.byType(ElevatedButton), findsNothing);

      // The identity + each audit-trail section is present.
      expect(find.text('Math 101'), findsOneWidget);
      expect(find.text('Bundles used'), findsOneWidget);
      expect(find.text('Participants'), findsOneWidget);
      expect(find.text('Activity summary'), findsOneWidget);
      expect(find.text('Approved exceptions'), findsOneWidget);
      expect(find.text('Event log'), findsOneWidget);

      // And their data renders.
      expect(find.text('Ada Lovelace'), findsWidgets);
      expect(
        find.text(
          "Ada Lovelace tried to open chat.example.com, which isn't allowed.",
        ),
        findsOneWidget,
      );

      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('an unapproved request renders in its own audit-trail panel', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1200, 1200);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    final now = DateTime(2026, 6, 12, 9, 50);
    await tester.pumpWidget(
      host(
        _FakeSessions(
          detail: _detail(),
          unapproved: [
            UnblockRequestSummary(
              host: 'youtube.com',
              count: 1,
              firstRequestedAt: now,
              latestRequestedAt: now,
              requesters: [
                UnblockRequestRequester(
                  userId: 'u1',
                  displayName: 'Ada Lovelace',
                  requestedAt: now,
                ),
              ],
            ),
          ],
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Unapproved requests'), findsOneWidget);
    expect(find.text('youtube.com'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  // #498: the event log showed each stored event as its raw kind with the
  // payload JSON under it, and the summary read "12 ForegroundChange". Both
  // now read in plain sentences, the way the live feed does (#491), with the
  // raw event in a tooltip for troubleshooting.
  testWidgets('the event log and the activity summary read as plain sentences, '
      'the raw event out of the way in a tooltip (#498)', (tester) async {
    tester.view.physicalSize = const Size(1200, 1600);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    const teacherId = 'teacher-0000-0000-0000-000000000498';
    SessionEventSummary count(String userId, String kind, int n) =>
        SessionEventSummary(
          userId: userId,
          kind: kind,
          count: n,
          firstAt: _startedAt,
          lastAt: _endedAt,
        );
    SessionRecentEvent stored(
      String id,
      String kind,
      String payloadJson, {
      String userId = 'u1',
    }) => SessionRecentEvent(
      id: id,
      userId: userId,
      kind: kind,
      payloadJson: payloadJson,
      occurredAt: DateTime(2026, 6, 12, 9, 30, 5),
    );

    final base = _detail();
    final detail = SessionDetail(
      id: base.id,
      classId: base.classId,
      className: base.className,
      joinCode: base.joinCode,
      startedAt: base.startedAt,
      endedAt: base.endedAt,
      summaries: [
        count('u1', 'ForegroundChange', 12),
        count('u1', 'BlockedUrl', 3),
        count('u1', 'ManualLeave', 1),
        // The teacher's approval for the whole class is theirs, and they are
        // not on the roster.
        count(teacherId, 'UnblockApproved', 1),
      ],
      recentEvents: [
        stored(
          'e1',
          'BlockedUrl',
          '{"url":"https://chat.example.com/c","host":"chat.example.com"}',
        ),
        stored(
          'e2',
          'ForegroundChange',
          '{"processName":"WINWORD","publisher":"Microsoft Corporation",'
              '"blocked":false}',
        ),
        stored('e3', 'UnblockRequest', '{"host":"youtube.com"}'),
        stored('e4', 'TamperDetected', '{"kind":"inprivate_opened"}'),
        stored(
          'e5',
          'UnblockApproved',
          '{"host":"wikipedia.org","scope":"class"}',
          userId: teacherId,
        ),
        stored('e6', 'HeartbeatLost', '{}'),
      ],
      participants: base.participants,
      bundles: base.bundles,
      grants: base.grants,
    );

    await tester.pumpWidget(host(_FakeSessions(detail: detail)));
    await tester.pumpAndSettle();

    // Each event, as a sentence about the student.
    for (final sentence in const [
      "Ada Lovelace tried to open chat.example.com, which isn't allowed.",
      'Ada Lovelace switched to WINWORD.',
      'Ada Lovelace asks for access to youtube.com.',
      'Ada Lovelace opened an InPrivate window.',
      'Access to wikipedia.org was approved for the whole class.',
      "Ada Lovelace's agent stopped reporting.",
    ]) {
      expect(find.text(sentence), findsOneWidget, reason: sentence);
    }
    // The summary, in words, and a row for someone off the roster names no
    // id.
    expect(
      find.text('left the session, 3 blocked websites, 12 app switches'),
      findsOneWidget,
    );
    expect(find.text('1 website approved'), findsOneWidget);
    expect(find.textContaining(teacherId), findsNothing);

    // Never the raw event on the page: no kind, no payload.
    for (final raw in const [
      'BlockedUrl',
      'ForegroundChange',
      'UnblockRequest',
      'TamperDetected',
      'UnblockApproved',
      'HeartbeatLost',
      'ManualLeave',
      'processName',
      '"host"',
    ]) {
      expect(find.textContaining(raw), findsNothing, reason: raw);
    }

    // It is in a tooltip, for troubleshooting.
    expect(
      find.byTooltip(
        'BlockedUrl\n'
        '{"url":"https://chat.example.com/c","host":"chat.example.com"}',
      ),
      findsOneWidget,
    );
    expect(
      find.byTooltip('ForegroundChange: 12\nBlockedUrl: 3\nManualLeave: 1'),
      findsOneWidget,
    );

    expect(tester.takeException(), isNull);
  });
}
