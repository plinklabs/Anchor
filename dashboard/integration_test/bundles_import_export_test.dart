import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/bundles/bundle_file_io.dart';
import 'package:anchor_dashboard/bundles/bundle_format.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

// Real-app e2e for bundle import/export (#304): boots the *real*
// AnchorDashboard (real router, real app-bar, real fonts, real navigation)
// wired to fake API subclasses and a fake [BundleFileIo]. The file-IO seam is
// faked because driving a real browser download / OS file-picker dialog is not
// possible from an integration test — that thin platform edge is the one part
// of the flow not covered end-to-end (see the PR's test plan). Everything the
// admin actually sees and triggers — navigating to Bundles, importing a file,
// the result, and exporting — runs against the real composed app.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

class _FakeAuth implements MsalAuthService {
  @override
  Future<void> initialize() async {}
  @override
  Future<AccountInfo?> signIn() async => null;
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
      MeResponse(id: 'a1', displayName: 'Admin', role: 'Admin');
  @override
  Future<List<ClassSummary>> classes() async => const [];
  @override
  Future<List<ActiveSession>> activeSessions() async => const [];
}

class _FakeBundles extends BundlesApi {
  _FakeBundles(this._store) : super(_dummyClient());
  final List<BundleDetail> _store;

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
}

class _FakeFileIo implements BundleFileIo {
  _FakeFileIo({this.pickResult});
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

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets(
    'admin imports a JSON file and the new bundle appears, then exports it '
    '(#304)',
    (tester) async {
      final store = <BundleDetail>[
        BundleDetail(
          id: 'b1',
          name: 'Exam apps',
          version: 1,
          isArchived: false,
          hasBeenUsed: false,
          entries: [
            BundleEntry(
              kind: BundleEntryKind.domain,
              value: '*.geogebra.org',
              matchType: BundleEntryMatchType.wildcard,
            ),
          ],
        ),
      ];
      final fileIo = _FakeFileIo(
        pickResult:
            '{"name":"Reading list","entries":[{"kind":"Domain","value":"example.com","matchType":"Exact"}]}',
      );

      final tokens = AuthTokenStore()
        ..setSession(
          token: 'fake-token',
          account: const AccountInfo(
            homeAccountId: 'home-1',
            username: 'admin@school.example',
            displayName: 'Admin',
            department: null,
          ),
        );

      await tester.pumpWidget(
        AnchorDashboard(
          tokens: tokens,
          auth: _FakeAuth(),
          api: _dummyClient(),
          sessions: _FakeSessions(),
          bundles: _FakeBundles(store),
          classes: ClassesApi(_dummyClient()),
          apiBaseUrl: Uri.parse('http://localhost'),
          bundleFileIo: fileIo,
        ),
      );
      await tester.pumpAndSettle();

      // Real navigation: open the admin area (first sub-tab is Bundles).
      await tester.tap(find.byKey(const Key('nav-admin')));
      await tester.pumpAndSettle();
      expect(find.text('Exam apps'), findsOneWidget);

      // Import the fake file → the new bundle is created and listed.
      await tester.tap(find.byKey(const Key('bundles-import-button')));
      await tester.pumpAndSettle();
      expect(find.textContaining('1 created'), findsOneWidget);
      expect(find.text('Reading list'), findsOneWidget);

      // Open the imported bundle and export it; the seam captures valid JSON.
      await tester.tap(find.text('Reading list'));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('bundles-export-button')));
      await tester.pumpAndSettle();

      expect(fileIo.downloadedName, 'reading-list.json');
      final exported = parseBundlesJson(fileIo.downloadedContents!);
      expect(exported.ok, isTrue, reason: exported.errors.join('\n'));
      expect(exported.bundles.single.name, 'Reading list');
      expect(exported.bundles.single.entries.single.value, 'example.com');
    },
  );
}
