import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../api/logs_api.dart';
import '../l10n/app_localizations.dart';
import '../logs/log_access_csv.dart';
import '../logs/log_file_io.dart';
import '../widgets/api_error_text.dart';
import 'past_session_shared.dart';

/// The shortest reason the backend takes (`LogAccess.MinReasonLength`).
const int logReasonMinLength = 10;

/// Admin-only "Logs" sub-tab (#566), in the paper treatment.
///
/// Lists the agents' stored log uploads (#446) with filters by session, date
/// and student. The admin selects one or more students of one session and
/// downloads their logs as a file; the download dialog asks for the purpose
/// (one of the two the privacy policy allows), a reason and, for a fraud
/// suspicion, who in the school's leadership asked, and the backend records
/// all of it before it sends a byte. A second view lists those records, the
/// access history, and exports them as CSV for the leadership or the DPO. The
/// route already gates the area on `isAdmin`.
class AdminLogsPage extends StatefulWidget {
  const AdminLogsPage({super.key, required this.logs, this.fileIo});

  final LogsApi logs;

  /// Saves a downloaded file. Null in production (the browser's own,
  /// `package:web`, built on first use); tests inject a fake.
  final LogFileIo? fileIo;

  @override
  State<AdminLogsPage> createState() => _AdminLogsPageState();
}

enum _LogsView { uploads, history }

class _AdminLogsPageState extends State<AdminLogsPage> {
  _LogsView _view = _LogsView.uploads;

  List<LogUpload>? _uploads;
  bool _loadingUploads = false;
  ApiErrorMessage? _uploadsError;

  List<LogAccessRecord>? _history;
  bool _loadingHistory = false;
  ApiErrorMessage? _historyError;

  String? _sessionFilter;
  String? _dateFilter;
  final TextEditingController _studentFilter = TextEditingController();

  /// Selected uploads, by [_keyOf].
  final Set<String> _selected = <String>{};

  LogFileIo? _io;
  LogFileIo get _fileIo => _io ??= widget.fileIo ?? createLogFileIo();

