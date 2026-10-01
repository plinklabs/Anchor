import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/bundles/bundle_file_io.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/bundles_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:plink_design_system/plink_design_system.dart';

// #383: a failed catalogue request on the Bundles page reads as a sentence,
// never the raw exception (`Save failed: ApiException(409): {"title":…}`).
// The failures an admin can act on get their own sentence: a 409 on Save is a
// name another bundle has, a 409 on Delete a bundle a session has used since
// the list loaded. A 403 is the calm no-admin-access notice, as the teacher
// pages show theirs (#278).

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

/// One bundle in the catalogue; each write throws what a test sets.
class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  Object? getError;
  Object? createError;
  Object? updateError;
  Object? deleteError;

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async => [
    BundleSummary(
      id: 'b1',
      name: 'Exam apps',
      version: 2,
      isArchived: false,
      hasBeenUsed: false,
    ),
  ];

  @override
  Future<BundleDetail> get(String id) async {
    final error = getError;
    if (error != null) throw error;
    return _existing();
  }

  @override
  Future<BundleDetail> create(String name, List<BundleEntry> entries) async {
    final error = createError;
    if (error != null) throw error;
    return _existing();
  }

  @override
  Future<BundleDetail> update(
    String id,
    String name,
    List<BundleEntry> entries,
  ) async {
    final error = updateError;
    if (error != null) throw error;
    return _existing();
  }

  @override
  Future<void> hardDelete(String id) async {
    final error = deleteError;
    if (error != null) throw error;
  }
}

class _FakeFileIo implements BundleFileIo {
  _FakeFileIo({this.pickResult});

  final String? pickResult;

  @override
  void downloadJson(String filename, String contents) {}

  @override
  Future<String?> pickJsonFile() async => pickResult;
}

http.ClientException _offline() =>
    http.ClientException('Failed to fetch', Uri.parse('http://localhost'));

Future<_FakeBundles> _pumpPage(
  WidgetTester tester, {
  String? importFile,
}) async {
  tester.view.physicalSize = const Size(1400, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  final bundles = _FakeBundles();
  await tester.pumpWidget(
    MaterialApp(
      theme: PlinkTheme.paper.copyWith(splashFactory: NoSplash.splashFactory),
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: BundlesPage(
        bundles: bundles,
        sessions: _FakeSessions(),
        fileIo: _FakeFileIo(pickResult: importFile),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return bundles;
}

Future<void> _openExisting(WidgetTester tester) async {
  await tester.tap(find.text('Exam apps'));
  await tester.pumpAndSettle();
}

Future<void> _save(WidgetTester tester) async {
  await tester.tap(find.byKey(const Key('bundles-save-button')));
  await tester.pumpAndSettle();
}

Color? _colorOf(WidgetTester tester, String text) =>
    tester.widget<Text>(find.text(text)).style?.color;

Color _errorColor(WidgetTester tester) =>
    Theme.of(tester.element(find.byType(BundlesPage))).colorScheme.error;

void _expectNoRawError() {
  for (final raw in const [
    'ApiException',
    'Exception',
    'Failed to fetch',
    '{"title"',
    '409',
    '500',
  ]) {
    expect(find.textContaining(raw), findsNothing, reason: raw);
  }
}

void main() {
  testWidgets('Save answering 500 reads as a sentence (#383)', (tester) async {
    final bundles = await _pumpPage(tester);
    await _openExisting(tester);
    bundles.updateError = ApiException(500, 'System.Exception: boom');

    await _save(tester);

    const sentence = 'Could not save the bundle. Please try again.';
    expect(find.text(sentence), findsOneWidget);
    expect(_colorOf(tester, sentence), _errorColor(tester));
    _expectNoRawError();
  });

  testWidgets('Save answering 409 says the name is taken (#383)', (
    tester,
  ) async {
    final bundles = await _pumpPage(tester);
    await _openExisting(tester);
    bundles.updateError = ApiException(
      409,
      '{"title":"A bundle with that name already exists.","status":409}',
    );

    await _save(tester);

    expect(
      find.text('A bundle with that name already exists. Choose another name.'),
      findsOneWidget,
    );
    _expectNoRawError();
  });

  testWidgets('Save answering 403 is the calm no-admin-access notice (#383)', (
    tester,
  ) async {
    final bundles = await _pumpPage(tester);
    await _openExisting(tester);
    bundles.updateError = ApiException(403, '');

    await _save(tester);

    const notice =
        "Your account doesn't have admin access. Ask an administrator to "
        'grant it.';
    expect(find.text(notice), findsOneWidget);
    expect(_colorOf(tester, notice), PlinkColors.ink60);
    _expectNoRawError();
    expect(find.textContaining('403'), findsNothing);
  });

  testWidgets(
    'Delete answering 409 says the bundle has been used and to archive it '
    '(#383)',
    (tester) async {
      final bundles = await _pumpPage(tester);
      await _openExisting(tester);
      bundles.deleteError = ApiException(
        409,
        '{"title":"Bundle has been used in a session and cannot be '
        'hard-deleted."}',
      );

      await tester.tap(find.widgetWithText(OutlinedButton, 'Delete'));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Delete'));
      await tester.pumpAndSettle();

      expect(
        find.text(
          "A session has used this bundle, so it can't be deleted. Archive "
          'it instead.',
        ),
        findsOneWidget,
      );
      _expectNoRawError();
    },
  );

  testWidgets('Export all while offline reads as a sentence (#383)', (
    tester,
  ) async {
    final bundles = await _pumpPage(tester);
    bundles.getError = _offline();

    await tester.tap(find.byKey(const Key('bundles-export-all-button')));
    await tester.pumpAndSettle();

    expect(
      find.text('Could not export the bundles. Please try again.'),
      findsOneWidget,
    );
    _expectNoRawError();
  });

  testWidgets(
    'a bundle the import could not save is listed by name, not by the raw '
    'error (#383)',
    (tester) async {
      final bundles = await _pumpPage(
        tester,
        importFile: '''
          [{"name":"Reading list","entries":[{"kind":"Domain","value":"example.com","matchType":"Exact"}]}]
        ''',
      );
      bundles.createError = ApiException(500, 'System.Exception: boom');

      await tester.tap(find.byKey(const Key('bundles-import-button')));
      await tester.pumpAndSettle();

      expect(find.textContaining('Imported with 1 failure'), findsOneWidget);
      expect(find.text('• "Reading list" could not be saved.'), findsOneWidget);
      _expectNoRawError();
    },
  );
}
