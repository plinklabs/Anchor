import 'dart:convert';
import 'dart:typed_data';

import 'api_client.dart';
import 'sessions_api.dart' show ApiException;

/// Why an admin downloads a student's log (#566): the two uses the privacy
/// policy (#565) allows. [wire] is the backend's enum value.
enum LogAccessPurpose {
  technicalDiagnosis('TechnicalDiagnosis'),
  fraudSuspicion('FraudSuspicion');

  const LogAccessPurpose(this.wire);

  final String wire;

  static LogAccessPurpose fromWire(String value) =>
      values.firstWhere((p) => p.wire == value);
}

/// One student's stored agent log of one session (#446), as the admin
/// Logs page lists it (#566): metadata only, the log itself is downloaded
/// with a stated purpose.
class LogUpload {
  LogUpload({
    required this.sessionId,
    required this.userId,
    required this.displayName,
    required this.className,
    required this.sessionStartedAt,
    required this.windowFrom,
    required this.windowTo,
    required this.isComplete,
    required this.uploadedAt,
    required this.sizeBytes,
  });

  final String sessionId;
  final String userId;
  final String displayName;
  final String className;
  final DateTime sessionStartedAt;
  final DateTime windowFrom;
  final DateTime windowTo;

  /// Whether it holds the whole window: the session had ended and its window
  /// had closed when the agent cut it.
  final bool isComplete;
  final DateTime uploadedAt;
  final int sizeBytes;

  factory LogUpload.fromJson(Map<String, dynamic> json) => LogUpload(
    sessionId: json['sessionId'] as String,
    userId: json['userId'] as String,
    displayName: json['displayName'] as String,
    className: json['className'] as String,
    sessionStartedAt: DateTime.parse(json['sessionStartedAt'] as String),
    windowFrom: DateTime.parse(json['windowFrom'] as String),
    windowTo: DateTime.parse(json['windowTo'] as String),
    isComplete: json['isComplete'] as bool,
    uploadedAt: DateTime.parse(json['uploadedAt'] as String),
    sizeBytes: (json['sizeBytes'] as num).toInt(),
  );
}

/// One admin's download of one student's log (#566), as the access history
/// lists it.
class LogAccessRecord {
  LogAccessRecord({
    required this.accessedAt,
    required this.adminUserId,
    required this.adminDisplayName,
    required this.sessionId,
    required this.sessionStartedAt,
    required this.className,
    required this.studentUserId,
    required this.studentDisplayName,
    required this.purpose,
    required this.reason,
    this.requestedBy,
  });

  final DateTime accessedAt;
  final String adminUserId;
  final String adminDisplayName;
  final String sessionId;
  final DateTime sessionStartedAt;
  final String className;
  final String studentUserId;
  final String studentDisplayName;
  final LogAccessPurpose purpose;
  final String reason;
  final String? requestedBy;

  factory LogAccessRecord.fromJson(Map<String, dynamic> json) =>
      LogAccessRecord(
        accessedAt: DateTime.parse(json['accessedAt'] as String),
        adminUserId: json['adminUserId'] as String,
        adminDisplayName: json['adminDisplayName'] as String,
        sessionId: json['sessionId'] as String,
        sessionStartedAt: DateTime.parse(json['sessionStartedAt'] as String),
        className: json['className'] as String,
        studentUserId: json['studentUserId'] as String,
        studentDisplayName: json['studentDisplayName'] as String,
        purpose: LogAccessPurpose.fromWire(json['purpose'] as String),
        reason: json['reason'] as String,
        requestedBy: json['requestedBy'] as String?,
      );
}

/// A downloaded log file: what to save it as, and its bytes.
class LogDownload {
  const LogDownload({
    required this.fileName,
    required this.mimeType,
    required this.bytes,
  });

  final String fileName;
  final String mimeType;
  final Uint8List bytes;
}

/// Client for Admin → Logs (#566), all admin-gated: the stored log uploads,
/// the recorded download, and the access history.
class LogsApi {
  LogsApi(this._client);
  final ApiClient _client;

  /// Every stored upload, newest first.
  Future<List<LogUpload>> listUploads() async {
    final res = await _client.get('admin/log-uploads');
    _ensureOk(res);
    return [
      for (final e in jsonDecode(res.body) as List<dynamic>)
        LogUpload.fromJson(e as Map<String, dynamic>),
    ];
  }

  /// The logs of [userIds] in session [sessionId]: one student's as the
  /// gzipped text (`.txt.gz`), several as one `.zip`. The backend records the
  /// download, with [purpose], [reason] and [requestedBy], before it sends
  /// anything, and refuses it without them.
  Future<LogDownload> download({
    required String sessionId,
    required List<String> userIds,
    required LogAccessPurpose purpose,
    required String reason,
    String? requestedBy,
  }) async {
    final res = await _client.post(
      'admin/log-uploads/download',
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({
        'sessionId': sessionId,
        'userIds': userIds,
        'purpose': purpose.wire,
        'reason': reason,
        'requestedBy': ?requestedBy,
      }),
    );
    _ensureOk(res);
    final single = userIds.length == 1;
    return LogDownload(
      // The backend names the file the same way; computed here because a
      // cross-origin response hides its Content-Disposition from the page.
      fileName: single
          ? 'anchor-agent-log-$sessionId-${userIds.single}.txt.gz'
          : 'anchor-agent-logs-$sessionId.zip',
      mimeType: single ? 'application/gzip' : 'application/zip',
      bytes: res.bodyBytes,
    );
  }

  /// Every recorded download, newest first.
  Future<List<LogAccessRecord>> accessHistory() async {
    final res = await _client.get('admin/log-access');
    _ensureOk(res);
    return [
      for (final e in jsonDecode(res.body) as List<dynamic>)
        LogAccessRecord.fromJson(e as Map<String, dynamic>),
    ];
  }

  void _ensureOk(dynamic res) {
    if (res.statusCode < 200 || res.statusCode >= 300) {
      throw ApiException(res.statusCode, res.body);
    }
  }
}
