import 'dart:convert';
import 'dart:typed_data';

import 'package:anchor_dashboard/api/api_client.dart';
import 'package:anchor_dashboard/api/auth_token_store.dart';
import 'package:anchor_dashboard/api/bundles_api.dart';
import 'package:anchor_dashboard/api/classes_api.dart';
import 'package:anchor_dashboard/api/logs_api.dart';
import 'package:anchor_dashboard/api/sessions_api.dart';
import 'package:anchor_dashboard/auth/msal_auth_service.dart';
import 'package:anchor_dashboard/logs/log_file_io.dart';
import 'package:anchor_dashboard/main.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support/e2e_binding.dart';

// Real-app e2e for Admin → Logs (#566): boots the actual AnchorDashboard (real
// router, real fonts, real window) and drives the flow an admin takes when a
// student's agent misbehaved or the leadership suspects fraud in a test: open
// Admin, switch to the Logs sub-tab, narrow the uploads to one session, select
// two students, and download their logs through the dialog that asks for the
// purpose, a reason and who asked. Then the access history shows the download
// and exports it as CSV. Exercises the route, the sub-nav entry, the page and
// dialog composition in the real layout (filters, rows, a dialog over the
// shell), so a broken nested route or an overflowing row can't hide behind the
// widget tests.

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
      MeResponse(id: 'a1', displayName: 'Ada Admin', role: 'Admin');

  @override
  Future<List<ClassSummary>> classes() async => const [];

  @override
  Future<List<ActiveSession>> activeSessions() async => const [];

  @override
  Future<List<SessionHistoryEntry>> history({
    int limit = 50,
    int offset = 0,
  }) async => const [];
}

class _FakeBundles extends BundlesApi {
  _FakeBundles() : super(_dummyClient());

  @override
  Future<List<BundleSummary>> list({bool includeArchived = false}) async =>
      const [];
}

LogUpload _upload(
  String sessionId,
  String userId,
  String name,
  String className,
  DateTime startedAt,
) => LogUpload(
  sessionId: sessionId,
  userId: userId,
  displayName: name,
  className: className,
  sessionStartedAt: startedAt,
  windowFrom: startedAt.subtract(const Duration(minutes: 30)),
  windowTo: startedAt.add(const Duration(minutes: 60)),
  isComplete: true,
  uploadedAt: startedAt.add(const Duration(hours: 2)),
  sizeBytes: 48213,
);

/// A backend that keeps the uploads and records each download, as the real
/// one does, in its access history.
class _FakeLogs extends LogsApi {
  _FakeLogs() : super(_dummyClient());

