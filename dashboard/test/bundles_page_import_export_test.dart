import 'dart:async';

import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/bundles/bundle_file_io.dart';
import 'package:anchor_dashboard/bundles/bundle_format.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/bundles_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:http/http.dart' as http;
import 'package:plink_design_system/plink_design_system.dart';

// #392: an Import or an Export all that finishes after the admin has left the
// page (Home, or Admins in the admin sub-nav) still reports its outcome, on
// the app's messenger, as #388 does for Save, Archive and Delete. That
// includes one that lands while the page is still animating out. What an
// import could not do can't open as a dialog on a page that is gone, so the
// app's notice gives the outcome, and its Details opens the list from the
// app's root navigator.

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
/// mutates state and the page can re-list it. While a test holds [gate],
/// each request waits on it, so the admin can leave the page mid-import or
/// mid-export, and then throws what the test set, if anything (#392).
class _FakeBundles extends BundlesApi {
  _FakeBundles(this._store) : super(_dummyClient());

  final List<BundleDetail> _store;
  final List<String> createdNames = [];
  final List<String> updatedNames = [];

  Completer<void>? gate;
  Object? listError;
  Object? getError;
  Object? createError;

  Future<void> _wait([Object? error]) async {
    final gate = this.gate;
    if (gate != null) await gate.future;
    if (error != null) throw error;
  }

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async {
    await _wait(listError);
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
    await _wait(getError);
    return _store.firstWhere((b) => b.id == id);
  }

  @override
  Future<BundleDetail> create(String name, List<BundleEntry> entries) async {
    await _wait(createError);
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
    await _wait();
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

  /// While a test holds it, the file dialog stays open until the test
  /// completes it with the file's contents (#392).
  Completer<String?>? pickGate;

  String? downloadedName;
  String? downloadedContents;

  @override
  void downloadJson(String filename, String contents) {
    downloadedName = filename;
    downloadedContents = contents;
  }

  @override
  Future<String?> pickJsonFile() async {
    final pickGate = this.pickGate;
    if (pickGate != null) return pickGate.future;
    return pickResult;
  }
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
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: BundlesPage(
        bundles: bundles,
        sessions: _FakeSessions(),
        fileIo: fileIo,
      ),
    ),
  );
  await tester.pumpAndSettle();
}