  @override
  void initState() {
    super.initState();
    // After the first frame, so Localizations are there for the error texts.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _loadUploads();
    });
  }

  @override
  void dispose() {
    _studentFilter.dispose();
    super.dispose();
  }

  static String _keyOf(LogUpload u) => '${u.sessionId}/${u.userId}';

  Future<void> _loadUploads() async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _loadingUploads = true;
      _uploadsError = null;
    });
    try {
      final list = await widget.logs.listUploads();
      if (!mounted) return;
      setState(() {
        _uploads = list;
        _selected.removeWhere((k) => !list.any((u) => _keyOf(u) == k));
      });
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _uploadsError = describeApiError(
          e,
          generic: l10n.logsLoadError,
          notAuthorized: l10n.apiError403Admin,
        ),
      );
    } finally {
      if (mounted) setState(() => _loadingUploads = false);
    }
  }

  Future<void> _loadHistory() async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _loadingHistory = true;
      _historyError = null;
    });
    try {
      final list = await widget.logs.accessHistory();
      if (!mounted) return;
      setState(() => _history = list);
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _historyError = describeApiError(
          e,
          generic: l10n.logsHistoryLoadError,
          notAuthorized: l10n.apiError403Admin,
        ),
      );
    } finally {
      if (mounted) setState(() => _loadingHistory = false);
    }
  }

  void _showView(_LogsView view) {
    setState(() => _view = view);
    // The history changes with every download: fetched fresh each time.
    if (view == _LogsView.history) _loadHistory();
  }

  List<LogUpload> get _visible {
    final student = _studentFilter.text.trim().toLowerCase();
    return [
      for (final u in _uploads ?? const <LogUpload>[])
        if ((_sessionFilter == null || u.sessionId == _sessionFilter) &&
            (_dateFilter == null ||
                pastFormatDate(u.sessionStartedAt.toLocal()) == _dateFilter) &&
            (student.isEmpty || u.displayName.toLowerCase().contains(student)))
          u,
    ];
  }

  /// A filter changed: what was selected may no longer be shown, so nothing
  /// hidden is downloaded by accident.
  void _filtersChanged(VoidCallback change) {
    setState(() {
      change();
      _selected.clear();
    });
  }

  List<LogUpload> get _selection => [
    for (final u in _uploads ?? const <LogUpload>[])
      if (_selected.contains(_keyOf(u))) u,
  ];

  bool get _selectionInOneSession =>
      _selection.map((u) => u.sessionId).toSet().length == 1;

  Future<void> _download() async {
    final selection = _selection;
    final l10n = AppLocalizations.of(context);
    final messenger = ScaffoldMessenger.of(context);
    final saved = await showDialog<String>(
      context: context,
      builder: (_) => LogDownloadDialog(
        count: selection.length,
        onDownload: (purpose, reason, requestedBy) async {
          final file = await widget.logs.download(
            sessionId: selection.first.sessionId,
            userIds: [for (final u in selection) u.userId],
            purpose: purpose,
            reason: reason,
            requestedBy: requestedBy,
          );
          _fileIo.save(file.fileName, file.bytes, file.mimeType);
          return file.fileName;
        },
      ),
    );
    if (saved == null || !mounted) return;
    setState(() {
      _selected.clear();
      // Stale now: the next look at the history fetches it again.
      _history = null;
    });
    messenger.showSnackBar(SnackBar(content: Text(l10n.logsDownloaded(saved))));
  }

  void _exportCsv() {
    final history = _history;
    if (history == null) return;
    final csv = logAccessCsv(history, AppLocalizations.of(context));
    final today = pastFormatDate(DateTime.now());
    // With a BOM, so a spreadsheet reads the names' accents as UTF-8.
    _fileIo.save(
      'anchor-log-access-$today.csv',
      Uint8List.fromList(utf8.encode('﻿$csv')),
      'text/csv',
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return Scaffold(
      backgroundColor: PlinkColors.paper,
      body: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(
          PlinkSpacing.s6,
          PlinkSpacing.s5,
          PlinkSpacing.s6,
          PlinkSpacing.s8,
        ),
        child: Align(
          alignment: Alignment.centerLeft,
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 880),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(l10n.logsHeader, style: pastMonoLabel(PlinkColors.ink60)),
                const SizedBox(height: PlinkSpacing.s2),
                Text(
                  l10n.logsIntro,
                  style: Theme.of(
                    context,
                  ).textTheme.bodySmall?.copyWith(color: PlinkColors.ink60),
                ),
                const SizedBox(height: PlinkSpacing.s5),
                Align(
                  alignment: Alignment.centerLeft,
                  child: SegmentedButton<_LogsView>(
                    showSelectedIcon: false,
                    segments: [
                      ButtonSegment(
                        value: _LogsView.uploads,
                        label: Text(
                          l10n.logsTabUploads,
                          key: const Key('logs-tab-uploads'),
                        ),
                      ),
                      ButtonSegment(
                        value: _LogsView.history,
                        label: Text(
                          l10n.logsTabHistory,
                          key: const Key('logs-tab-history'),
                        ),
                      ),
                    ],
                    selected: {_view},
                    onSelectionChanged: (s) => _showView(s.single),
                  ),
                ),
                const SizedBox(height: PlinkSpacing.s5),
                if (_view == _LogsView.uploads)
                  ..._buildUploads(l10n)
                else
                  ..._buildHistory(l10n),
              ],
            ),
          ),
        ),
      ),
    );
  }

  // ------- Uploads -------

  List<Widget> _buildUploads(AppLocalizations l10n) {
    final uploads = _uploads;
    if (_uploadsError != null) {
      return [ApiErrorText(_uploadsError!, key: const Key('logs-error'))];
    }
    if (uploads == null) {
      return [
        if (_loadingUploads)
          const Padding(
            padding: EdgeInsets.symmetric(vertical: PlinkSpacing.s6),
            child: Center(child: CircularProgressIndicator()),
          ),
      ];
    }
    if (uploads.isEmpty) {
      return [
        Text(
          l10n.logsEmpty,
          key: const Key('logs-empty'),
          style: pastMonoLabel(PlinkColors.muted),
        ),
      ];
    }
    final visible = _visible;
    final selection = _selection;
    final canDownload = selection.isNotEmpty && _selectionInOneSession;
    return [
      _buildFilters(l10n, uploads),
      const SizedBox(height: PlinkSpacing.s4),
      Wrap(
        spacing: PlinkSpacing.s4,
        runSpacing: PlinkSpacing.s2,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          ElevatedButton.icon(
            key: const Key('logs-download'),
            icon: const Icon(Icons.download, size: 18),
            label: Text(l10n.logsDownload),
            onPressed: canDownload ? _download : null,
          ),
          Text(
            selection.isNotEmpty && !_selectionInOneSession
                ? l10n.logsOneSessionHint
                : l10n.logsSelected(selection.length),
            key: const Key('logs-selection'),
            style: pastMonoSpec(PlinkColors.ink60, PlinkType.textSm),
          ),
        ],
      ),
      const SizedBox(height: PlinkSpacing.s4),
      if (visible.isEmpty)
        Text(
          l10n.logsNoMatch,
          key: const Key('logs-no-match'),
          style: pastMonoLabel(PlinkColors.muted),
        )
      else
        _Framed(
          children: [
            for (final u in visible)
              _UploadRow(
                upload: u,
                selected: _selected.contains(_keyOf(u)),
                onChanged: (on) => setState(() {
                  if (on) {
                    _selected.add(_keyOf(u));
                  } else {
                    _selected.remove(_keyOf(u));
                  }
                }),
              ),
          ],
        ),
    ];
  }

  Widget _buildFilters(AppLocalizations l10n, List<LogUpload> uploads) {
    // One entry per session and per day, newest first, as the list is.
    final sessions = <String, LogUpload>{};
    for (final u in uploads) {
      sessions.putIfAbsent(u.sessionId, () => u);
    }
    final dates = <String>{
      for (final u in uploads) pastFormatDate(u.sessionStartedAt.toLocal()),
    }.toList()..sort((a, b) => b.compareTo(a));

    return Wrap(
      spacing: PlinkSpacing.s4,
      runSpacing: PlinkSpacing.s3,
      crossAxisAlignment: WrapCrossAlignment.end,
      children: [
        SizedBox(
          width: 300,
          child: DropdownButtonFormField<String?>(
            key: const Key('logs-filter-session'),
            initialValue: _sessionFilter,
            isExpanded: true,
            decoration: InputDecoration(
              labelText: l10n.logsFilterSession,
              isDense: true,
            ),
            items: [
              DropdownMenuItem<String?>(
                value: null,
                child: Text(l10n.logsFilterAllSessions),
              ),
              for (final s in sessions.values)
                DropdownMenuItem<String?>(
                  value: s.sessionId,
                  child: Text(
                    _sessionLabel(s),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
            ],
            onChanged: (v) => _filtersChanged(() => _sessionFilter = v),
          ),
        ),
        SizedBox(
          width: 180,
          child: DropdownButtonFormField<String?>(
            key: const Key('logs-filter-date'),
            initialValue: _dateFilter,
            isExpanded: true,
            decoration: InputDecoration(
              labelText: l10n.logsFilterDate,
              isDense: true,
            ),
            items: [
              DropdownMenuItem<String?>(
                value: null,
                child: Text(l10n.logsFilterAllDates),
              ),
              for (final d in dates)
                DropdownMenuItem<String?>(value: d, child: Text(d)),
            ],
            onChanged: (v) => _filtersChanged(() => _dateFilter = v),
          ),
        ),
        SizedBox(
          width: 220,
          child: TextField(
            key: const Key('logs-filter-student'),
            controller: _studentFilter,
            decoration: InputDecoration(
              labelText: l10n.logsFilterStudent,
              isDense: true,
              prefixIcon: const Icon(Icons.search, size: 18),
            ),
            onChanged: (_) => _filtersChanged(() {}),
          ),
        ),
      ],
    );
  }

  // ------- Access history -------

  List<Widget> _buildHistory(AppLocalizations l10n) {
    final history = _history;
    return [
      Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(
            child: Text(
              l10n.logsHistoryIntro,
              style: Theme.of(
                context,
              ).textTheme.bodySmall?.copyWith(color: PlinkColors.ink60),
            ),
          ),
          const SizedBox(width: PlinkSpacing.s4),
          OutlinedButton.icon(
            key: const Key('logs-export-csv'),
            icon: const Icon(Icons.table_view, size: 18),
            label: Text(l10n.logsExportCsv),
            onPressed: history == null || history.isEmpty ? null : _exportCsv,
          ),
        ],
      ),
      const SizedBox(height: PlinkSpacing.s4),
      if (_historyError != null)
        ApiErrorText(_historyError!, key: const Key('logs-history-error'))
      else if (history == null)
        const Padding(
          padding: EdgeInsets.symmetric(vertical: PlinkSpacing.s6),
          child: Center(child: CircularProgressIndicator()),
        )
      else if (history.isEmpty && !_loadingHistory)
        Text(
          l10n.logsHistoryEmpty,
          key: const Key('logs-history-empty'),
          style: pastMonoLabel(PlinkColors.muted),
        )
      else
        _Framed(
          children: [
            for (var i = 0; i < history.length; i++)
              _HistoryRow(key: Key('logs-history-row-$i'), record: history[i]),
          ],
        ),
    ];
  }
}