  final List<LogUpload> uploads = [
    _upload(
      's1',
      'u1',
      'Bram Peeters',
      '5 Wetenschappen-Wiskunde',
      DateTime(2026, 11, 20, 8, 30),
    ),
    _upload(
      's1',
      'u2',
      'Chloé Van den Bossche',
      '5 Wetenschappen-Wiskunde',
      DateTime(2026, 11, 20, 8, 30),
    ),
    _upload(
      's1',
      'u3',
      'Daan Janssens',
      '5 Wetenschappen-Wiskunde',
      DateTime(2026, 11, 20, 8, 30),
    ),
    _upload('s2', 'u4', 'Eva Claes', '4B', DateTime(2026, 11, 21, 10, 15)),
  ];
  final List<LogAccessRecord> history = [];

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
    for (final userId in userIds) {
      final u = uploads.firstWhere(
        (u) => u.sessionId == sessionId && u.userId == userId,
      );
      history.insert(
        0,
        LogAccessRecord(
          accessedAt: DateTime.utc(2026, 11, 23, 9),
          adminUserId: 'a1',
          adminDisplayName: 'Ada Admin',
          sessionId: sessionId,
          sessionStartedAt: u.sessionStartedAt,
          className: u.className,
          studentUserId: userId,
          studentDisplayName: u.displayName,
          purpose: purpose,
          reason: reason,
          requestedBy: requestedBy,
        ),
      );
    }
    return LogDownload(
      fileName: 'anchor-agent-logs-$sessionId.zip',
      mimeType: 'application/zip',
      bytes: Uint8List.fromList([0x50, 0x4b, 3, 4]),
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

AnchorDashboard _app(_FakeLogs logs, _FakeFileIo io) {
  final tokens = AuthTokenStore()
    ..setSession(
      token: 'fake-token',
      account: const AccountInfo(
        homeAccountId: 'home-1',
        username: 'ada@school.example',
        displayName: 'Ada Admin',
        department: null,
      ),
    );
  return AnchorDashboard(
    tokens: tokens,
    auth: _FakeAuth(),
    api: _dummyClient(),
    sessions: _FakeSessions(),
    bundles: _FakeBundles(),
    classes: ClassesApi(_dummyClient()),
    logs: logs,
    logFileIo: io,
    apiBaseUrl: Uri.parse('http://localhost'),
  );
}

ButtonStyleButton _button(WidgetTester tester, String key) =>
    tester.widget<ButtonStyleButton>(find.byKey(Key(key)));

void main() {
  ensureE2eBinding();

  testWidgets('admin downloads two students\' logs with a stated reason, and '
      'the access history records it (#566)', (tester) async {
    final logs = _FakeLogs();
    final io = _FakeFileIo();
    await tester.pumpWidget(_app(logs, io));
    await tester.pumpAndSettle();

    // Into the admin area (lands on Bundles), then across to Logs.
    await tester.tap(find.byKey(const Key('nav-admin')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('admin-nav-logs')));
    await tester.pumpAndSettle();

    expect(find.text('Bram Peeters'), findsOneWidget);
    expect(find.text('Eva Claes'), findsOneWidget);
    expect(_button(tester, 'logs-download').onPressed, isNull);
    expect(tester.takeException(), isNull);

    // Narrow to the first session.
    await tester.tap(find.byKey(const Key('logs-filter-session')));
    await tester.pumpAndSettle();
    await tester.tap(
      find.text('5 Wetenschappen-Wiskunde · 2026-11-20 08:30').last,
    );
    await tester.pumpAndSettle();
    expect(find.text('Eva Claes'), findsNothing);
    expect(find.text('Daan Janssens'), findsOneWidget);

    await tester.tap(find.byKey(const Key('logs-select-s1-u1')));
    await tester.tap(find.byKey(const Key('logs-select-s1-u2')));
    await tester.pumpAndSettle();
    expect(find.text('2 students selected'), findsOneWidget);

    await tester.tap(find.byKey(const Key('logs-download')));
    await tester.pumpAndSettle();
    expect(find.text('Download 2 logs'), findsOneWidget);
    expect(find.byKey(const Key('log-retention-reminder')), findsOneWidget);
    expect(_button(tester, 'log-download-confirm').onPressed, isNull);

    // A fraud suspicion: the reason alone isn't enough, who asked is too.
    await tester.tap(find.byKey(const Key('log-purpose-fraud')));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const Key('log-reason')),
      'Suspected use of a chat site during the maths test of 20 November.',
    );
    await tester.pumpAndSettle();
    expect(_button(tester, 'log-download-confirm').onPressed, isNull);
    await tester.enterText(
      find.byKey(const Key('log-requested-by')),
      'The principal',
    );
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);

    await tester.tap(find.byKey(const Key('log-download-confirm')));
    await tester.pumpAndSettle();

    expect(find.text('Download 2 logs'), findsNothing);
    expect(io.saved.single.$1, 'anchor-agent-logs-s1.zip');
    expect(io.saved.single.$3, 'application/zip');
    expect(find.text('Downloaded anchor-agent-logs-s1.zip.'), findsOneWidget);

    // The access history shows both records, and exports them.
    await tester.tap(find.byKey(const Key('logs-tab-history')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('logs-history-row-0')), findsOneWidget);
    expect(find.byKey(const Key('logs-history-row-1')), findsOneWidget);
    expect(
      find.text(
        'Suspected use of a chat site during the maths test of 20 November.',
      ),
      findsNWidgets(2),
    );
    expect(
      find.textContaining('Suspicion of fraud · requested by The principal'),
      findsNWidgets(2),
    );
    expect(tester.takeException(), isNull);

    await tester.tap(find.byKey(const Key('logs-export-csv')));
    await tester.pumpAndSettle();
    final (name, bytes, type) = io.saved.last;
    expect(name, endsWith('.csv'));
    expect(type, 'text/csv');
    final csv = utf8.decode(bytes.sublist(3));
    expect(csv.split('\r\n').where((l) => l.isNotEmpty), hasLength(3));
    expect(csv, contains('Chloé Van den Bossche'));
    expect(tester.takeException(), isNull);
  });
}
