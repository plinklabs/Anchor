import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
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
      expect(find.text('BlockedUrl'), findsOneWidget);

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
}