/// "3A · 2026-11-20 08:30": a session as the filters and rows name it.
String _sessionLabel(LogUpload u) {
  final started = u.sessionStartedAt.toLocal();
  return '${u.className} · ${pastFormatDate(started)} ${pastFormatTime(started)}';
}

/// A hairline-framed list, its rows separated by hairlines.
class _Framed extends StatelessWidget {
  const _Framed({required this.children});

  final List<Widget> children;

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: BoxDecoration(
        border: Border.all(
          color: PlinkColors.hairline,
          width: PlinkBorders.width,
        ),
        borderRadius: BorderRadius.circular(PlinkRadius.base),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          for (var i = 0; i < children.length; i++) ...[
            if (i > 0) const PastHairline(),
            children[i],
          ],
        ],
      ),
    );
  }
}

/// One stored upload: its checkbox, the student, and the session, window,
/// completeness and size on a quiet mono line.
class _UploadRow extends StatelessWidget {
  const _UploadRow({
    required this.upload,
    required this.selected,
    required this.onChanged,
  });

  final LogUpload upload;
  final bool selected;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final u = upload;
    final kb = (u.sizeBytes / 1024).toStringAsFixed(
      u.sizeBytes < 10240 ? 1 : 0,
    );
    final spec = [
      _sessionLabel(u),
      l10n.logsWindow(
        pastFormatTime(u.windowFrom.toLocal()),
        pastFormatTime(u.windowTo.toLocal()),
      ),
      u.isComplete ? l10n.logsComplete : l10n.logsPartial,
      l10n.logsSizeKb(kb),
    ].join(' · ');
    return InkWell(
      key: Key('logs-row-${u.sessionId}-${u.userId}'),
      onTap: () => onChanged(!selected),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(
          PlinkSpacing.s2,
          PlinkSpacing.s2,
          PlinkSpacing.s4,
          PlinkSpacing.s2,
        ),
        child: Row(
          children: [
            Checkbox(
              key: Key('logs-select-${u.sessionId}-${u.userId}'),
              value: selected,
              onChanged: (v) => onChanged(v ?? false),
            ),
            const SizedBox(width: PlinkSpacing.s2),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    u.displayName,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: Theme.of(
                      context,
                    ).textTheme.bodyLarge?.copyWith(color: PlinkColors.ink),
                  ),
                  Text(
                    spec,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: pastMonoSpec(
                      u.isComplete ? PlinkColors.ink60 : PlinkColors.muted,
                      PlinkType.textSm,
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// One recorded download: the student and session, who downloaded it when and
/// why, and at whose request.
class _HistoryRow extends StatelessWidget {
  const _HistoryRow({super.key, required this.record});

  final LogAccessRecord record;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final r = record;
    final at = r.accessedAt.toLocal();
    final started = r.sessionStartedAt.toLocal();
    final who = [
      '${pastFormatDate(at)} ${pastFormatTime(at)}',
      r.adminDisplayName,
      logAccessPurposeLabel(r.purpose, l10n),
      if (r.requestedBy != null) l10n.logsHistoryRequestedBy(r.requestedBy!),
    ].join(' · ');
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        PlinkSpacing.s4,
        PlinkSpacing.s3,
        PlinkSpacing.s4,
        PlinkSpacing.s3,
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            '${r.studentDisplayName} · ${r.className} · '
            '${pastFormatDate(started)} ${pastFormatTime(started)}',
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: Theme.of(
              context,
            ).textTheme.bodyLarge?.copyWith(color: PlinkColors.ink),
          ),
          Text(who, style: pastMonoSpec(PlinkColors.ink60, PlinkType.textSm)),
          const SizedBox(height: PlinkSpacing.s1),
          Text(
            r.reason,
            style: Theme.of(
              context,
            ).textTheme.bodySmall?.copyWith(color: PlinkColors.ink),
          ),
        ],
      ),
    );
  }
}

