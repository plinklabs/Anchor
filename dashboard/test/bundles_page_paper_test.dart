import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/pages/bundles_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:plink_design_system/plink_design_system.dart';

// Paper redesign of the bundles editor (AD5, #170). Verifies the brand
// contracts the redesign introduced: a version spec rendered as a mono badge,
// and a single magenta spark — the Save/Create commit is the only ElevatedButton
// on the page (the DS theme paints ElevatedButton in the spark), so "New bundle"
// and the rest stay calm ink.

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
        kind: BundleEntryKind.domain,
        value: '*.geogebra.org',
        matchType: BundleEntryMatchType.wildcard,
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
  Widget host() => MaterialApp(
        theme: PlinkTheme.paper,
        home: BundlesPage(
          bundles: _FakeBundles(),
          sessions: _FakeSessions(),
        ),
      );

  testWidgets(
    'the list pane shows the version as a mono spec chip, not a ListTile subtitle',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(host());
      await tester.pumpAndSettle();

      // The version reads as a mono badge (PlinkBadge upper-cases its text).
      expect(find.widgetWithText(PlinkBadge, 'V3'), findsOneWidget);

      // "New bundle" is a calm ink action — never the spark.
      expect(
        find.widgetWithText(OutlinedButton, 'New bundle'),
        findsOneWidget,
      );
    },
  );

  testWidgets(
    'opening a bundle yields exactly one magenta spark — the Save commit',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(host());
      await tester.pumpAndSettle();

      await tester.tap(find.text('Exam apps'));
      await tester.pumpAndSettle();

      // The editor's Save is the single ElevatedButton on the page (the one
      // spark); "Add", "Check", "Delete/Archive", "New bundle" are all calm.
      final save = find.byKey(const Key('bundles-save-button'));
      expect(save, findsOneWidget);
      expect(find.byType(ElevatedButton), findsOneWidget);
      expect(
        find.descendant(of: save, matching: find.text('Save')),
        findsOneWidget,
      );

      // No layout overflow when the editor renders (guards the #115 budget too).
      expect(tester.takeException(), isNull);
    },
  );
}
