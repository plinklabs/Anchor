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

void main() {
  testWidgets('Unauthenticated app starts on the login page', (tester) async {
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
    final sessions = SessionsApi(api);
    final bundles = BundlesApi(api);
    final classes = ClassesApi(api);

    await tester.pumpWidget(
      AnchorDashboard(
        tokens: tokens,
        auth: auth,
        api: api,
        sessions: sessions,
        bundles: bundles,
        classes: classes,
        apiBaseUrl: Uri.parse('http://localhost'),
      ),
    );
    await tester.pumpAndSettle();

    // The redesigned login page (AD2, #167) is what an unauthenticated boot
    // lands on — its headline and the single primary sign-in action.
    expect(find.byKey(const Key('login-headline')), findsOneWidget);
    expect(find.byKey(const Key('sign-in')), findsOneWidget);
  });
}
