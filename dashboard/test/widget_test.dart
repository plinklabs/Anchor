import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  testWidgets('Unauthenticated app starts on the login page', (tester) async {
    final tokens = AuthTokenStore();
    final api = ApiClient(
      baseUrl: Uri.parse('http://localhost'),
      tokens: tokens,
    );

    await tester.pumpWidget(AnchorDashboard(tokens: tokens, api: api));
    await tester.pumpAndSettle();

    expect(find.text('Anchor — Sign in'), findsOneWidget);
  });
}
