import 'dart:convert';
import 'dart:typed_data';

import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/logs_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart' show ApiException;
import 'package:anchor_dashboard/l10n/app_localizations.dart';
import 'package:anchor_dashboard/l10n/app_localizations_en.dart';
import 'package:anchor_dashboard/logs/log_access_csv.dart';
import 'package:anchor_dashboard/logs/log_file_io.dart';
import 'package:anchor_dashboard/pages/admin_logs_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../integration_test/support/text_contrast.dart';

// Widget tests for Admin → Logs (#566), against a scripted fake LogsApi: the
// uploads list and its filters, the Download button that stays disabled until
// students of one session are selected, the download dialog that won't
// download without a purpose, a reason and (for a fraud suspicion) who asked,
// and the access history with its CSV export. The page in the real app (route,
// sub-nav, real fonts and layout) is covered by integration_test/
// admin_logs_test.dart.

ApiClient _dummyClient() => ApiClient(
  baseUrl: Uri.parse('http://localhost'),
  tokenProvider: () async => null,
);

LogUpload _upload(
  String sessionId,
  String userId,
  String name, {
  String className = '3A',
  DateTime? startedAt,
  bool isComplete = true,
}) {
  final started = startedAt ?? DateTime(2026, 11, 20, 8, 30);
  return LogUpload(
    sessionId: sessionId,
    userId: userId,
    displayName: name,
    className: className,
    sessionStartedAt: started,
    windowFrom: started.subtract(const Duration(minutes: 30)),
    windowTo: started.add(const Duration(minutes: 60)),
    isComplete: isComplete,
    uploadedAt: started.add(const Duration(hours: 2)),
    sizeBytes: 12800,
  );
}

class _Download {
  _Download(
    this.sessionId,
    this.userIds,
    this.purpose,
    this.reason,
    this.requestedBy,
  );

  final String sessionId;
  final List<String> userIds;
  final LogAccessPurpose purpose;
  final String reason;
  final String? requestedBy;
}

class _FakeLogs extends LogsApi {
  _FakeLogs({required this.uploads, List<LogAccessRecord>? history})
    : history = history ?? [],
      super(_dummyClient());

  List<LogUpload> uploads;
  List<LogAccessRecord> history;
  Object? downloadThrows;
  final List<_Download> downloads = [];

  @override
  Future<List<LogUpload>> listUploads() async => uploads;

  @override
  Future<LogDownload> download({
    required String sessionId,
    required List<String> userIds,
    required LogAccessPurpose purpose,
    required String reason,
    String? requestedBy,
  }) async {
    if (downloadThrows != null) throw downloadThrows!;
    downloads.add(_Download(sessionId, userIds, purpose, reason, requestedBy));
    return LogDownload(
      fileName: userIds.length == 1 ? 'one.txt.gz' : 'several.zip',
      mimeType: userIds.length == 1 ? 'application/gzip' : 'application/zip',
      bytes: Uint8List.fromList([1, 2, 3]),
    );
  }

  @override
  Future<List<LogAccessRecord>> accessHistory() async => history;
}

class _FakeFileIo implements LogFileIo {
  final List<(String, Uint8List, String)> saved = [];

  @override
  void save(String fileName, Uint8List bytes, String mimeType) =>
      saved.add((fileName, bytes, mimeType));
}

LogAccessRecord _record({
  LogAccessPurpose purpose = LogAccessPurpose.fraudSuspicion,
  String reason = 'Suspected use of a chat site during the test.',
  String? requestedBy = 'The principal',
}) => LogAccessRecord(
  accessedAt: DateTime.utc(2026, 11, 23, 9, 0),
  adminUserId: 'a1',
  adminDisplayName: 'Ada Admin',
  sessionId: 's1',
  sessionStartedAt: DateTime.utc(2026, 11, 20, 7, 30),
  className: '3A',
  studentUserId: 'u1',
  studentDisplayName: 'Bram',
  purpose: purpose,
  reason: reason,
  requestedBy: requestedBy,
);

Future<void> _pump(
  WidgetTester tester,
  _FakeLogs logs, {
  _FakeFileIo? io,
  ThemeData? theme,
}) async {
  tester.view.physicalSize = const Size(1600, 1200);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
  await tester.pumpWidget(
    MaterialApp(
      theme: theme,
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: AdminLogsPage(logs: logs, fileIo: io ?? _FakeFileIo()),
    ),
  );
  await tester.pumpAndSettle();
}

