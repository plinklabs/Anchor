import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/auth/msal_config.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:plink_design_system/plink_design_system.dart';

// AF3 (#164): the dashboard consumes the design-system Flutter binding rather
// than hand-rolling a theme. This guards the wiring so a regression can't
// quietly fall back to `ColorScheme.fromSeed(Colors.indigo)`: the app must run
// the Plink *paper* theme (the teacher surface is paper-only, never ink) with
// the one Anchor identity layered on top — the deep-indigo product accent.

AnchorDashboard _app() {
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

void main() {
  testWidgets('runs the Plink paper theme — not the old indigo seed scheme', (
    tester,
  ) async {
    await tester.pumpWidget(_app());

    final app = tester.widget<MaterialApp>(find.byType(MaterialApp));
    final theme = app.theme!;

    // Paper, light, hairlines-not-shadows — the binding's signature.
    expect(theme.brightness, Brightness.light);
    expect(theme.scaffoldBackgroundColor, PlinkColors.paper);
    expect(theme.shadowColor, Colors.transparent);

    // Teacher surface is paper-only: no ink/dark theme is offered, so the OS
    // dark-mode setting can never flip the dashboard to ink.
    expect(app.darkTheme, isNull);
  });

  testWidgets(
    'layers the Anchor deep-indigo product accent on the foundations',
    (tester) async {
      await tester.pumpWidget(_app());

      final app = tester.widget<MaterialApp>(find.byType(MaterialApp));
      final accent = app.theme!.extension<PlinkProductAccent>();

      expect(accent, isNotNull);
      expect(
        accent!.accent,
        const Color(0xFF34357A),
      ); // ANCHOR_BRAND.md §2 paper
      // The accent is the brand's, not the spark's — magenta stays primary.
      expect(app.theme!.colorScheme.primary, PlinkColors.magenta);
    },
  );
}
