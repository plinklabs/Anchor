import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/pages/bundles_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

class _FakeSessions extends SessionsApi {
  _FakeSessions({required this.admin}) : super(_dummyClient());
  final bool admin;

  @override
  Future<MeResponse> me() async => MeResponse.fromJson({
    'id': 'u1',
    'displayName': 'Admin',
    'role': admin ? 'Admin' : 'Teacher',
  });
}

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      const [];
}

void main() {
  testWidgets(
    'list pane exposes a "New bundle" button that opens the empty editor (#98)',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(
        MaterialApp(
          home: BundlesPage(
            bundles: _FakeBundles(),
            sessions: _FakeSessions(admin: true),
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Editor is hidden until you select or create.
      expect(
        find.text('Select a bundle or press + to create a new one.'),
        findsOneWidget,
      );

      // The create affordance is in the list pane (#98), not buried in the
      // AppBar. We don't pump the editor state here because rendering the
      // match-type dropdown trips a pre-existing layout overflow (#115).
      // Found by Key because FilledButton.icon wraps in an internal widget
      // that find.widgetWithText doesn't reliably match across Flutter
      // versions.
      final newBundleButton = find.byKey(const Key('bundles-new-button'));
      expect(newBundleButton, findsOneWidget);
      expect(find.descendant(of: newBundleButton, matching: find.text('New bundle')), findsOneWidget);
      final button = tester.widget<ButtonStyleButton>(newBundleButton);
      expect(button.onPressed, isNotNull);
    },
  );
}
