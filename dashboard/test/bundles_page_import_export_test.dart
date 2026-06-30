import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/bundles/bundle_file_io.dart';
import 'package:anchor_dashboard/bundles/bundle_format.dart';
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
  Future<MeResponse> me() async =>
      MeResponse(id: 'a1', displayName: 'Admin', role: 'Admin');
}

/// A bundles client backed by an in-memory catalogue so import actually
/// mutates state and the page can re-list it.
class _FakeBundles extends BundlesApi {
  _FakeBundles(this._store) : super(_dummyClient());

  final List<BundleDetail> _store;
  final List<String> createdNames = [];
  final List<String> updatedNames = [];

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async => [
    for (final b in _store)
      BundleSummary(
        id: b.id,
        name: b.name,
        version: b.version,
        isArchived: b.isArchived,
        hasBeenUsed: b.hasBeenUsed,
      ),
  ];

  @override
  Future<BundleDetail> get(String id) async =>
      _store.firstWhere((b) => b.id == id);

  @override
  Future<BundleDetail> create(String name, List<BundleEntry> entries) async {
    createdNames.add(name);
    final detail = BundleDetail(
      id: 'id-${_store.length + 1}',
      name: name,
      version: 1,
      isArchived: false,
      hasBeenUsed: false,
      entries: entries,
    );
    _store.add(detail);
    return detail;
  }

  @override
  Future<BundleDetail> update(
    String id,
    String name,
    List<BundleEntry> entries,
  ) async {
    updatedNames.add(name);
    final i = _store.indexWhere((b) => b.id == id);
    final detail = BundleDetail(
      id: id,
      name: name,
      version: _store[i].version + 1,
      isArchived: false,
      hasBeenUsed: _store[i].hasBeenUsed,
      entries: entries,
    );
    _store[i] = detail;
    return detail;
  }
}

class _FakeFileIo implements BundleFileIo {
  _FakeFileIo({this.pickResult});

  /// What [pickJsonFile] returns; set per test.
  String? pickResult;

  String? downloadedName;
  String? downloadedContents;

  @override
  void downloadJson(String filename, String contents) {
    downloadedName = filename;
    downloadedContents = contents;
  }

  @override
  Future<String?> pickJsonFile() async => pickResult;
}

BundleDetail _existing() => BundleDetail(
  id: 'b1',
  name: 'Exam apps',
  version: 2,
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

Future<void> _pumpPage(
  WidgetTester tester, {
  required _FakeBundles bundles,
  required _FakeFileIo fileIo,
}) async {
  tester.view.physicalSize = const Size(1400, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  await tester.pumpWidget(
    MaterialApp(
      home: BundlesPage(
        bundles: bundles,
        sessions: _FakeSessions(),
        fileIo: fileIo,
      ),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('Export downloads the selected bundle as JSON (#304)', (
    tester,
  ) async {
    final bundles = _FakeBundles([_existing()]);
    final fileIo = _FakeFileIo();
    await _pumpPage(tester, bundles: bundles, fileIo: fileIo);

    // Open the bundle so the editor (and its Export button) renders.
    await tester.tap(find.text('Exam apps'));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('bundles-export-button')));
    await tester.pumpAndSettle();

    expect(fileIo.downloadedName, 'exam-apps.json');
    final parsed = parseBundlesJson(fileIo.downloadedContents!);
    expect(parsed.ok, isTrue, reason: parsed.errors.join('\n'));
    expect(parsed.bundles.single.name, 'Exam apps');
    expect(parsed.bundles.single.entries.single.value, '*.geogebra.org');
  });

  testWidgets('Export all downloads every bundle in one envelope (#304)', (
    tester,
  ) async {
    final bundles = _FakeBundles([_existing()]);
    final fileIo = _FakeFileIo();
    await _pumpPage(tester, bundles: bundles, fileIo: fileIo);

    await tester.tap(find.byKey(const Key('bundles-export-all-button')));
    await tester.pumpAndSettle();

    expect(fileIo.downloadedName, 'bundles.json');
    expect(
      parseBundlesJson(fileIo.downloadedContents!).bundles.single.name,
      'Exam apps',
    );
  });

  testWidgets(
    'Import upserts by name: new -> create, existing -> update (#304)',
    (tester) async {
      final bundles = _FakeBundles([_existing()]);
      final fileIo = _FakeFileIo(
        pickResult: '''
        [
          {"name":"Exam apps","entries":[{"kind":"App","value":"msedge","matchType":"Exact"}]},
          {"name":"Reading list","entries":[{"kind":"Domain","value":"example.com","matchType":"Exact"}]}
        ]
      ''',
      );
      await _pumpPage(tester, bundles: bundles, fileIo: fileIo);

      await tester.tap(find.byKey(const Key('bundles-import-button')));
      await tester.pumpAndSettle();

      expect(bundles.updatedNames, ['Exam apps']);
      expect(bundles.createdNames, ['Reading list']);
      // A summary snackbar and the new row both confirm the result.
      expect(find.textContaining('1 created, 1 updated'), findsOneWidget);
      expect(find.text('Reading list'), findsOneWidget);
    },
  );

  testWidgets('Import rejects an invalid file with an error dialog (#304)', (
    tester,
  ) async {
    final bundles = _FakeBundles([_existing()]);
    final fileIo = _FakeFileIo(pickResult: '{ not valid json');
    await _pumpPage(tester, bundles: bundles, fileIo: fileIo);

    await tester.tap(find.byKey(const Key('bundles-import-button')));
    await tester.pumpAndSettle();

    expect(find.text('Import rejected'), findsOneWidget);
    expect(find.textContaining('not valid JSON'), findsOneWidget);
    // Nothing was written.
    expect(bundles.createdNames, isEmpty);
    expect(bundles.updatedNames, isEmpty);
  });
}
