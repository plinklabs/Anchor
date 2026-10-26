import 'package:flutter/material.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../api/schools_api.dart';
import '../l10n/app_localizations.dart';
import '../widgets/api_error_text.dart';

/// Admin-only "Schools" sub-tab (#301), in the paper treatment.
///
/// One readable column listing every school (Entra company) with a per-school
/// active toggle. Only active schools reach teachers in the Classes school
/// selector, so this is where an admin curates away the irrelevant ones. New
/// schools default to active, so the list mirrors what teachers see until an
/// admin deactivates a row. Each row also opens the school's lesson periods
/// (#447): one timetable for every school day, which lets its teachers end a
/// session at the end of a period. The route already gates the area on
/// `isAdmin`.
class ManageSchoolsPage extends StatefulWidget {
  const ManageSchoolsPage({super.key, required this.schools});

  final SchoolsApi schools;

  @override
  State<ManageSchoolsPage> createState() => _ManageSchoolsPageState();
}

class _ManageSchoolsPageState extends State<ManageSchoolsPage> {
  bool _loading = false;
  ApiErrorMessage? _error;
  List<School>? _schools;

  // Names with an in-flight toggle, so their row can show progress and stay
  // un-tappable without freezing the rest of the page.
  final Set<String> _busy = <String>{};

  @override
  void initState() {
    super.initState();
    // Defer the first load until after the first frame, so inherited widgets
    // (Localizations) are available when _loadSchools reads them.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _loadSchools();
    });
  }

  Future<void> _loadSchools() async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final list = await widget.schools.listSchools();
      if (!mounted) return;
      setState(() => _schools = list);
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _error = describeApiError(
          e,
          generic: l10n.schoolsLoadError,
          notAuthorized: l10n.apiError403Admin,
        ),
      );
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _setActive(School school, bool isActive) async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _busy.add(school.name);
      _error = null;
    });
    try {
      final updated = await widget.schools.setActive(school.name, isActive);
      if (!mounted) return;
      // Swap the one row in place — no full reload, so the list doesn't jump.
      setState(() {
        _schools = [
          for (final s in _schools ?? const <School>[])
            if (s.name == updated.name) updated else s,
        ];
      });
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _error = describeApiError(
          e,
          generic: l10n.schoolsUpdateError(school.name),
          notAuthorized: l10n.apiError403Admin,
        ),
      );
    } finally {
      if (mounted) setState(() => _busy.remove(school.name));
    }
  }

  /// Opens the lesson-period editor for [school] (#447). The dialog saves on
  /// its own and hands back the saved school, which replaces the row.
  Future<void> _editLessonPeriods(School school) async {
    final updated = await showDialog<School>(
      context: context,
      builder: (_) => _LessonPeriodsDialog(
        school: school,
        onSave: (periods) =>
            widget.schools.setLessonPeriods(school.name, periods),
      ),
    );
    if (updated == null || !mounted) return;
    setState(() {
      _schools = [
        for (final s in _schools ?? const <School>[])
          if (s.name == updated.name) updated else s,
      ];
    });
  }

  @override
  Widget build(BuildContext context) {
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
            constraints: const BoxConstraints(maxWidth: 640),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(
                  AppLocalizations.of(context).schoolsHeader,
                  style: _monoLabel(PlinkColors.ink60),
                ),
                const SizedBox(height: PlinkSpacing.s2),
                Text(
                  AppLocalizations.of(context).schoolsIntro,
                  style: Theme.of(
                    context,
                  ).textTheme.bodySmall?.copyWith(color: PlinkColors.ink60),
                ),
                const SizedBox(height: PlinkSpacing.s6),
                if (_error != null) ...[
                  ApiErrorText(_error!, key: const Key('manage-schools-error')),
                  const SizedBox(height: PlinkSpacing.s4),
                ],
                _buildSchoolList(),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildSchoolList() {
    final schools = _schools;
    if (_loading && schools == null) {
      return const Padding(
        padding: EdgeInsets.symmetric(vertical: PlinkSpacing.s6),
        child: Center(child: CircularProgressIndicator()),
      );
    }
    if (schools == null || schools.isEmpty) {
      return Text(
        AppLocalizations.of(context).schoolsEmpty,
        key: const Key('manage-schools-empty'),
        style: _monoLabel(PlinkColors.muted),
      );
    }
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
          for (var i = 0; i < schools.length; i++) ...[
            if (i > 0) const _Hairline(),
            _SchoolRow(
              school: schools[i],
              busy: _busy.contains(schools[i].name),
              onChanged: (value) => _setActive(schools[i], value),
              onEditLessonPeriods: () => _editLessonPeriods(schools[i]),
            ),
          ],
        ],
      ),
    );
  }
}

