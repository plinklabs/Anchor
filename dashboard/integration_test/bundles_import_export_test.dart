import 'dart:async';

import 'package:anchor_dashboard/api/admins_api.dart';
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
// page's failure paths: an import that can't save a bundle (#383), a
// catalogue or a bundle that fails to load (#384), a failed Save that must
// stay with its bundle when the admin moves on (#385), and that must still be
// reported, naming its bundle, with the edit there to reopen (#386). And two
// opens that answer out of order, where only the last one asked for may set
// the editor (#387). And a Save, Archive or Delete that fails after the admin
// has left the page for another, which the app still reports (#388).

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

  /// What update throws, when a test sets it, after waiting on [updateGate]
  /// while a test holds it, so the admin can move on mid-Save (#385).
  Object? updateError;
  Completer<void>? updateGate;

  /// What archive and hardDelete throw, when a test sets them, after waiting
  /// on [removeGate] while a test holds it, so the admin can leave the page
  /// mid-action (#388).
  Object? archiveError;
  Object? deleteError;
  Completer<void>? removeGate;

  /// With [holdOpens], each get waits in [heldOpens] for the test to answer
  /// it, by bundle id, so opens can answer out of order (#387).
  bool holdOpens = false;
  final heldOpens = <String, Completer<BundleDetail>>{};

  /// Answers the held open of [id] with the bundle, or fails it with [error].
  void answerOpen(String id, {Object? error}) {
    final held = heldOpens.remove(id)!;
    if (error == null) {
      held.complete(_store.firstWhere((b) => b.id == id));
    } else {
      held.completeError(error);
    }
  }

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
    if (holdOpens) {
      final held = heldOpens[id] = Completer<BundleDetail>();
      return held.future;
    }
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

  @override
  Future<BundleDetail> update(
    String id,
    String name,
    List<BundleEntry> entries,
  ) async {
    final gate = updateGate;
    if (gate != null) await gate.future;
    final error = updateError;
    if (error != null) throw error;
    final i = _store.indexWhere((b) => b.id == id);
    final saved = BundleDetail(
      id: id,
      name: name,
      version: _store[i].version + 1,
      isArchived: _store[i].isArchived,
      hasBeenUsed: _store[i].hasBeenUsed,
      entries: entries,
    );
    _store[i] = saved;
    return saved;
  }

  @override
  Future<void> archive(String id) async {
    final gate = removeGate;
    if (gate != null) await gate.future;
    final error = archiveError;
    if (error != null) throw error;
    final i = _store.indexWhere((b) => b.id == id);
    final b = _store[i];
    _store[i] = BundleDetail(
      id: b.id,
      name: b.name,
      version: b.version,
      isArchived: true,
      hasBeenUsed: b.hasBeenUsed,
      entries: b.entries,
    );
  }

  @override
  Future<void> hardDelete(String id) async {
    final gate = removeGate;
    if (gate != null) await gate.future;
    final error = deleteError;
    if (error != null) throw error;
    _store.removeWhere((b) => b.id == id);
  }
}

/// The Admins sub-page's client, with no admins to list, so the admin can go
/// there from Bundles without a backend (#388).
class _FakeAdmins extends AdminsApi {
  _FakeAdmins() : super(_dummyClient());

  @override
  Future<List<AdminUser>> listAdmins() async => const [];
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