ElevatedButton _button(WidgetTester tester, String key) =>
    tester.widget<ElevatedButton>(find.byKey(Key(key)));

Future<void> _select(WidgetTester tester, String sessionId, String userId) =>
    tester
        .tap(find.byKey(Key('logs-select-$sessionId-$userId')))
        .then((_) => tester.pumpAndSettle());

Future<void> _openDialog(WidgetTester tester) async {
  await tester.tap(find.byKey(const Key('logs-download')));
  await tester.pumpAndSettle();
}

void main() {
  group('the uploads', () {
    testWidgets('lists every upload with its session, window and state', (
      tester,
    ) async {
      await _pump(
        tester,
        _FakeLogs(
          uploads: [
            _upload('s1', 'u1', 'Bram'),
            _upload('s1', 'u2', 'Chloé', isComplete: false),
          ],
        ),
      );

      expect(find.text('Bram'), findsOneWidget);
      expect(find.text('Chloé'), findsOneWidget);
      expect(
        find.text('3A · 2026-11-20 08:30 · log 08:00–09:30 · complete · 13 KB'),
        findsOneWidget,
      );
      expect(find.textContaining('· partial ·'), findsOneWidget);
    });

    testWidgets('says so when nothing is stored', (tester) async {
      await _pump(tester, _FakeLogs(uploads: []));
      expect(find.byKey(const Key('logs-empty')), findsOneWidget);
    });

    testWidgets('filters by session, date and student', (tester) async {
      await _pump(
        tester,
        _FakeLogs(
          uploads: [
            _upload('s1', 'u1', 'Bram'),
            _upload('s1', 'u2', 'Chloé'),
            _upload(
              's2',
              'u3',
              'Daan',
              className: '4B',
              startedAt: DateTime(2026, 11, 21, 10, 15),
            ),
          ],
        ),
      );

      await tester.enterText(
        find.byKey(const Key('logs-filter-student')),
        'chl',
      );
      await tester.pumpAndSettle();
      expect(find.text('Chloé'), findsOneWidget);
      expect(find.text('Bram'), findsNothing);
      expect(find.text('Daan'), findsNothing);
      await tester.enterText(find.byKey(const Key('logs-filter-student')), '');
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const Key('logs-filter-session')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('4B · 2026-11-21 10:15').last);
      await tester.pumpAndSettle();
      expect(find.text('Daan'), findsOneWidget);
      expect(find.text('Bram'), findsNothing);

      await tester.tap(find.byKey(const Key('logs-filter-session')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('All sessions').last);
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('logs-filter-date')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('2026-11-20').last);
      await tester.pumpAndSettle();
      expect(find.text('Bram'), findsOneWidget);
      expect(find.text('Chloé'), findsOneWidget);
      expect(find.text('Daan'), findsNothing);
    });

    testWidgets(
      'Download stays disabled until students of one session are selected',
      (tester) async {
        await _pump(
          tester,
          _FakeLogs(
            uploads: [
              _upload('s1', 'u1', 'Bram'),
              _upload('s1', 'u2', 'Chloé'),
              _upload('s2', 'u3', 'Daan', className: '4B'),
            ],
          ),
        );

        expect(_button(tester, 'logs-download').onPressed, isNull);
        expect(find.text('No students selected'), findsOneWidget);

        await _select(tester, 's1', 'u1');
        await _select(tester, 's1', 'u2');
        expect(_button(tester, 'logs-download').onPressed, isNotNull);
        expect(find.text('2 students selected'), findsOneWidget);

        // A student of another session: one download is one session.
        await _select(tester, 's2', 'u3');
        expect(_button(tester, 'logs-download').onPressed, isNull);
        expect(
          find.text(
            'Select students of one session to download their logs together.',
          ),
          findsOneWidget,
        );
      },
    );

    testWidgets('a filter change clears the selection', (tester) async {
      await _pump(
        tester,
        _FakeLogs(
          uploads: [_upload('s1', 'u1', 'Bram'), _upload('s1', 'u2', 'Chloé')],
        ),
      );
      await _select(tester, 's1', 'u1');
      expect(_button(tester, 'logs-download').onPressed, isNotNull);

      await tester.enterText(
        find.byKey(const Key('logs-filter-student')),
        'chl',
      );
      await tester.pumpAndSettle();

      expect(_button(tester, 'logs-download').onPressed, isNull);
    });
  });

  // #601: the notices and a partial upload's line were in the 2.79:1 caption
  // grey.
  testWidgets('its notices and every upload line read at WCAG AA on paper', (
    tester,
  ) async {
    await _pump(tester, _FakeLogs(uploads: []), theme: PlinkTheme.paper);
    expectReadableText(tester, shows: ['No logs are stored.']);
    await tester.tap(find.byKey(const Key('logs-tab-history')));
    await tester.pumpAndSettle();
    expectReadableText(tester, shows: ['No logs have been downloaded yet.']);

    await tester.pumpWidget(const SizedBox.shrink());
    await _pump(
      tester,
      _FakeLogs(
        uploads: [
          _upload('s1', 'u1', 'Bram'),
          _upload('s1', 'u2', 'Chloé', isComplete: false),
        ],
      ),
      theme: PlinkTheme.paper,
    );
    expectReadableText(tester, shows: ['· complete ·', '· partial ·']);
    await tester.enterText(
      find.byKey(const Key('logs-filter-student')),
      'nobody',
    );
    await tester.pumpAndSettle();
    expectReadableText(tester, shows: ['No logs match these filters.']);
  });

  group('the download dialog', () {
    testWidgets('won\'t download without a purpose and a reason, and shows '
        'the retention reminder', (tester) async {
      final logs = _FakeLogs(uploads: [_upload('s1', 'u1', 'Bram')]);
      await _pump(tester, logs);
      await _select(tester, 's1', 'u1');
      await _openDialog(tester);

      expect(find.text('Download 1 log'), findsOneWidget);
      expect(
        find.text(
          "Downloaded copies fall outside the backend's 14-day retention. "
          'Delete them once the analysis is done.',
        ),
        findsOneWidget,
      );
      expect(_button(tester, 'log-download-confirm').onPressed, isNull);

      // A reason without a purpose: still disabled.
      await tester.enterText(
        find.byKey(const Key('log-reason')),
        'The agent never joined this session.',
      );
      await tester.pumpAndSettle();
      expect(_button(tester, 'log-download-confirm').onPressed, isNull);

      // A purpose with a reason too short to say anything: still disabled.
      await tester.tap(find.byKey(const Key('log-purpose-technical')));
      await tester.enterText(find.byKey(const Key('log-reason')), 'logs');
      await tester.pumpAndSettle();
      expect(find.text('At least 10 characters.'), findsOneWidget);
      expect(_button(tester, 'log-download-confirm').onPressed, isNull);

      await tester.enterText(
        find.byKey(const Key('log-reason')),
        'The agent never joined this session.',
      );
      await tester.pumpAndSettle();
      expect(_button(tester, 'log-download-confirm').onPressed, isNotNull);
      // Who asked is only asked for a fraud suspicion.
      expect(find.byKey(const Key('log-requested-by')), findsNothing);

      expect(logs.downloads, isEmpty);
    });

    testWidgets('a fraud suspicion also needs who in the leadership asked', (
      tester,
    ) async {
      final logs = _FakeLogs(
        uploads: [_upload('s1', 'u1', 'Bram'), _upload('s1', 'u2', 'Chloé')],
      );
      final io = _FakeFileIo();
      await _pump(tester, logs, io: io);
      await _select(tester, 's1', 'u1');
      await _select(tester, 's1', 'u2');
      await _openDialog(tester);
      expect(find.text('Download 2 logs'), findsOneWidget);

      await tester.tap(find.byKey(const Key('log-purpose-fraud')));
      await tester.enterText(
        find.byKey(const Key('log-reason')),
        '  Suspected use of a chat site during the test.  ',
      );
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('log-requested-by')), findsOneWidget);
      expect(_button(tester, 'log-download-confirm').onPressed, isNull);

      await tester.enterText(find.byKey(const Key('log-requested-by')), '  ');
      await tester.pumpAndSettle();
      expect(_button(tester, 'log-download-confirm').onPressed, isNull);

      await tester.enterText(
        find.byKey(const Key('log-requested-by')),
        'The principal',
      );
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('log-download-confirm')));
      await tester.pumpAndSettle();

      final download = logs.downloads.single;
      expect(download.sessionId, 's1');
      expect(download.userIds, ['u1', 'u2']);
      expect(download.purpose, LogAccessPurpose.fraudSuspicion);
      expect(download.reason, 'Suspected use of a chat site during the test.');
      expect(download.requestedBy, 'The principal');
      expect(io.saved.single.$1, 'several.zip');
      expect(io.saved.single.$3, 'application/zip');
      // Closed, the selection cleared, and the file named.
      expect(find.text('Download 2 logs'), findsNothing);
      expect(find.text('Downloaded several.zip.'), findsOneWidget);
      expect(_button(tester, 'logs-download').onPressed, isNull);
    });

    testWidgets('a failed download stays open with a sentence, saves nothing', (
      tester,
    ) async {
      final logs = _FakeLogs(uploads: [_upload('s1', 'u1', 'Bram')])
        ..downloadThrows = ApiException(500, 'System.Exception: boom');
      final io = _FakeFileIo();
      await _pump(tester, logs, io: io);
      await _select(tester, 's1', 'u1');
      await _openDialog(tester);
      await tester.tap(find.byKey(const Key('log-purpose-technical')));
      await tester.enterText(
        find.byKey(const Key('log-reason')),
        'The agent never joined this session.',
      );
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('log-download-confirm')));
      await tester.pumpAndSettle();

      expect(
        find.text('Could not download the logs. Please try again.'),
        findsOneWidget,
      );
      expect(find.textContaining('System.'), findsNothing);
      expect(io.saved, isEmpty);
      expect(find.text('Download 1 log'), findsOneWidget);
    });
  });

  group('the access history', () {
    testWidgets('lists the records and exports them as CSV', (tester) async {
      final io = _FakeFileIo();
      await _pump(
        tester,
        _FakeLogs(uploads: [_upload('s1', 'u1', 'Bram')], history: [_record()]),
        io: io,
      );

      await tester.tap(find.byKey(const Key('logs-tab-history')));
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('logs-history-row-0')), findsOneWidget);
      expect(
        find.text('Suspected use of a chat site during the test.'),
        findsOneWidget,
      );
      expect(
        find.textContaining('Ada Admin · Suspicion of fraud · requested by '),
        findsOneWidget,
      );

      await tester.tap(find.byKey(const Key('logs-export-csv')));
      await tester.pumpAndSettle();

      final (name, bytes, type) = io.saved.single;
      expect(name, startsWith('anchor-log-access-'));
      expect(name, endsWith('.csv'));
      expect(type, 'text/csv');
      // A UTF-8 byte order mark first, so a spreadsheet reads the accents.
      expect(bytes.take(3), [0xEF, 0xBB, 0xBF]);
      final csv = utf8.decode(bytes.sublist(3));
      expect(csv, startsWith('Downloaded at (UTC),Admin,'));
      expect(csv, contains('The principal'));
    });

    testWidgets('says so when nothing was downloaded, and has nothing to '
        'export', (tester) async {
      await _pump(tester, _FakeLogs(uploads: []));
      await tester.tap(find.byKey(const Key('logs-tab-history')));
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('logs-history-empty')), findsOneWidget);
      expect(
        tester
            .widget<OutlinedButton>(find.byKey(const Key('logs-export-csv')))
            .onPressed,
        isNull,
      );
    });
  });

  group('logAccessCsv', () {
    final l10n = AppLocalizationsEn();

    test('one row per record, quoted where needed, times in UTC', () {
      final csv = logAccessCsv([
        _record(reason: 'Said "help", then left.\nTwice.'),
        _record(
          purpose: LogAccessPurpose.technicalDiagnosis,
          reason: 'The agent never joined.',
          requestedBy: null,
        ),
      ], l10n);
      final lines = csv.split('\r\n');

      expect(
        lines.first,
        'Downloaded at (UTC),Admin,Admin ID,Session ID,Session started (UTC),'
        'Class,Student,Student ID,Purpose,Reason,Requested by',
      );
      expect(
        csv,
        contains(
          '2026-11-23T09:00:00.000Z,Ada Admin,a1,s1,2026-11-20T07:30:00.000Z,'
          '3A,Bram,u1,Suspicion of fraud,"Said ""help"", then left.\nTwice.",'
          'The principal\r\n',
        ),
      );
      expect(csv, endsWith('Technical diagnosis,The agent never joined.,\r\n'));
    });

    test("a field a spreadsheet would run as a formula doesn't run", () {
      final csv = logAccessCsv([
        _record(reason: '=HYPERLINK("http://x")', requestedBy: '@leader'),
      ], l10n);

      expect(csv, contains(',"\'=HYPERLINK(""http://x"")",\'@leader\r\n'));
    });
  });
}
