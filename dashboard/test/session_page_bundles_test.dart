import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/session_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

// The dashboard authenticates via MSAL and can't be dev-impersonated, so we
// pump the page with fake API subclasses rather than hitting a real backend
// (see reference_dashboard_widget_test_verify). These fakes resolve
// synchronously so the page settles in a couple of pumps.
ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

class _FakeSessions extends SessionsApi {
  _FakeSessions() : super(_dummyClient());

  final List<List<String>> updateCalls = [];
  List<SessionBundleInfo> currentBundles = const [];

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
    participants: const [],
    bundles: currentBundles,
    grants: const [],
  );

  @override
  Future<List<UnblockRequestSummary>> unblockRequests(String sessionId) async =>
      const [];

  @override
  Future<List<SessionBundleInfo>> updateBundles(
    String sessionId,
    List<String> bundleIds,
  ) async {
    updateCalls.add(bundleIds);
    currentBundles = [
      for (final id in bundleIds) SessionBundleInfo(id: id, name: id),
    ];
    return currentBundles;
  }
}

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async => [
    BundleSummary(
      id: 'b1',
      name: 'Math',
      version: 1,
      isArchived: false,
      hasBeenUsed: false,
    ),
  ];
}

void main() {
  testWidgets(
    'Toggling a bundle chip in the live view calls updateBundles (#93)',
    (tester) async {
      final sessions = _FakeSessions();

      await tester.pumpWidget(
        MaterialApp(
          // NoSplash avoids loading the ink_sparkle fragment shader, which fails
          // to decode under the test engine when a tap triggers a ripple.
          theme: ThemeData(splashFactory: NoSplash.splashFactory),
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: SessionPage(
            sessionId: '11111111-2222-3333-4444-555555555555',
            tokens: AuthTokenStore(),
            sessions: sessions,
            bundles: _FakeBundles(),
            apiBaseUrl: Uri.parse('http://localhost'),
          ),
        ),
      );
      // Settle initState's getSession + bundles.list microtasks. Avoid
      // pumpAndSettle: the real hub client keeps retrying localhost and stalls.
      await tester.pump();
      await tester.pump();

      final chip = find.widgetWithText(FilterChip, 'Math');
      expect(chip, findsOneWidget);

      await tester.tap(chip);
      await tester.pump();
      await tester.pump();

      expect(sessions.updateCalls, [
        ['b1'],
      ]);
    },
  );
}
