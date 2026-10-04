import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/bundles_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:plink_design_system/plink_design_system.dart';

// #384: a failed catalogue load or bundle open on the Bundles page says so.
// Since 79b55eb (#75) both failures went into the editor's error line, which
// the page doesn't draw with nothing selected: the catalogue read "No bundles."
// and a row tap did nothing visible. Now the catalogue's failure shows in the
// list pane and a bundle's in the editor pane, each as the #278 sentence with a
// Retry; a 403 is the calm no-admin-access notice, which a retry can't clear.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

/// An admin; `me()` throws what a test sets.
class _FakeSessions extends SessionsApi {
  _FakeSessions() : super(_dummyClient());

  Object? meError;

  @override
  Future<MeResponse> me() async {
    final error = meError;
    if (error != null) throw error;
    return MeResponse(id: 'a1', displayName: 'Admin', role: 'Admin');
  }
}

BundleDetail _detail(String id, String name, String domain) => BundleDetail(
  id: id,
  name: name,
  version: 2,
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

/// Two bundles in the catalogue; `list()` and `get()` throw what a test sets.
class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  static final _store = [
    _detail('b1', 'Exam apps', '*.geogebra.org'),
    _detail('b2', 'Reading list', '*.example.com'),
  ];

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
}

http.ClientException _offline() =>
    http.ClientException('Failed to fetch', Uri.parse('http://localhost'));

const _listSentence = 'Could not load bundles. Please try again.';
const _oneSentence = 'Could not load this bundle. Please try again.';
const _adminNotice =
    "Your account doesn't have admin access. Ask an administrator to grant it.";
const _placeholder = 'Select a bundle, or start a new one.';

final _loadRetry = find.byKey(const Key('bundles-load-retry-button'));
final _openRetry = find.byKey(const Key('bundles-open-retry-button'));

/// Pumps the page with [setUp] applied to the fakes before the first load.
/// Settling proves there's no spinner left: an indeterminate
/// CircularProgressIndicator never lets pumpAndSettle return.
Future<(_FakeSessions, _FakeBundles)> _pumpPage(
  WidgetTester tester, {
  Object? meError,
  Object? listError,
}) async {
  tester.view.physicalSize = const Size(1400, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  final sessions = _FakeSessions()..meError = meError;
  final bundles = _FakeBundles()..listError = listError;
  await tester.pumpWidget(
    MaterialApp(
      theme: PlinkTheme.paper.copyWith(splashFactory: NoSplash.splashFactory),
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: BundlesPage(bundles: bundles, sessions: sessions),
    ),
  );
  await tester.pumpAndSettle();
  return (sessions, bundles);
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
    'System.',
    '403',
    '500',
  ]) {
    expect(find.textContaining(raw), findsNothing, reason: raw);
  }
}

/// The sentence in the error colour, with a Retry, and nothing else where the
/// catalogue goes: no empty state, no spinner.
void _expectCatalogueFailure(WidgetTester tester) {
  expect(find.text(_listSentence), findsOneWidget);
  expect(_colorOf(tester, _listSentence), _errorColor(tester));
  expect(_loadRetry, findsOneWidget);
  expect(find.text('No bundles.'), findsNothing);
  expect(find.byType(CircularProgressIndicator), findsNothing);
  _expectNoRawError();
}

/// The sentence in the error colour, with a Retry, where the editor goes: not
/// the select-a-bundle placeholder, not a spinner.
void _expectOpenFailure(WidgetTester tester) {
  expect(find.text(_oneSentence), findsOneWidget);
  expect(_colorOf(tester, _oneSentence), _errorColor(tester));
  expect(_openRetry, findsOneWidget);
  expect(find.text(_placeholder), findsNothing);
  expect(find.byType(CircularProgressIndicator), findsNothing);
  _expectNoRawError();
}

Future<void> _tap(WidgetTester tester, Finder finder) async {
  await tester.tap(finder);
  await tester.pumpAndSettle();
}