/// Asks why the logs are downloaded (#566) before it downloads them: the
/// purpose, a reason of at least [logReasonMinLength] characters and, for a
/// fraud suspicion, who in the school's leadership asked. Download stays
/// disabled until those are filled. It reminds the admin that the copies fall
/// outside the backend's retention. [onDownload] downloads and saves the
/// file and returns its name, which the dialog closes with.
class LogDownloadDialog extends StatefulWidget {
  const LogDownloadDialog({
    super.key,
    required this.count,
    required this.onDownload,
  });

  final int count;
  final Future<String> Function(
    LogAccessPurpose purpose,
    String reason,
    String? requestedBy,
  )
  onDownload;

  @override
  State<LogDownloadDialog> createState() => _LogDownloadDialogState();
}

class _LogDownloadDialogState extends State<LogDownloadDialog> {
  LogAccessPurpose? _purpose;
  final TextEditingController _reason = TextEditingController();
  final TextEditingController _requestedBy = TextEditingController();
  bool _busy = false;
  ApiErrorMessage? _error;

  @override
  void dispose() {
    _reason.dispose();
    _requestedBy.dispose();
    super.dispose();
  }

  bool get _fraud => _purpose == LogAccessPurpose.fraudSuspicion;

  bool get _complete =>
      _purpose != null &&
      _reason.text.trim().length >= logReasonMinLength &&
      (!_fraud || _requestedBy.text.trim().isNotEmpty);