/// One school row — the company name over a quiet Active/Inactive state label
/// and its number of lesson periods, with a trailing lesson-periods action
/// (#447) and active toggle.
class _SchoolRow extends StatelessWidget {
  const _SchoolRow({
    required this.school,
    required this.busy,
    required this.onChanged,
    required this.onEditLessonPeriods,
  });

  final School school;
  final bool busy;
  final ValueChanged<bool> onChanged;
  final VoidCallback onEditLessonPeriods;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        PlinkSpacing.s4,
        PlinkSpacing.s3,
        PlinkSpacing.s3,
        PlinkSpacing.s3,
      ),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  school.name,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(
                    context,
                  ).textTheme.bodyLarge?.copyWith(color: PlinkColors.ink),
                ),
                Text(
                  '${school.isActive ? l10n.statusActive : l10n.statusInactive}'
                  ' · ${l10n.schoolsLessonPeriodCount(school.lessonPeriods.length)}',
                  key: Key('school-state-${school.name}'),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: _monoSpec(
                    school.isActive ? PlinkColors.ink60 : PlinkColors.muted,
                    PlinkType.textSm,
                  ),
                ),
              ],
            ),
          ),
          // A calm ink action: the timetable is edited in a dialog.
          TextButton.icon(
            key: Key('school-periods-${school.name}'),
            icon: const Icon(Icons.schedule, size: 18),
            label: Text(l10n.schoolsEditLessonPeriods),
            onPressed: onEditLessonPeriods,
          ),
          const SizedBox(width: PlinkSpacing.s2),
          if (busy)
            const Padding(
              padding: EdgeInsets.symmetric(horizontal: PlinkSpacing.s3),
              child: SizedBox(
                width: 16,
                height: 16,
                child: CircularProgressIndicator(strokeWidth: 2),
              ),
            )
          else
            Switch(
              key: Key('school-toggle-${school.name}'),
              value: school.isActive,
              onChanged: onChanged,
            ),
        ],
      ),
    );
  }
}

/// Edits a school's lesson periods (#447): one timetable for every school day,
/// as school-local times. Each row is a period's start and end; the dialog
/// checks each time, that a period ends after it starts and that no two
/// overlap, and only then lets the admin save. Saving sends the whole
/// timetable in start order and closes with the saved school.
class _LessonPeriodsDialog extends StatefulWidget {
  const _LessonPeriodsDialog({required this.school, required this.onSave});

  final School school;
  final Future<School> Function(List<LessonPeriod> periods) onSave;

  @override
  State<_LessonPeriodsDialog> createState() => _LessonPeriodsDialogState();
}

class _LessonPeriodsDialogState extends State<_LessonPeriodsDialog> {
  late final List<_PeriodRow> _rows = [
    for (final p in widget.school.lessonPeriods)
      _PeriodRow(start: p.start, end: p.end),
  ];
  bool _saving = false;
  ApiErrorMessage? _error;

  @override
  void dispose() {
    for (final row in _rows) {
      row.dispose();
    }
    super.dispose();
  }

  void _add() {
    // A new period most often starts where the last one ended.
    final last = _rows.isEmpty ? null : _parseTime(_rows.last.end.text);
    setState(() {
      _rows.add(_PeriodRow(start: last == null ? '' : _formatTime(last)));
    });
  }

  void _remove(_PeriodRow row) {
    setState(() => _rows.remove(row));
    // Its fields let go of the controllers in the coming frame.
    WidgetsBinding.instance.addPostFrameCallback((_) => row.dispose());
  }