/// Pumps the page the way the app routes it (#392, as #388 does): at
/// `/admin/bundles`, in the admin area's shell, in the app shell, whose
/// Scaffold shows the snack bars of MaterialApp's messenger, the app's. `/`
/// stands in for Home, and `/admin/admins` for the admin sub-nav's Admins.
/// Going to either animates the page out, mounted, before it is disposed, as
/// in the app.
Future<GoRouter> _pumpRouted(
  WidgetTester tester, {
  required _FakeBundles bundles,
  required _FakeFileIo fileIo,
}) async {
  tester.view.physicalSize = const Size(1400, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  final router = GoRouter(
    initialLocation: '/admin/bundles',
    routes: [
      ShellRoute(
        builder: (context, state, child) => Scaffold(body: child),
        routes: [
          GoRoute(
            path: '/',
            builder: (context, state) => const Scaffold(body: Text('Home')),
          ),
          ShellRoute(
            builder: (context, state, child) => child,
            routes: [
              GoRoute(
                path: '/admin/bundles',
                builder: (context, state) => BundlesPage(
                  bundles: bundles,
                  sessions: _FakeSessions(),
                  fileIo: fileIo,
                ),
              ),
              GoRoute(
                path: '/admin/admins',
                builder: (context, state) =>
                    const Scaffold(body: Text('Admins')),
              ),
            ],
          ),
        ],
      ),
    ],
  );
  await tester.pumpWidget(
    MaterialApp.router(
      theme: PlinkTheme.paper.copyWith(splashFactory: NoSplash.splashFactory),
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      routerConfig: router,
    ),
  );
  await tester.pumpAndSettle();
  return router;
}

/// A file with one new bundle, Reading list, which the import creates.
const _readingList = '''
  [{"name":"Reading list","entries":[{"kind":"Domain","value":"example.com","matchType":"Exact"}]}]
''';

/// A file that updates Exam apps and creates Reading list.
const _examAppsAndReadingList = '''
  [
    {"name":"Exam apps","entries":[{"kind":"App","value":"msedge","matchType":"Exact"}]},
    {"name":"Reading list","entries":[{"kind":"Domain","value":"example.com","matchType":"Exact"}]}
  ]
''';

const _withOneFailure = 'Imported with 1 failure (0 created, 0 updated)';
const _readingListFailed = '• "Reading list" could not be saved.';
const _importSentence = 'Could not import the bundles. Please try again.';
const _exportSentence = 'Could not export the bundles. Please try again.';

final _importButton = find.byKey(const Key('bundles-import-button'));
final _exportAllButton = find.byKey(const Key('bundles-export-all-button'));
final _details = find.widgetWithText(SnackBarAction, 'Details');
final _closeNotice = find.descendant(
  of: find.byType(SnackBar),
  matching: find.byIcon(Icons.close),
);

/// A snack bar saying [text], on whichever messenger shows it.
Finder _notice(String text) =>
    find.descendant(of: find.byType(SnackBar), matching: find.text(text));

/// [finder] inside the Bundles page, not on the app around it.
Finder _inPage(Finder finder) =>
    find.descendant(of: find.byType(BundlesPage), matching: finder);

/// The failure list's dialog, titled [title], listing [line].
void _expectList(String title, String line) {
  final dialog = find.byType(AlertDialog);
  expect(dialog, findsOneWidget);
  expect(
    find.descendant(of: dialog, matching: find.text(title)),
    findsOneWidget,
  );
  expect(
    find.descendant(of: dialog, matching: find.text(line)),
    findsOneWidget,
  );
}

/// Taps [button] (Import or Export all), with [bundles] holding the backend
/// until the test completes the gate this returns.
Future<Completer<void>> _start(
  WidgetTester tester,
  _FakeBundles bundles,
  Finder button,
) async {
  final gate = bundles.gate = Completer<void>();
  await tester.tap(button);
  await tester.pump();
  return gate;
}

/// Goes to [location] and lets the page animate out: it is gone.
Future<void> _leave(
  WidgetTester tester,
  GoRouter router,
  String location,
) async {
  router.go(location);
  await tester.pumpAndSettle();
  expect(find.byType(BundlesPage), findsNothing);
}

/// Goes to [location] and stops while the page is still animating out: the
/// admin has left it, but it is still mounted.
Future<void> _startLeaving(
  WidgetTester tester,
  GoRouter router,
  String location,
) async {
  router.go(location);
  await tester.pump();
  await tester.pump(const Duration(milliseconds: 100));
  expect(find.byType(BundlesPage), findsOneWidget);
}

/// Lets an answer that landed while the page animates out take effect, and
/// checks the page, still on screen, shows nothing of its own: no dialog and
/// no notice that would go with it. Then lets the page go.
Future<void> _expectNothingOnTheLeavingPage(WidgetTester tester) async {
  await tester.pump();
  await tester.pump(const Duration(milliseconds: 50));
  expect(find.byType(BundlesPage), findsOneWidget);
  expect(find.byType(AlertDialog), findsNothing);
  expect(_inPage(find.byType(SnackBar)), findsNothing);
  await tester.pumpAndSettle();
  expect(find.byType(BundlesPage), findsNothing);
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

  group('an Import or Export all that finishes after the admin left the page '
      'is reported on the app (#392)', () {
    testWidgets(
      'an Import that could not save a bundle, finishing after the admin went '
      'Home: the app\'s notice gives the outcome and stays, and its Details '
      'lists the bundle',
      (tester) async {
        final bundles = _FakeBundles([_existing()])
          ..createError = ApiException(500, 'System.Exception: boom');
        final router = await _pumpRouted(
          tester,
          bundles: bundles,
          fileIo: _FakeFileIo(pickResult: _readingList),
        );
        final gate = await _start(tester, bundles, _importButton);

        await _leave(tester, router, '/');
        gate.complete();
        await tester.pumpAndSettle();

        expect(find.text('Home'), findsOneWidget);
        expect(_notice(_withOneFailure), findsOneWidget);
        expect(find.byType(AlertDialog), findsNothing);
        expect(tester.takeException(), isNull);

        // The admin may be busy on Home by now, so it waits for them.
        await tester.pump(const Duration(seconds: 30));
        expect(_notice(_withOneFailure), findsOneWidget);
        expect(_closeNotice, findsOneWidget);

        await tester.tap(_details);
        await tester.pumpAndSettle();
        _expectList(_withOneFailure, _readingListFailed);
        expect(find.textContaining('System.'), findsNothing);
        expect(find.textContaining('ApiException'), findsNothing);

        await tester.tap(find.widgetWithText(TextButton, 'Close'));
        await tester.pumpAndSettle();
        expect(find.byType(AlertDialog), findsNothing);
        expect(find.text('Home'), findsOneWidget);
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'an Import that could not save a bundle, finishing while the page '
      'animates out to Admins: nothing on the page, and the app\'s notice '
      'lists it',
      (tester) async {
        final bundles = _FakeBundles([_existing()])
          ..createError = ApiException(500, 'System.Exception: boom');
        final router = await _pumpRouted(
          tester,
          bundles: bundles,
          fileIo: _FakeFileIo(pickResult: _readingList),
        );
        final gate = await _start(tester, bundles, _importButton);

        await _startLeaving(tester, router, '/admin/admins');
        gate.complete();
        await _expectNothingOnTheLeavingPage(tester);

        expect(find.text('Admins'), findsOneWidget);
        expect(_notice(_withOneFailure), findsOneWidget);
        await tester.tap(_details);
        await tester.pumpAndSettle();
        _expectList(_withOneFailure, _readingListFailed);
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'an Import that saves every bundle after the admin went Home: the '
      'app\'s notice gives the count, and goes after a moment',
      (tester) async {
        final bundles = _FakeBundles([_existing()]);
        final router = await _pumpRouted(
          tester,
          bundles: bundles,
          fileIo: _FakeFileIo(pickResult: _examAppsAndReadingList),
        );
        final gate = await _start(tester, bundles, _importButton);

        await _leave(tester, router, '/');
        gate.complete();
        await tester.pumpAndSettle();

        expect(find.text('Home'), findsOneWidget);
        expect(
          _notice('Imported 2 bundles (1 created, 1 updated).'),
          findsOneWidget,
        );
        expect(_details, findsNothing);
        expect(bundles.updatedNames, ['Exam apps']);
        expect(bundles.createdNames, ['Reading list']);
        expect(tester.takeException(), isNull);

        // A success goes after a moment, as it does on the page.
        await tester.pump(const Duration(seconds: 10));
        await tester.pumpAndSettle();
        expect(find.byType(SnackBar), findsNothing);
      },
    );

    testWidgets(
      'an Import that could not read the catalogue after the admin went to '
      'Admins: the app\'s notice says so, and stays until closed',
      (tester) async {
        final bundles = _FakeBundles([_existing()]);
        final router = await _pumpRouted(
          tester,
          bundles: bundles,
          fileIo: _FakeFileIo(pickResult: _readingList),
        );
        bundles.listError = ApiException(500, 'System.Exception: boom');
        final gate = await _start(tester, bundles, _importButton);

        await _leave(tester, router, '/admin/admins');
        gate.complete();
        await tester.pumpAndSettle();

        expect(find.text('Admins'), findsOneWidget);
        expect(_notice(_importSentence), findsOneWidget);
        expect(find.textContaining('System.'), findsNothing);
        await tester.pump(const Duration(seconds: 30));
        expect(_notice(_importSentence), findsOneWidget);
        await tester.tap(_closeNotice);
        await tester.pumpAndSettle();
        expect(find.byType(SnackBar), findsNothing);
        expect(bundles.createdNames, isEmpty);
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'a file picked after the admin left: a valid one is imported and its '
      'count shown, an invalid one shown as rejected, with Details',
      (tester) async {
        final bundles = _FakeBundles([_existing()]);
        final fileIo = _FakeFileIo();
        final router = await _pumpRouted(
          tester,
          bundles: bundles,
          fileIo: fileIo,
        );

        var pick = fileIo.pickGate = Completer<String?>();
        await tester.tap(_importButton);
        await tester.pump();
        await _leave(tester, router, '/');
        pick.complete(_readingList);
        await tester.pumpAndSettle();

        expect(
          _notice('Imported 1 bundle (1 created, 0 updated).'),
          findsOneWidget,
        );
        expect(bundles.createdNames, ['Reading list']);
        expect(tester.takeException(), isNull);
        await tester.pump(const Duration(seconds: 10));
        await tester.pumpAndSettle();

        router.go('/admin/bundles');
        await tester.pumpAndSettle();
        pick = fileIo.pickGate = Completer<String?>();
        await tester.tap(_importButton);
        await tester.pump();
        await _leave(tester, router, '/admin/admins');
        pick.complete('{ not valid json');
        await tester.pumpAndSettle();

        expect(_notice('Import rejected'), findsOneWidget);
        await tester.tap(_details);
        await tester.pumpAndSettle();
        expect(
          find.descendant(
            of: find.byType(AlertDialog),
            matching: find.text('Import rejected'),
          ),
          findsOneWidget,
        );
        expect(find.textContaining('not valid JSON'), findsOneWidget);
        expect(bundles.createdNames, ['Reading list']);
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'an Import that could not save a bundle, finishing after the admin left '
      'and came back: the app\'s notice, its list over the new page, and the '
      'new page left alone',
      (tester) async {
        final bundles = _FakeBundles([_existing()])
          ..createError = ApiException(500, 'System.Exception: boom');
        final router = await _pumpRouted(
          tester,
          bundles: bundles,
          fileIo: _FakeFileIo(pickResult: _readingList),
        );
        final gate = await _start(tester, bundles, _importButton);

        await _leave(tester, router, '/admin/admins');
        // The new page's catalogue loads at once; the import's request still
        // waits on the gate it took.
        bundles.gate = null;
        router.go('/admin/bundles');
        await tester.pumpAndSettle();
        expect(find.text('Exam apps'), findsOneWidget);

        gate.complete();
        await tester.pumpAndSettle();

        expect(_notice(_withOneFailure), findsOneWidget);
        expect(_inPage(find.byType(SnackBar)), findsNothing);
        expect(find.byType(AlertDialog), findsNothing);
        await tester.tap(_details);
        await tester.pumpAndSettle();
        _expectList(_withOneFailure, _readingListFailed);
        await tester.tap(find.widgetWithText(TextButton, 'Close'));
        await tester.pumpAndSettle();
        expect(find.text('Exam apps'), findsOneWidget);
        expect(
          find.text('Select a bundle, or start a new one.'),
          findsOneWidget,
        );
        expect(tester.takeException(), isNull);
      },
    );

    for (final fails in [false, true]) {
      for (final animatingOut in [false, true]) {
        testWidgets(
          'an Export all that ${fails ? 'fails' : 'succeeds'} after the '
          'admin went Home${animatingOut ? ', while the page animates out' : ''}'
          ': the app\'s notice says so',
          (tester) async {
            final bundles = _FakeBundles([_existing()]);
            final fileIo = _FakeFileIo();
            final router = await _pumpRouted(
              tester,
              bundles: bundles,
              fileIo: fileIo,
            );
            if (fails) {
              bundles.getError = http.ClientException(
                'Failed to fetch',
                Uri.parse('http://localhost'),
              );
            }
            final gate = await _start(tester, bundles, _exportAllButton);

            if (animatingOut) {
              await _startLeaving(tester, router, '/');
              gate.complete();
              await _expectNothingOnTheLeavingPage(tester);
            } else {
              await _leave(tester, router, '/');
              gate.complete();
              await tester.pumpAndSettle();
            }

            expect(find.text('Home'), findsOneWidget);
            if (fails) {
              expect(_notice(_exportSentence), findsOneWidget);
              expect(find.textContaining('Failed to fetch'), findsNothing);
              expect(fileIo.downloadedName, isNull);
              // A failure waits for the admin, busy on Home by now.
              await tester.pump(const Duration(seconds: 30));
              expect(_notice(_exportSentence), findsOneWidget);
              expect(_closeNotice, findsOneWidget);
            } else {
              expect(_notice('Exported 1 bundle.'), findsOneWidget);
              expect(fileIo.downloadedName, 'bundles.json');
            }
            expect(tester.takeException(), isNull);
          },
        );
      }
    }

    testWidgets(
      'with the admin still on the page, an Import lists what it could not '
      'save in a dialog, and its and Export all\'s sentences are the page\'s, '
      'as before',
      (tester) async {
        final bundles = _FakeBundles([_existing()])
          ..createError = ApiException(500, 'System.Exception: boom');
        final fileIo = _FakeFileIo(pickResult: _readingList);
        await _pumpRouted(tester, bundles: bundles, fileIo: fileIo);

        // A failure: the dialog on the page, no notice.
        var gate = await _start(tester, bundles, _importButton);
        gate.complete();
        await tester.pumpAndSettle();
        _expectList(_withOneFailure, _readingListFailed);
        expect(find.byType(SnackBar), findsNothing);
        await tester.tap(find.widgetWithText(TextButton, 'Close'));
        await tester.pumpAndSettle();

        // A success: the count, on the page, and the new row.
        bundles.createError = null;
        gate = await _start(tester, bundles, _importButton);
        gate.complete();
        await tester.pumpAndSettle();
        expect(
          _inPage(_notice('Imported 1 bundle (1 created, 0 updated).')),
          findsOneWidget,
        );
        expect(_details, findsNothing);
        expect(find.widgetWithText(InkWell, 'Reading list'), findsOneWidget);
        await tester.pump(const Duration(seconds: 10));
        await tester.pumpAndSettle();
        expect(find.byType(SnackBar), findsNothing);

        // Export all, done and failed: the page's sentence, which goes after
        // a moment.
        gate = await _start(tester, bundles, _exportAllButton);
        gate.complete();
        await tester.pumpAndSettle();
        expect(_inPage(_notice('Exported 2 bundles.')), findsOneWidget);
        expect(fileIo.downloadedName, 'bundles.json');
        await tester.pump(const Duration(seconds: 10));
        await tester.pumpAndSettle();

        bundles.getError = ApiException(500, 'System.Exception: boom');
        gate = await _start(tester, bundles, _exportAllButton);
        gate.complete();
        await tester.pumpAndSettle();
        expect(_inPage(_notice(_exportSentence)), findsOneWidget);
        expect(_closeNotice, findsNothing);
        await tester.pump(const Duration(seconds: 10));
        await tester.pumpAndSettle();
        expect(find.byType(SnackBar), findsNothing);
        expect(tester.takeException(), isNull);
      },
    );
  });
}
