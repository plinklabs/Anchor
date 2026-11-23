import '../api/logs_api.dart';
import '../l10n/app_localizations.dart';

/// The access history (#566) as CSV, to hand to the school's leadership or
/// DPO: one row per recorded download, newest first as listed, times in UTC
/// (ISO 8601). Fields are quoted where they need it, and a field a spreadsheet
/// would run as a formula (starting with `=`, `+`, `-`, `@`, a tab or a
/// carriage return) is prefixed with `'`: a reason is free text. Lines end in
/// CRLF, as RFC 4180 has it.
String logAccessCsv(List<LogAccessRecord> records, AppLocalizations l10n) {
  final rows = <List<String>>[
    [
      l10n.logsCsvAccessedAt,
      l10n.logsCsvAdmin,
      l10n.logsCsvAdminId,
      l10n.logsCsvSessionId,
      l10n.logsCsvSessionStartedAt,
      l10n.logsCsvClass,
      l10n.logsCsvStudent,
      l10n.logsCsvStudentId,
      l10n.logsCsvPurpose,
      l10n.logsCsvReason,
      l10n.logsCsvRequestedBy,
    ],
    for (final r in records)
      [
        r.accessedAt.toUtc().toIso8601String(),
        r.adminDisplayName,
        r.adminUserId,
        r.sessionId,
        r.sessionStartedAt.toUtc().toIso8601String(),
        r.className,
        r.studentDisplayName,
        r.studentUserId,
        logAccessPurposeLabel(r.purpose, l10n),
        r.reason,
        r.requestedBy ?? '',
      ],
  ];
  return '${rows.map((row) => row.map(_field).join(',')).join('\r\n')}\r\n';
}

/// The short, localized name of a download's purpose.
String logAccessPurposeLabel(LogAccessPurpose purpose, AppLocalizations l10n) =>
    switch (purpose) {
      LogAccessPurpose.technicalDiagnosis => l10n.logsPurposeTechnical,
      LogAccessPurpose.fraudSuspicion => l10n.logsPurposeFraud,
    };

String _field(String value) {
  var v = value;
  if (v.isNotEmpty && '=+-@\t\r'.contains(v[0])) v = "'$v";
  if (v.contains(RegExp(r'[",\r\n]'))) v = '"${v.replaceAll('"', '""')}"';
  return v;
}