  /// What's wrong with each row, or null where nothing is: a time that isn't
  /// one (only once something is typed), a period that doesn't end after it
  /// starts, a period that overlaps another.
  Map<_PeriodRow, String> _problems(AppLocalizations l10n) {
    final problems = <_PeriodRow, String>{};
    final valid = <(_PeriodRow, int, int)>[];
    for (final row in _rows) {
      final start = _parseTime(row.start.text);
      final end = _parseTime(row.end.text);
      final typed = row.start.text.trim().isNotEmpty;
      final typedEnd = row.end.text.trim().isNotEmpty;
      if ((typed && start == null) || (typedEnd && end == null)) {
        problems[row] = l10n.schoolsLessonPeriodInvalidTime;
      } else if (start != null && end != null) {
        if (end <= start) {
          problems[row] = l10n.schoolsLessonPeriodEndBeforeStart;
        } else {
          valid.add((row, start, end));
        }
      }
    }
    valid.sort((a, b) => a.$2.compareTo(b.$2));
    for (var i = 1; i < valid.length; i++) {
      if (valid[i].$2 < valid[i - 1].$3) {
        problems[valid[i].$1] = l10n.schoolsLessonPeriodsOverlap;
      }
    }
    return problems;
  }

  bool get _complete => _rows.every(
    (r) => _parseTime(r.start.text) != null && _parseTime(r.end.text) != null,
  );

  Future<void> _save() async {
    final l10n = AppLocalizations.of(context);
    final periods = [
      for (final row in _rows)
        (_parseTime(row.start.text)!, _parseTime(row.end.text)!),
    ]..sort((a, b) => a.$1.compareTo(b.$1));
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final saved = await widget.onSave([
        for (final (start, end) in periods)
          LessonPeriod(start: _formatTime(start), end: _formatTime(end)),
      ]);
      if (!mounted) return;
      Navigator.of(context).pop(saved);
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _saving = false;
        _error = describeApiError(
          e,
          generic: l10n.schoolsLessonPeriodsSaveError(widget.school.name),
          notAuthorized: l10n.apiError403Admin,
        );
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final problems = _problems(l10n);
    final canSave = !_saving && problems.isEmpty && _complete;
    return AlertDialog(
      title: Text(l10n.schoolsLessonPeriodsTitle(widget.school.name)),
      content: SizedBox(
        width: 460,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                l10n.schoolsLessonPeriodsIntro,
                style: Theme.of(
                  context,
                ).textTheme.bodySmall?.copyWith(color: PlinkColors.ink60),
              ),
              const SizedBox(height: PlinkSpacing.s4),
              if (_rows.isEmpty)
                Text(
                  l10n.schoolsLessonPeriodsEmpty,
                  key: const Key('lesson-periods-empty'),
                  style: _monoLabel(PlinkColors.muted),
                ),
              for (var i = 0; i < _rows.length; i++)
                _PeriodRowEditor(
                  key: ObjectKey(_rows[i]),
                  index: i,
                  row: _rows[i],
                  problem: problems[_rows[i]],
                  enabled: !_saving,
                  onChanged: () => setState(() {}),
                  onRemove: () => _remove(_rows[i]),
                ),
              Align(
                alignment: Alignment.centerLeft,
                // Calm ink affordance — only the Save commit wears the spark.
                child: TextButton.icon(
                  key: const Key('lesson-period-add'),
                  icon: const Icon(Icons.add, size: 18),
                  label: Text(l10n.schoolsAddLessonPeriod),
                  onPressed: _saving ? null : _add,
                ),
              ),
              if (_error != null) ...[
                const SizedBox(height: PlinkSpacing.s3),
                ApiErrorText(_error!, key: const Key('lesson-periods-error')),
              ],
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
          onPressed: _saving ? null : () => Navigator.of(context).pop(),
          child: Text(l10n.actionCancel),
        ),
        ElevatedButton(
          key: const Key('lesson-periods-save'),
          onPressed: canSave ? _save : null,
          child: _saving
              ? const SizedBox(
                  width: 16,
                  height: 16,
                  child: CircularProgressIndicator(
                    strokeWidth: 2,
                    color: PlinkColors.onInk,
                  ),
                )
              : Text(l10n.actionSave),
        ),
      ],
    );
  }
}

/// One period being edited: its start and end as typed.
class _PeriodRow {
  _PeriodRow({String start = '', String end = ''})
    : start = TextEditingController(text: start),
      end = TextEditingController(text: end);

  final TextEditingController start;
  final TextEditingController end;

