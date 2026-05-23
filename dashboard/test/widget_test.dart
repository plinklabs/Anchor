import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/auth/msal_config.dart';
import 'package:anchor_dashboard/main.dart';
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

    await tester.pumpWidget(
      AnchorDashboard(
        tokens: tokens,
        auth: auth,
        api: api,
        sessions: sessions,
        apiBaseUrl: Uri.parse('http://localhost'),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Anchor — Sign in'), findsOneWidget);
  });
}
