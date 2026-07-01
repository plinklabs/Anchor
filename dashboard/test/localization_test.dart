import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/auth/msal_config.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

// #321 — proves the localization pipeline end to end through the REAL app
// (AnchorDashboard wires the gen-l10n delegates + the system-language
// localeResolutionCallback in main.dart). Each test forces the platform
// language the browser/OS would report and asserts the resolved copy on the
// login page (an unauthenticated boot lands there):
//
//   * Dutch (nl)      → Dutch copy            (the PoC locale)
//   * Dutch-Belgium   → Dutch copy            (regional variant matches on
//                                              language code, nl-BE → nl)
//   * French (fr)     → English copy          (unsupported language falls back
//                                              to en, the source locale)

Widget _app() {
  final tokens = AuthTokenStore();
  final auth = MsalAuthService(
    const MsalConfig(
      tenantId: 'test-tenant',
      clientId: 'test-client',
      apiScope: 'api://test/.default',
    ),
  );
  final api = ApiClient(
    baseUrl: Uri.parse('http://localhost'),
    tokenProvider: () async => tokens.token,
  );
  return AnchorDashboard(
    tokens: tokens,
    auth: auth,
    api: api,
    sessions: SessionsApi(api),
    bundles: BundlesApi(api),
    classes: ClassesApi(api),
    apiBaseUrl: Uri.parse('http://localhost'),
  );
}

Future<void> _pumpUnder(WidgetTester tester, List<Locale> locales) async {
  // Set the platform language BEFORE the first build so the app resolves its
  // locale from it, exactly as it would from the browser/OS at startup.
  tester.platformDispatcher.localesTestValue = locales;
  addTearDown(tester.platformDispatcher.clearLocalesTestValue);
  await tester.pumpWidget(_app());
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('Dutch browser language shows Dutch copy', (tester) async {
    await _pumpUnder(tester, const <Locale>[Locale('nl')]);

    expect(find.text('Aanmelden met Microsoft'), findsOneWidget);
    expect(find.text('Klaar wanneer je klas dat is.'), findsOneWidget);
    // The English source must NOT leak through when Dutch is active.
    expect(find.text('Sign in with Microsoft'), findsNothing);
  });

  testWidgets('a Dutch regional variant (nl-BE) still resolves to Dutch', (
    tester,
  ) async {
    await _pumpUnder(tester, const <Locale>[Locale('nl', 'BE')]);

    expect(find.text('Aanmelden met Microsoft'), findsOneWidget);
  });

  testWidgets('an unsupported language (fr) falls back to English', (
    tester,
  ) async {
    await _pumpUnder(tester, const <Locale>[Locale('fr')]);

    expect(find.text('Sign in with Microsoft'), findsOneWidget);
    expect(find.text('Aanmelden met Microsoft'), findsNothing);
  });
}