  void dispose() {
    start.dispose();
    end.dispose();
  }
}

/// A period's row in the editor: its number, the start and end fields, a
/// remove action, and what's wrong with it, if anything.
class _PeriodRowEditor extends StatelessWidget {
  const _PeriodRowEditor({
    super.key,
    required this.index,
    required this.row,
    required this.problem,
    required this.enabled,
    required this.onChanged,
    required this.onRemove,
  });

  final int index;
  final _PeriodRow row;
  final String? problem;
  final bool enabled;
  final VoidCallback onChanged;
  final VoidCallback onRemove;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    Widget field(TextEditingController controller, String label, String key) =>
        SizedBox(
          width: 96,
          child: TextField(
            key: Key(key),
            controller: controller,
            enabled: enabled,
            onChanged: (_) => onChanged(),
            keyboardType: TextInputType.datetime,
            style: _monoSpec(PlinkColors.ink, PlinkType.textBase),
            decoration: InputDecoration(
              labelText: label,
              hintText: '08:25',
              isDense: true,
            ),
          ),
        );

    return Padding(
      padding: const EdgeInsets.only(bottom: PlinkSpacing.s3),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  l10n.schoolsLessonPeriodNumber(index + 1),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: _monoLabel(PlinkColors.ink60),
                ),
              ),
              field(
                row.start,
                l10n.schoolsLessonPeriodStart,
                'lesson-period-start-$index',
              ),
              const SizedBox(width: PlinkSpacing.s3),
              field(
                row.end,
                l10n.schoolsLessonPeriodEnd,
                'lesson-period-end-$index',
              ),
              IconButton(
                key: Key('lesson-period-remove-$index'),
                icon: const Icon(Icons.remove_circle_outline),
                color: PlinkColors.ink60,
                tooltip: l10n.schoolsRemoveLessonPeriod,
                onPressed: enabled ? onRemove : null,
              ),
            ],
          ),
          if (problem != null)
            Padding(
              padding: const EdgeInsets.only(top: PlinkSpacing.s1),
              child: Text(
                problem!,
                key: Key('lesson-period-problem-$index'),
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: Theme.of(context).colorScheme.error,
                ),
              ),
            ),
        ],
      ),
    );
  }
}

/// Minutes after midnight for a typed time like `8:25` or `08:25`, or null
/// when it isn't one. The backend takes the same forms.
int? _parseTime(String text) {
  final match = RegExp(r'^(\d{1,2}):(\d{2})$').firstMatch(text.trim());
  if (match == null) return null;
  final hours = int.parse(match.group(1)!);
  final minutes = int.parse(match.group(2)!);
  if (hours > 23 || minutes > 59) return null;
  return hours * 60 + minutes;
}

/// `HH:mm` for minutes after midnight.
String _formatTime(int minutes) =>
    '${(minutes ~/ 60).toString().padLeft(2, '0')}:'
    '${(minutes % 60).toString().padLeft(2, '0')}';

/// A full-width 1px instrument rule — the system separates with hairlines,
/// never shadows. Mirrors the manage-admins list.
class _Hairline extends StatelessWidget {
  const _Hairline();

  @override
  Widget build(BuildContext context) {
    return const SizedBox(
      height: PlinkBorders.width,
      child: ColoredBox(color: PlinkColors.hairline),
    );
  }
}

/// A space-mono label style for the quiet section header and microcopy.
TextStyle _monoLabel(Color color) =>
    const TextStyle(
      fontFamily: PlinkType.monoFamily,
      package: PlinkType.fontPackage,
      fontFamilyFallback: PlinkType.monoFallback,
      fontSize: PlinkType.label,
    ).copyWith(
      letterSpacing: PlinkType.tracking(
        PlinkType.labelTrackingTight,
        PlinkType.label,
      ),
      color: color,
      height: 1.3,
    );

/// Tabular-figure mono for the quiet Active/Inactive state label.
TextStyle _monoSpec(Color color, double size) => TextStyle(
  fontFamily: PlinkType.monoFamily,
  package: PlinkType.fontPackage,
  fontFamilyFallback: PlinkType.monoFallback,
  fontSize: size,
  color: color,
  height: 1.4,
  fontFeatures: const <FontFeature>[FontFeature.tabularFigures()],
);