  Future<void> _submit() async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final requestedBy = _requestedBy.text.trim();
      final fileName = await widget.onDownload(
        _purpose!,
        _reason.text.trim(),
        _fraud && requestedBy.isNotEmpty ? requestedBy : null,
      );
      if (!mounted) return;
      Navigator.of(context).pop(fileName);
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _busy = false;
        _error = describeApiError(
          e,
          generic: l10n.logsDownloadError,
          notAuthorized: l10n.apiError403Admin,
        );
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final reasonLength = _reason.text.trim().length;
    return AlertDialog(
      title: Text(l10n.logsDownloadTitle(widget.count)),
      content: SizedBox(
        width: 500,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                l10n.logsDownloadIntro,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: PlinkColors.ink60,
                ),
              ),
              const SizedBox(height: PlinkSpacing.s4),
              Text(
                l10n.logsPurposeLabel,
                style: pastMonoLabel(PlinkColors.ink60),
              ),
              RadioGroup<LogAccessPurpose>(
                groupValue: _purpose,
                onChanged: (p) {
                  if (!_busy) setState(() => _purpose = p);
                },
                child: Column(
                  children: [
                    RadioListTile<LogAccessPurpose>(
                      key: const Key('log-purpose-technical'),
                      value: LogAccessPurpose.technicalDiagnosis,
                      contentPadding: EdgeInsets.zero,
                      title: Text(l10n.logsPurposeTechnical),
                    ),
                    RadioListTile<LogAccessPurpose>(
                      key: const Key('log-purpose-fraud'),
                      value: LogAccessPurpose.fraudSuspicion,
                      contentPadding: EdgeInsets.zero,
                      title: Text(l10n.logsPurposeFraud),
                      subtitle: Text(l10n.logsPurposeFraudDetail),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: PlinkSpacing.s3),
              TextField(
                key: const Key('log-reason'),
                controller: _reason,
                enabled: !_busy,
                minLines: 2,
                maxLines: 4,
                maxLength: 1000,
                onChanged: (_) => setState(() {}),
                decoration: InputDecoration(
                  labelText: l10n.logsReasonLabel,
                  hintText: l10n.logsReasonHint,
                  helperText:
                      reasonLength > 0 && reasonLength < logReasonMinLength
                      ? l10n.logsReasonTooShort(logReasonMinLength)
                      : null,
                ),
              ),
              if (_fraud) ...[
                const SizedBox(height: PlinkSpacing.s3),
                TextField(
                  key: const Key('log-requested-by'),
                  controller: _requestedBy,
                  enabled: !_busy,
                  maxLength: 200,
                  onChanged: (_) => setState(() {}),
                  decoration: InputDecoration(
                    labelText: l10n.logsRequestedByLabel,
                    hintText: l10n.logsRequestedByHint,
                  ),
                ),
              ],
              const SizedBox(height: PlinkSpacing.s3),
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Icon(
                    Icons.info_outline,
                    size: 18,
                    color: PlinkColors.ink60,
                  ),
                  const SizedBox(width: PlinkSpacing.s2),
                  Expanded(
                    child: Text(
                      l10n.logsRetentionReminder,
                      key: const Key('log-retention-reminder'),
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: PlinkColors.ink,
                      ),
                    ),
                  ),
                ],
              ),
              if (_error != null) ...[
                const SizedBox(height: PlinkSpacing.s3),
                ApiErrorText(_error!, key: const Key('log-download-error')),
              ],
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
          onPressed: _busy ? null : () => Navigator.of(context).pop(),
          child: Text(l10n.actionCancel),
        ),
        ElevatedButton(
          key: const Key('log-download-confirm'),
          onPressed: !_busy && _complete ? _submit : null,
          child: _busy
              ? const SizedBox(
                  width: 16,
                  height: 16,
                  child: CircularProgressIndicator(
                    strokeWidth: 2,
                    color: PlinkColors.onInk,
                  ),
                )
              : Text(l10n.logsDownload),
        ),
      ],
    );
  }
}
