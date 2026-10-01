import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support/e2e_binding.dart';

// Real-app e2e for the Dutch dashboard (#321, #371). Every other e2e pins the
// app to English (ensureE2eBinding's default), so this is the one that proves
// the Dutch catalogue renders in a real browser with the real fonts: it boots
// AnchorDashboard with a Belgian-Dutch browser language, walks sign-in into the
// shell, and checks the Dutch copy shows, no English leaks through, and the
// longer Dutch strings don't overflow. On the en-US CI runner it also proves
// the pin itself works, since only the pin can make that browser render Dutch.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

class _FakeAuth implements MsalAuthService {
  @override
  Future<void> initialize() async {}
  @override
  Future<AccountInfo?> signIn() async => const AccountInfo(
    homeAccountId: 'home-1',
    username: 'leerkracht@school.example',
    displayName: 'Mevr. Peeters',
    department: null,
  );
  @override
  Future<void> signOut() async {}
  @override
  Future<String> acquireToken() async => 'fake-token';
  @override
  Future<String> acquireTokenSilent() async => 'fake-token';
  @override
  AccountInfo? currentAccount() => null;
}

class _FakeSessions extends SessionsApi {
  _FakeSessions() : super(_dummyClient());

  @override
  Future<MeResponse> me() async =>
      MeResponse(id: 't1', displayName: 'Mevr. Peeters', role: 'Teacher');

  @override
  Future<List<ClassSummary>> classes() async => [
    ClassSummary(id: 'c1', name: '3A', schoolYear: '2025-2026'),
  ];

  @override
  Future<List<ActiveSession>> activeSessions() async => const [];

  @override
  Future<List<SessionHistoryEntry>> history({
    int limit = 50,
    int offset = 0,
  }) async => const [];
}

class _FakeClasses extends ClassesApi {
  _FakeClasses() : super(_dummyClient());

  @override
  Future<List<String>> schools() async => const [];
}

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      const [];
}

void main() {
  ensureE2eBinding(locales: const <Locale>[Locale('nl', 'BE')]);

  testWidgets(
    'a Dutch browser gets the Dutch login and shell, no English, no overflow '
    '(#321, #371)',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(
        AnchorDashboard(
          tokens: AuthTokenStore(), // no session → redirect to /login
          auth: _FakeAuth(),
          api: _dummyClient(),
          sessions: _FakeSessions(),
          bundles: _FakeBundles(),
          classes: _FakeClasses(),
          apiBaseUrl: Uri.parse('http://localhost'),
        ),
      );
      await tester.pumpAndSettle();

      // nl-BE resolves to the Dutch catalogue (matched on language code).
      expect(find.text('ANCHOR VOOR LEERKRACHTEN'), findsOneWidget);
      expect(find.text('Klaar wanneer je klas dat is.'), findsOneWidget);
      expect(
        find.widgetWithText(ElevatedButton, 'Aanmelden met Microsoft'),
        findsOneWidget,
      );
      expect(find.text('ANCHOR FOR TEACHERS'), findsNothing);
      expect(find.text('Sign in with Microsoft'), findsNothing);

      await tester.tap(find.byKey(const Key('sign-in')));
      await tester.pumpAndSettle();

      // The shell and home stay Dutch past sign-in.
      expect(find.text('01 · START'), findsOneWidget);
      expect(find.text('KLASSEN'), findsOneWidget);
      expect(find.text('Start een focussessie.'), findsOneWidget);
      expect(find.text('Start sessie voor 3A'), findsOneWidget);
      expect(find.text('01 · HOME'), findsNothing);
      expect(find.text('Start a focus session.'), findsNothing);

      // No RenderFlex overflow or other exception with the longer Dutch copy.
      expect(tester.takeException(), isNull);
    },
  );
}