  testWidgets(
    'a failed Save stays with its bundle: opening another bundle or a new '
    'draft, even mid-Save, shows no error (#385)',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      BundleDetail bundle(String id, String name, String domain) =>
          BundleDetail(
            id: id,
            name: name,
            version: 1,
            isArchived: false,
            hasBeenUsed: false,
            entries: [
              BundleEntry(
                kind: BundleEntryKind.domain,
                value: domain,
                matchType: BundleEntryMatchType.wildcard,
              ),
            ],
          );
      final bundles = _FakeBundles(<BundleDetail>[
        bundle('b1', 'Exam apps', '*.geogebra.org'),
        bundle('b2', 'Reading list', '*.example.com'),
      ]);
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

      const nameTaken =
          'A bundle with that name already exists. Choose another name.';
      const saveSentence = 'Could not save the bundle. Please try again.';
      final save = find.byKey(const Key('bundles-save-button'));
      Finder row(String name) => find.widgetWithText(InkWell, name);
      String editorName() => tester
          .widget<TextField>(find.widgetWithText(TextField, 'Name'))
          .controller!
          .text;

      // Save on Exam apps answers 409: the name is taken.
      await tester.tap(row('Exam apps'));
      await tester.pumpAndSettle();
      bundles.updateError = ApiException(
        409,
        '{"title":"A bundle with that name already exists."}',
      );
      await tester.tap(save);
      await tester.pumpAndSettle();
      expect(find.text(nameTaken), findsOneWidget);

      // Opening Reading list leaves that failure behind.
      await tester.tap(row('Reading list'));
      await tester.pumpAndSettle();
      expect(editorName(), 'Reading list');
      expect(find.text('*.example.com'), findsOneWidget);
      expect(find.text(nameTaken), findsNothing);

      // Save on Reading list answers 500; New bundle leaves that behind.
      bundles.updateError = ApiException(500, 'System.Exception: boom');
      await tester.tap(save);
      await tester.pumpAndSettle();
      expect(find.text(saveSentence), findsOneWidget);
      await tester.tap(find.byKey(const Key('bundles-new-button')));
      await tester.pumpAndSettle();
      expect(editorName(), '');
      expect(find.text(saveSentence), findsNothing);

      // A slow Save on Exam apps that fails after the admin opened Reading
      // list doesn't land under Reading list's editor. The Save spinner never
      // settles, so pump while the Save is in flight.
      await tester.tap(row('Exam apps'));
      await tester.pumpAndSettle();
      final gate = bundles.updateGate = Completer<void>();
      await tester.tap(save);
      await tester.pump();
      await tester.tap(row('Reading list'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 300));
      expect(editorName(), 'Reading list');
      gate.complete();
      await tester.pumpAndSettle();

      expect(editorName(), 'Reading list');
      expect(find.text('*.example.com'), findsOneWidget);
      expect(find.text(saveSentence), findsNothing);
      expect(find.text(nameTaken), findsNothing);
      expect(find.textContaining('ApiException'), findsNothing);
      expect(find.textContaining('System.'), findsNothing);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'a Save that fails after the admin opened another bundle is reported '
    'naming its bundle, Reopen puts the edit back, and the notice goes with '
    'the page (#386)',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      BundleDetail bundle(String id, String name, String domain) =>
          BundleDetail(
            id: id,
            name: name,
            version: 1,
            isArchived: false,
            hasBeenUsed: false,
            entries: [
              BundleEntry(
                kind: BundleEntryKind.domain,
                value: domain,
                matchType: BundleEntryMatchType.wildcard,
              ),
            ],
          );
      final bundles = _FakeBundles(<BundleDetail>[
        bundle('b1', 'Exam apps', '*.geogebra.org'),
        bundle('b2', 'Reading list', '*.example.com'),
      ]);
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

      const saveSentence = 'Could not save the bundle. Please try again.';
      final save = find.byKey(const Key('bundles-save-button'));
      final nameField = find.widgetWithText(TextField, 'Name');
      final reopen = find.widgetWithText(SnackBarAction, 'Reopen');
      Finder row(String name) => find.widgetWithText(InkWell, name);
      String editorName() =>
          tester.widget<TextField>(nameField).controller!.text;

      /// Saves the editor's draft, which the backend answers slowly and then
      /// with a 500, and opens Reading list before the answer lands. The Save
      /// spinner never settles, so pump while the Save is in flight.
      Future<void> saveThenOpenReadingList() async {
        final gate = bundles.updateGate = Completer<void>();
        bundles.updateError = ApiException(500, 'System.Exception: boom');
        await tester.tap(save);
        await tester.pump();
        await tester.tap(row('Reading list'));
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 300));
        expect(editorName(), 'Reading list');
        gate.complete();
        await tester.pumpAndSettle();
      }

      // The admin renames Exam apps and saves, then opens Reading list; the
      // Save fails. Reading list's editor shows no error, and a notice names
      // Exam apps.
      await tester.tap(row('Exam apps'));
      await tester.pumpAndSettle();
      await tester.enterText(nameField, 'Exam apps (spring)');
      await saveThenOpenReadingList();

      expect(
        find.text('Could not save "Exam apps". Please try again.'),
        findsOneWidget,
      );
      expect(editorName(), 'Reading list');
      expect(find.text('*.example.com'), findsOneWidget);
      expect(find.text(saveSentence), findsNothing);
      expect(find.textContaining('System.'), findsNothing);

      // Reopen puts the edit back, with the error it would have shown, and
      // it saves once the backend answers.
      await tester.tap(reopen);
      await tester.pumpAndSettle();
      expect(editorName(), 'Exam apps (spring)');
      expect(find.text('*.geogebra.org'), findsOneWidget);
      expect(find.text(saveSentence), findsOneWidget);
      expect(reopen, findsNothing);

      bundles.updateGate = null;
      bundles.updateError = null;
      await tester.tap(save);
      await tester.pumpAndSettle();
      expect(row('Exam apps (spring)'), findsOneWidget);
      expect(find.text(saveSentence), findsNothing);

      // Another late failure, and the admin leaves the page: the notice, and
      // its Reopen, go with it.
      await saveThenOpenReadingList();
      const lateSave = 'Could not save "Exam apps (spring)". Please try again.';
      expect(find.text(lateSave), findsOneWidget);
      await tester.tap(find.byKey(const Key('nav-home')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('bundles-save-button')), findsNothing);
      expect(find.text(lateSave), findsNothing);
      expect(reopen, findsNothing);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'two opens answering out of order leave the editor on the bundle clicked '
    'last, and New bundle keeps its draft over a late open (#387)',
    (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      BundleDetail bundle(String id, String name, String domain) =>
          BundleDetail(
            id: id,
            name: name,
            version: 1,
            isArchived: false,
            hasBeenUsed: false,
            entries: [
              BundleEntry(
                kind: BundleEntryKind.domain,
                value: domain,
                matchType: BundleEntryMatchType.wildcard,
              ),
            ],
          );
      final bundles = _FakeBundles(<BundleDetail>[
        bundle('b1', 'Exam apps', '*.geogebra.org'),
        bundle('b2', 'Reading list', '*.example.com'),
      ]);
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

      const openSentence = 'Could not load this bundle. Please try again.';
      final save = find.byKey(const Key('bundles-save-button'));
      final spinner = find.byType(CircularProgressIndicator);
      Finder row(String name) => find.widgetWithText(InkWell, name);
      String editorName() => tester
          .widget<TextField>(find.widgetWithText(TextField, 'Name'))
          .controller!
          .text;

      /// Taps or answers, then pumps: an open's spinner never settles.
      Future<void> pumpAfter(FutureOr<void> Function() action) async {
        await action();
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 300));
      }

      // The admin clicks Exam apps and then Reading list. Reading list
      // answers first, Exam apps last: the editor stays on Reading list.
      bundles.holdOpens = true;
      await pumpAfter(() => tester.tap(row('Exam apps')));
      await pumpAfter(() => tester.tap(row('Reading list')));
      expect(spinner, findsOneWidget);

      await pumpAfter(() => bundles.answerOpen('b2'));
      expect(editorName(), 'Reading list');
      await pumpAfter(() => bundles.answerOpen('b1'));
      await tester.pumpAndSettle();

      expect(editorName(), 'Reading list');
      expect(find.text('*.example.com'), findsOneWidget);
      expect(find.text('*.geogebra.org'), findsNothing);
      expect(spinner, findsNothing);

      // The admin clicks Exam apps, then New bundle before it answers, and
      // Exam apps fails late: the new draft stays, with no failed open.
      await pumpAfter(() => tester.tap(row('Exam apps')));
      await tester.tap(find.byKey(const Key('bundles-new-button')));
      await tester.pumpAndSettle();
      expect(editorName(), '');
      await tester.enterText(find.widgetWithText(TextField, 'Name'), 'Draft');

      await pumpAfter(
        () => bundles.answerOpen(
          'b1',
          error: http.ClientException(
            'Failed to fetch',
            Uri.parse('http://localhost'),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(editorName(), 'Draft');
      expect(
        find.descendant(of: save, matching: find.text('Create')),
        findsOneWidget,
      );
      expect(find.text('*.geogebra.org'), findsNothing);
      expect(find.text(openSentence), findsNothing);
      expect(find.byKey(const Key('bundles-open-retry-button')), findsNothing);
      expect(find.textContaining('Failed to fetch'), findsNothing);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'a Save, Archive or Delete that fails after the admin left the page is '
    'reported on the app, naming its bundle, with no Reopen (#388)',
    (tester) async {
      BundleDetail bundle(
        String id,
        String name,
        String domain, {
        bool used = false,
      }) => BundleDetail(
        id: id,
        name: name,
        version: 1,
        isArchived: false,
        hasBeenUsed: used,
        entries: [
          BundleEntry(
            kind: BundleEntryKind.domain,
            value: domain,
            matchType: BundleEntryMatchType.wildcard,
          ),
        ],
      );
      final bundles = _FakeBundles(<BundleDetail>[
        bundle('b1', 'Exam apps', '*.geogebra.org'),
        bundle('b2', 'Lab tools', '*.labs.example', used: true),
      ]);
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
          admins: _FakeAdmins(),
          apiBaseUrl: Uri.parse('http://localhost'),
          bundleFileIo: _FakeFileIo(),
        ),
      );
      await tester.pumpAndSettle();

      final save = find.byKey(const Key('bundles-save-button'));
      final nameField = find.widgetWithText(TextField, 'Name');
      final reopen = find.widgetWithText(SnackBarAction, 'Reopen');
      final home = find.byKey(const Key('nav-home'));
      final admins = find.byKey(const Key('admin-nav-admins'));
      Finder row(String name) => find.widgetWithText(InkWell, name);
      Finder notice(String text) =>
          find.descendant(of: find.byType(SnackBar), matching: find.text(text));

      /// Opens the Bundles page, and [name] on it.
      Future<void> open(String name) async {
        await tester.tap(find.byKey(const Key('nav-admin')));
        await tester.pumpAndSettle();
        await tester.tap(row(name));
        await tester.pumpAndSettle();
      }

      /// Starts the confirmed [action] (Archive or Delete), held on the
      /// backend. Its spinner never settles, so pump instead.
      Future<void> confirm(String action) async {
        await tester.tap(find.widgetWithText(OutlinedButton, action));
        await tester.pumpAndSettle();
        await tester.tap(find.widgetWithText(FilledButton, action));
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 300));
      }

      /// Leaves the page through [nav]. With [animatingOut], stops one frame
      /// in: the page is animating out, still mounted, but already left.
      Future<void> leave(Finder nav, {bool animatingOut = false}) async {
        await tester.tap(nav);
        await tester.pump();
        if (animatingOut) {
          expect(save, findsOneWidget);
        } else {
          await tester.pumpAndSettle();
          expect(save, findsNothing);
        }
      }

      /// The app's notice says [text], with no Reopen, on the page the admin
      /// went to. Closing it clears the way for the next one.
      Future<void> expectNoticeThenClose(String text) async {
        expect(save, findsNothing);
        expect(notice(text), findsOneWidget);
        expect(reopen, findsNothing);
        await tester.tap(
          find.descendant(
            of: find.byType(SnackBar),
            matching: find.byIcon(Icons.close),
          ),
        );
        await tester.pumpAndSettle();
        expect(find.byType(SnackBar), findsNothing);
      }

      // The admin renames Exam apps and saves, and goes Home before the
      // backend answers. The Save fails: Home says so, naming Exam apps.
      await open('Exam apps');
      await tester.enterText(nameField, 'Exam apps (spring)');
      var gate = bundles.updateGate = Completer<void>();
      bundles.updateError = ApiException(500, 'System.Exception: boom');
      await tester.tap(save);
      await tester.pump();
      await leave(home);
      gate.complete();
      await tester.pumpAndSettle();
      expect(find.text('01 · HOME'), findsOneWidget);
      await expectNoticeThenClose(
        'Could not save "Exam apps". Please try again.',
      );

      // The same, with the Save failing while the page is still animating
      // out: it doesn't land under the editor of a page the admin has left.
      await open('Exam apps');
      gate = bundles.updateGate = Completer<void>();
      await tester.tap(save);
      await tester.pump();
      await leave(home, animatingOut: true);
      gate.complete();
      await tester.pump();
      await tester.pumpAndSettle();
      await expectNoticeThenClose(
        'Could not save "Exam apps". Please try again.',
      );

      // An Archive of Lab tools fails after the admin went to Admins.
      await open('Lab tools');
      gate = bundles.removeGate = Completer<void>();
      bundles.archiveError = http.ClientException(
        'Failed to fetch',
        Uri.parse('http://localhost'),
      );
      await confirm('Archive');
      await leave(admins);
      gate.complete();
      await tester.pumpAndSettle();
      await expectNoticeThenClose(
        'Could not archive "Lab tools". Please try again.',
      );

      // A Delete of Exam apps answers 409, as a session has used it since,
      // while the page animates out on the way to Admins.
      await open('Exam apps');
      gate = bundles.removeGate = Completer<void>();
      bundles.deleteError = ApiException(409, '');
      await confirm('Delete');
      await leave(admins, animatingOut: true);
      gate.complete();
      await tester.pump();
      await tester.pumpAndSettle();
      await expectNoticeThenClose(
        'Could not delete "Exam apps". A session has used it. Archive it '
        'instead.',
      );

      // A Save that succeeds after the admin went Home says nothing, and the
      // catalogue shows it when they come back.
      await open('Exam apps');
      await tester.enterText(nameField, 'Exam apps (spring)');
      gate = bundles.updateGate = Completer<void>();
      bundles.updateError = null;
      await tester.tap(save);
      await tester.pump();
      await leave(home);
      gate.complete();
      await tester.pumpAndSettle();
      expect(find.byType(SnackBar), findsNothing);
      await tester.tap(find.byKey(const Key('nav-admin')));
      await tester.pumpAndSettle();
      expect(row('Exam apps (spring)'), findsOneWidget);

      expect(find.textContaining('ApiException'), findsNothing);
      expect(find.textContaining('System.'), findsNothing);
      expect(find.textContaining('Failed to fetch'), findsNothing);
      expect(tester.takeException(), isNull);
    },
  );
}
