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
  _FakeSessions() : super(_dummyClient());

  @override
  Future<MeResponse> me() async => MeResponse.fromJson({
    'id': 'u1',
    'displayName': 'Admin',
    'role': 'Admin',
  });
}

/// Serves a single bundle whose only entry is an *app* with the
/// `SignedPublisher` match type — the longest match-type label, and the one
/// that overflowed the 160px dropdown (#115).
class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  static final _detail = BundleDetail(
    id: 'b1',
    name: 'Exam apps',
    version: 3,
    isArchived: false,
    hasBeenUsed: false,
    entries: [
      BundleEntry(
        kind: BundleEntryKind.app,
        value: 'Contoso.SignedApp',
        matchType: BundleEntryMatchType.signedPublisher,
      ),
    ],
  );

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async => [
    BundleSummary(
      id: _detail.id,
      name: _detail.name,
      version: _detail.version,
      isArchived: _detail.isArchived,
      hasBeenUsed: _detail.hasBeenUsed,
    ),
  ];

  @override
  Future<BundleDetail> get(String id) async => _detail;
}

void main() {
  testWidgets(
    'opening a bundle with a SignedPublisher app entry renders the match-type '
    'dropdown without a layout overflow (#115)',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(
        MaterialApp(
          home: BundlesPage(
            bundles: _FakeBundles(),
            sessions: _FakeSessions(),
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Open the bundle from the list pane — this renders the editor and, with
      // it, the Apps entry row's match-type dropdown. Before the fix the
      // dropdown reserved space for the widest item ("SignedPublisher") inside
      // a 160px box, tripping a "RenderFlex overflowed by 25 pixels" error that
      // pumpAndSettle reports as a test failure.
      await tester.tap(find.text('Exam apps'));
      await tester.pumpAndSettle();

      // The dropdown shows its selected label, confirming the editor rendered
      // the row that used to overflow.
      expect(find.text('SignedPublisher'), findsOneWidget);

      // Belt and braces: assert no exception was captured during layout. A
      // RenderFlex overflow surfaces through FlutterError, which the test
      // binding stashes here.
      expect(tester.takeException(), isNull);
    },
  );
}
