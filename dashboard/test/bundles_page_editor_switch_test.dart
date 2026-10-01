import 'dart:async';

import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/pages/bundles_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:plink_design_system/plink_design_system.dart';

// #385: an error from the editor's own actions (Save and its validation,
// Archive, Delete) belongs to the bundle or draft it was for. Since 79b55eb
// (#75) opening another bundle or starting a new one never cleared it, so A's
// failed Save stayed under B's editor, or under the new draft, as if that one
// had failed. An action on A that answers after the admin has moved on must
// not land on B's editor either, failed or not. A failed catalogue reload
// (#384) is about the list, not a bundle, so it stays where it is.

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

BundleDetail _detail(
  String id,
  String name,
  String domain, {
  bool used = false,
}) => BundleDetail(
  id: id,
  name: name,
  version: 2,
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

/// Three bundles: two never used (so the editor offers Delete) and one a
/// session has used (Archive). Each write waits on [gate] while a test holds
/// it, so the test can switch bundles while the write is in flight, and then
/// throws [writeError] if a test set one.
class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  final store = <BundleDetail>[
    _detail('b1', 'Exam apps', '*.geogebra.org'),
    _detail('b2', 'Reading list', '*.example.com'),
    _detail('b3', 'Lab tools', '*.labs.example', used: true),
  ];

  Object? listError;
  Object? getError;
  Object? writeError;
  Completer<void>? gate;

  Future<void> _write() async {
    final gate = this.gate;
    if (gate != null) await gate.future;
    final error = writeError;
    if (error != null) throw error;
  }

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async {
    final error = listError;
    if (error != null) throw error;
    return [
      for (final b in store)
        if (includeArchived || !b.isArchived)
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
    return store.firstWhere((b) => b.id == id);
  }

  @override
  Future<BundleDetail> create(String name, List<BundleEntry> entries) async {
    await _write();
    final detail = BundleDetail(
      id: 'b${store.length + 1}',
      name: name,
      version: 1,
      isArchived: false,
      hasBeenUsed: false,
      entries: entries,
    );
    store.add(detail);
    return detail;
  }

  @override
  Future<BundleDetail> update(
    String id,
    String name,
    List<BundleEntry> entries,
  ) async {
    await _write();
    final i = store.indexWhere((b) => b.id == id);
    final saved = BundleDetail(
      id: id,
      name: name,
      version: store[i].version + 1,
      isArchived: store[i].isArchived,
      hasBeenUsed: store[i].hasBeenUsed,
      entries: entries,
    );
    store[i] = saved;
    return saved;
  }

  @override
  Future<void> archive(String id) async {
    await _write();
    final i = store.indexWhere((b) => b.id == id);
    final b = store[i];
    store[i] = BundleDetail(
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
    await _write();
    store.removeWhere((b) => b.id == id);
  }
}

const _saveSentence = 'Could not save the bundle. Please try again.';
const _nameTaken =
    'A bundle with that name already exists. Choose another name.';
const _nameRequired = 'Name is required.';
const _deleteSentence = 'Could not delete the bundle. Please try again.';
const _archiveSentence = 'Could not archive the bundle. Please try again.';
const _listSentence = 'Could not load bundles. Please try again.';
const _oneSentence = 'Could not load this bundle. Please try again.';

/// Every line the editor's own actions can leave under the tester.
const _actionErrors = [
  _saveSentence,
  _nameTaken,
  _nameRequired,
  _deleteSentence,
  _archiveSentence,
];

final _saveButton = find.byKey(const Key('bundles-save-button'));
final _newButton = find.byKey(const Key('bundles-new-button'));
final _nameField = find.widgetWithText(TextField, 'Name');
final _loadRetry = find.byKey(const Key('bundles-load-retry-button'));
final _openRetry = find.byKey(const Key('bundles-open-retry-button'));

/// A catalogue row, not the editor's name field showing the same name.
Finder _row(String name) => find.widgetWithText(InkWell, name);

Future<_FakeBundles> _pumpPage(WidgetTester tester) async {
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
      home: BundlesPage(bundles: bundles, sessions: _FakeSessions()),
    ),
  );
  await tester.pumpAndSettle();
  return bundles;
}

Future<void> _tap(WidgetTester tester, Finder finder) async {
  await tester.tap(finder);
  await tester.pumpAndSettle();
}

/// Taps [finder] while a write is in flight. The Save button's spinner never
/// lets pumpAndSettle return, so this pumps until the open (or the dialog's
/// exit) has landed instead.
Future<void> _tapInFlight(WidgetTester tester, Finder finder) async {
  await tester.tap(finder);
  await tester.pump();
  await tester.pump(const Duration(milliseconds: 500));
}

