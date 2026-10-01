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
import 'package:http/http.dart' as http;

import 'support/e2e_binding.dart';

// Real-app e2e for bundle import/export (#304): boots the *real*
// AnchorDashboard (real router, real app-bar, real fonts, real navigation)
// wired to fake API subclasses and a fake [BundleFileIo]. The file-IO seam is
// faked because driving a real browser download / OS file-picker dialog is not
// possible from an integration test — that thin platform edge is the one part
// of the flow not covered end-to-end (see the PR's test plan). Everything the
// admin actually sees and triggers — navigating to Bundles, importing a file,
// the result, and exporting — runs against the real composed app. So do the
// page's failure paths: an import that can't save a bundle (#383), and a
// catalogue or a bundle that fails to load (#384).

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

  /// What create throws, when a test sets it (#383).
  Object? createError;

  /// What list and get throw, when a test sets them (#384).
  Object? listError;
  Object? getError;

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async {
    final error = listError;
    if (error != null) throw error;
    return [
      for (final b in _store)
        BundleSummary(
          id: b.id,
          name: b.name,
          version: b.version,
          isArchived: b.isArchived,
          hasBeenUsed: b.hasBeenUsed,
        ),
    ];
  }

  @override
  Future<BundleDetail> get(String id) async {
    final error = getError;
    if (error != null) throw error;
    return _store.firstWhere((b) => b.id == id);
  }

  @override
  Future<BundleDetail> create(String name, List<BundleEntry> entries) async {
    final error = createError;
    if (error != null) throw error;
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
  ensureE2eBinding();

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

  testWidgets(
    'a bundle the import could not save is listed by name, never by the raw '
    'exception (#383)',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final bundles = _FakeBundles(<BundleDetail>[])
        ..createError = ApiException(500, 'System.Exception: boom');
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
          bundles: bundles,
          classes: ClassesApi(_dummyClient()),
          apiBaseUrl: Uri.parse('http://localhost'),
          bundleFileIo: _FakeFileIo(
            pickResult:
                '{"name":"Reading list","entries":[{"kind":"Domain","value":"example.com","matchType":"Exact"}]}',
          ),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('nav-admin')));
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const Key('bundles-import-button')));
      await tester.pumpAndSettle();

      expect(find.textContaining('Imported with 1 failure'), findsOneWidget);
      expect(find.text('• "Reading list" could not be saved.'), findsOneWidget);
      expect(find.textContaining('ApiException'), findsNothing);
      expect(find.textContaining('System.'), findsNothing);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'a catalogue that fails to load and a bundle that fails to open each say '
    'so with a Retry, never "No bundles." or a silent tap (#384)',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final bundles = _FakeBundles(<BundleDetail>[
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
      ])..listError = ApiException(500, 'System.Exception: boom');
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
          bundles: bundles,
          classes: ClassesApi(_dummyClient()),
          apiBaseUrl: Uri.parse('http://localhost'),
          bundleFileIo: _FakeFileIo(),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('nav-admin')));
      await tester.pumpAndSettle();

      // The catalogue didn't load: the list pane says so, not "No bundles.".
      final loadRetry = find.byKey(const Key('bundles-load-retry-button'));
      expect(
        find.text('Could not load bundles. Please try again.'),
        findsOneWidget,
      );
      expect(loadRetry, findsOneWidget);
      expect(find.text('No bundles.'), findsNothing);

      // Retry once the backend answers again: the catalogue loads.
      bundles.listError = null;
      await tester.tap(loadRetry);
      await tester.pumpAndSettle();
      expect(find.text('Exam apps'), findsOneWidget);
      expect(
        find.text('Could not load bundles. Please try again.'),
        findsNothing,
      );

      // Opening a bundle while offline says so where the editor goes, not the
      // select-a-bundle placeholder.
      bundles.getError = http.ClientException(
        'Failed to fetch',
        Uri.parse('http://localhost'),
      );
      await tester.tap(find.text('Exam apps'));
      await tester.pumpAndSettle();
      final openRetry = find.byKey(const Key('bundles-open-retry-button'));
      expect(
        find.text('Could not load this bundle. Please try again.'),
        findsOneWidget,
      );
      expect(openRetry, findsOneWidget);
      expect(find.text('Select a bundle, or start a new one.'), findsNothing);

      // Retry opens it.
      bundles.getError = null;
      await tester.tap(openRetry);
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('bundles-save-button')), findsOneWidget);
      expect(find.text('*.geogebra.org'), findsOneWidget);

      expect(find.textContaining('ApiException'), findsNothing);
      expect(find.textContaining('Failed to fetch'), findsNothing);
      expect(find.textContaining('System.'), findsNothing);
      expect(tester.takeException(), isNull);
    },
  );
}