void main() {
  group('catalogue load (#384)', () {
    testWidgets(
      'list() answering 500 says so with a Retry, not "No bundles."; Retry '
      'loads the catalogue',
      (tester) async {
        final (_, bundles) = await _pumpPage(
          tester,
          listError: ApiException(500, 'System.Exception: boom'),
        );

        _expectCatalogueFailure(tester);

        bundles.listError = null;
        await _tap(tester, _loadRetry);

        expect(find.text('Exam apps'), findsOneWidget);
        expect(find.text('Reading list'), findsOneWidget);
        expect(find.text(_listSentence), findsNothing);
        expect(_loadRetry, findsNothing);
      },
    );

    testWidgets('list() failing offline says so with a Retry', (tester) async {
      await _pumpPage(tester, listError: _offline());

      _expectCatalogueFailure(tester);
    });

    testWidgets(
      'list() answering 403 is the calm no-admin-access notice, with no '
      'Retry and no "No bundles."',
      (tester) async {
        await _pumpPage(tester, listError: ApiException(403, ''));

        expect(find.text(_adminNotice), findsOneWidget);
        expect(_colorOf(tester, _adminNotice), PlinkColors.ink60);
        expect(_loadRetry, findsNothing);
        expect(find.text('No bundles.'), findsNothing);
        expect(find.byType(CircularProgressIndicator), findsNothing);
        _expectNoRawError();
      },
    );

    testWidgets(
      'the admin check (me()) failing says so with a Retry that runs it again',
      (tester) async {
        final (sessions, _) = await _pumpPage(
          tester,
          meError: ApiException(500, 'System.Exception: boom'),
        );

        _expectCatalogueFailure(tester);

        sessions.meError = null;
        await _tap(tester, _loadRetry);

        expect(find.text('Exam apps'), findsOneWidget);
        expect(find.text(_listSentence), findsNothing);
      },
    );

    testWidgets(
      'a reload failing with a bundle open shows in the list pane, above the '
      'rows already loaded, and keeps the editor',
      (tester) async {
        final (_, bundles) = await _pumpPage(tester);
        await _tap(tester, find.text('Exam apps'));
        bundles.listError = _offline();

        // The Show archived toggle reloads the catalogue.
        await _tap(tester, find.byType(Switch));

        expect(find.text(_listSentence), findsOneWidget);
        expect(_colorOf(tester, _listSentence), _errorColor(tester));
        expect(_loadRetry, findsOneWidget);
        expect(find.text('Reading list'), findsOneWidget);
        expect(find.byKey(const Key('bundles-save-button')), findsOneWidget);
        _expectNoRawError();

        bundles.listError = null;
        await _tap(tester, _loadRetry);

        expect(find.text(_listSentence), findsNothing);
        expect(find.byKey(const Key('bundles-save-button')), findsOneWidget);
      },
    );
  });

  group('bundle open (#384)', () {
    testWidgets(
      'get() answering 500 with nothing open says so where the editor goes, '
      'with a Retry that opens the bundle',
      (tester) async {
        final (_, bundles) = await _pumpPage(tester);
        bundles.getError = ApiException(500, 'System.Exception: boom');

        await _tap(tester, find.text('Exam apps'));

        _expectOpenFailure(tester);

        bundles.getError = null;
        await _tap(tester, _openRetry);

        expect(find.text(_oneSentence), findsNothing);
        expect(find.byKey(const Key('bundles-save-button')), findsOneWidget);
        expect(find.text('*.geogebra.org'), findsOneWidget);
      },
    );

    testWidgets('get() failing offline says so with a Retry', (tester) async {
      final (_, bundles) = await _pumpPage(tester);
      bundles.getError = _offline();

      await _tap(tester, find.text('Exam apps'));

      _expectOpenFailure(tester);
    });

    testWidgets(
      'get() answering 403 is the calm no-admin-access notice, with no Retry',
      (tester) async {
        final (_, bundles) = await _pumpPage(tester);
        bundles.getError = ApiException(403, '');

        await _tap(tester, find.text('Exam apps'));

        expect(find.text(_adminNotice), findsOneWidget);
        expect(_colorOf(tester, _adminNotice), PlinkColors.ink60);
        expect(_openRetry, findsNothing);
        expect(find.text(_placeholder), findsNothing);
        expect(find.byType(CircularProgressIndicator), findsNothing);
        _expectNoRawError();
      },
    );

    testWidgets(
      'get() failing with another bundle open says the clicked one did not '
      'open, not under the other bundle\'s editor',
      (tester) async {
        final (_, bundles) = await _pumpPage(tester);
        await _tap(tester, find.text('Exam apps'));
        bundles.getError = ApiException(500, 'System.Exception: boom');

        await _tap(tester, find.text('Reading list'));

        _expectOpenFailure(tester);
        expect(find.byKey(const Key('bundles-save-button')), findsNothing);
        expect(find.text('*.geogebra.org'), findsNothing);

        bundles.getError = null;
        await _tap(tester, _openRetry);

        expect(find.text('*.example.com'), findsOneWidget);
      },
    );
  });
}