/// Opens the confirm dialog for Delete or Archive and confirms it.
Future<void> _confirm(
  WidgetTester tester,
  String action, {
  bool settle = true,
}) async {
  await _tap(tester, find.widgetWithText(OutlinedButton, action));
  if (settle) {
    await _tap(tester, find.widgetWithText(FilledButton, action));
  } else {
    await _tapInFlight(tester, find.widgetWithText(FilledButton, action));
  }
}

String _editorName(WidgetTester tester) =>
    tester.widget<TextField>(_nameField).controller!.text;

void _expectNoActionError() {
  for (final error in _actionErrors) {
    expect(find.text(error), findsNothing, reason: error);
  }
}

/// The editor holds Reading list (b2), saved state and all.
void _expectReadingListEditor(WidgetTester tester) {
  expect(_editorName(tester), 'Reading list');
  expect(find.text('*.example.com'), findsOneWidget);
  expect(find.text('*.geogebra.org'), findsNothing);
  expect(
    find.descendant(of: _saveButton, matching: find.text('Save')),
    findsOneWidget,
  );
}

/// The editor holds an empty new draft.
void _expectNewDraft(WidgetTester tester) {
  expect(_editorName(tester), '');
  expect(find.text('*.geogebra.org'), findsNothing);
  expect(
    find.descendant(of: _saveButton, matching: find.text('Create')),
    findsOneWidget,
  );
}

void main() {
  group('opening another bundle or a new draft clears the editor error of '
      'the one it leaves (#385)', () {
    final saveFailures = <(String, Object, String)>[
      ('500', ApiException(500, 'System.Exception: boom'), _saveSentence),
      (
        '409',
        ApiException(
          409,
          '{"title":"A bundle with that name already exists."}',
        ),
        _nameTaken,
      ),
    ];

    for (final (status, error, sentence) in saveFailures) {
      testWidgets('a Save on A answering $status is not under B\'s editor', (
        tester,
      ) async {
        final bundles = await _pumpPage(tester);
        await _tap(tester, _row('Exam apps'));
        bundles.writeError = error;
        await _tap(tester, _saveButton);
        expect(find.text(sentence), findsOneWidget);

        await _tap(tester, _row('Reading list'));

        _expectReadingListEditor(tester);
        _expectNoActionError();
      });

      testWidgets('a Save on A answering $status is not on the new draft', (
        tester,
      ) async {
        final bundles = await _pumpPage(tester);
        await _tap(tester, _row('Exam apps'));
        bundles.writeError = error;
        await _tap(tester, _saveButton);
        expect(find.text(sentence), findsOneWidget);

        await _tap(tester, _newButton);

        _expectNewDraft(tester);
        _expectNoActionError();
      });
    }

    testWidgets('a Save on A that fails validation is not under B\'s editor', (
      tester,
    ) async {
      await _pumpPage(tester);
      await _tap(tester, _row('Exam apps'));
      await tester.enterText(_nameField, '');
      await _tap(tester, _saveButton);
      expect(find.text(_nameRequired), findsOneWidget);

      await _tap(tester, _row('Reading list'));

      _expectReadingListEditor(tester);
      _expectNoActionError();
    });

    testWidgets('a Create of a new draft that fails is not under A\'s editor', (
      tester,
    ) async {
      final bundles = await _pumpPage(tester);
      await _tap(tester, _newButton);
      await tester.enterText(_nameField, 'Draft');
      await tester.enterText(
        find.widgetWithText(TextField, 'e.g. *.geogebra.org'),
        'example.org',
      );
      bundles.writeError = ApiException(500, 'System.Exception: boom');
      await _tap(tester, _saveButton);
      expect(find.text(_saveSentence), findsOneWidget);

      await _tap(tester, _row('Exam apps'));

      expect(_editorName(tester), 'Exam apps');
      expect(find.text('*.geogebra.org'), findsOneWidget);
      _expectNoActionError();
    });

    testWidgets('a Delete of A that fails is not under B\'s editor', (
      tester,
    ) async {
      final bundles = await _pumpPage(tester);
      await _tap(tester, _row('Exam apps'));
      bundles.writeError = ApiException(500, 'System.Exception: boom');
      await _confirm(tester, 'Delete');
      expect(find.text(_deleteSentence), findsOneWidget);

      await _tap(tester, _row('Reading list'));

      _expectReadingListEditor(tester);
      _expectNoActionError();
    });

    testWidgets('an Archive that fails is not on the new draft', (
      tester,
    ) async {
      final bundles = await _pumpPage(tester);
      await _tap(tester, _row('Lab tools'));
      bundles.writeError = http.ClientException(
        'Failed to fetch',
        Uri.parse('http://localhost'),
      );
      await _confirm(tester, 'Archive');
      expect(find.text(_archiveSentence), findsOneWidget);

      await _tap(tester, _newButton);

      _expectNewDraft(tester);
      _expectNoActionError();
    });

    testWidgets(
      'a Save on A that fails is not under B\'s editor when B first fails to '
      'open and then opens on Retry',
      (tester) async {
        final bundles = await _pumpPage(tester);
        await _tap(tester, _row('Exam apps'));
        bundles.writeError = ApiException(500, 'System.Exception: boom');
        await _tap(tester, _saveButton);
        expect(find.text(_saveSentence), findsOneWidget);

        bundles.getError = ApiException(500, 'System.Exception: boom');
        await _tap(tester, _row('Reading list'));
        expect(find.text(_oneSentence), findsOneWidget);
        _expectNoActionError();

        bundles.getError = null;
        await _tap(tester, _openRetry);

        _expectReadingListEditor(tester);
        _expectNoActionError();
      },
    );
  });

  testWidgets(
    'a failed catalogue reload stays in the list pane when the editor moves '
    'to another bundle or a new draft (#385)',
    (tester) async {
      final bundles = await _pumpPage(tester);
      await _tap(tester, _row('Exam apps'));
      // The Show archived toggle reloads the catalogue, which fails.
      bundles.listError = http.ClientException(
        'Failed to fetch',
        Uri.parse('http://localhost'),
      );
      await _tap(tester, find.byType(Switch));
      expect(find.text(_listSentence), findsOneWidget);
      bundles.writeError = ApiException(500, 'System.Exception: boom');
      await _tap(tester, _saveButton);
      expect(find.text(_saveSentence), findsOneWidget);

      await _tap(tester, _row('Reading list'));

      _expectReadingListEditor(tester);
      _expectNoActionError();
      expect(find.text(_listSentence), findsOneWidget);
      expect(_loadRetry, findsOneWidget);

      await _tap(tester, _newButton);

      _expectNewDraft(tester);
      expect(find.text(_listSentence), findsOneWidget);
      expect(_loadRetry, findsOneWidget);
    },
  );

  group('an action that answers after the admin moved on leaves the new '
      'editor alone (#385)', () {
    testWidgets('a slow Save on A that fails after B opened is not under B', (
      tester,
    ) async {
      final bundles = await _pumpPage(tester);
      await _tap(tester, _row('Exam apps'));
      final gate = bundles.gate = Completer<void>();
      bundles.writeError = ApiException(500, 'System.Exception: boom');
      await _tapInFlight(tester, _saveButton);

      await _tapInFlight(tester, _row('Reading list'));
      expect(_editorName(tester), 'Reading list');

      gate.complete();
      await tester.pumpAndSettle();

      _expectReadingListEditor(tester);
      _expectNoActionError();
    });

    testWidgets(
      'a slow Save on A that fails after New bundle is not on the draft',
      (tester) async {
        final bundles = await _pumpPage(tester);
        await _tap(tester, _row('Exam apps'));
        final gate = bundles.gate = Completer<void>();
        bundles.writeError = ApiException(
          409,
          '{"title":"A bundle with that name already exists."}',
        );
        await _tapInFlight(tester, _saveButton);

        await _tapInFlight(tester, _newButton);

        gate.complete();
        await tester.pumpAndSettle();

        _expectNewDraft(tester);
        _expectNoActionError();
      },
    );

    testWidgets(
      'a slow Save on A that succeeds after B opened keeps B in the editor, '
      'and the catalogue shows A\'s new version',
      (tester) async {
        final bundles = await _pumpPage(tester);
        await _tap(tester, _row('Exam apps'));
        final gate = bundles.gate = Completer<void>();
        await _tapInFlight(tester, _saveButton);

        await _tapInFlight(tester, _row('Reading list'));

        gate.complete();
        await tester.pumpAndSettle();

        _expectReadingListEditor(tester);
        _expectNoActionError();
        // PlinkBadge prints its label in capitals.
        expect(bundles.store.first.version, 3);
        expect(
          find.descendant(of: _row('Exam apps'), matching: find.text('V3')),
          findsOneWidget,
        );
      },
    );

    testWidgets(
      'a slow Delete of A that succeeds after B opened keeps B in the editor',
      (tester) async {
        final bundles = await _pumpPage(tester);
        await _tap(tester, _row('Exam apps'));
        final gate = bundles.gate = Completer<void>();
        await _confirm(tester, 'Delete', settle: false);

        await _tapInFlight(tester, _row('Reading list'));

        gate.complete();
        await tester.pumpAndSettle();

        _expectReadingListEditor(tester);
        _expectNoActionError();
        expect(_row('Exam apps'), findsNothing);
      },
    );

    testWidgets(
      'a slow Archive that fails after B opened is not under B\'s editor',
      (tester) async {
        final bundles = await _pumpPage(tester);
        await _tap(tester, _row('Lab tools'));
        final gate = bundles.gate = Completer<void>();
        bundles.writeError = ApiException(500, 'System.Exception: boom');
        await _confirm(tester, 'Archive', settle: false);

        await _tapInFlight(tester, _row('Reading list'));

        gate.complete();
        await tester.pumpAndSettle();

        _expectReadingListEditor(tester);
        _expectNoActionError();
      },
    );
  });
}
