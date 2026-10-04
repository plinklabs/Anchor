import 'package:flutter/material.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../api/classes_api.dart';
import '../api/sessions_api.dart';
import '../l10n/app_localizations.dart';
import '../widgets/api_error_text.dart';
import 'add_student_search.dart';

/// Classes / roster manager (#152), redesigned to the paper treatment (AD6,
/// #171) so it reads like the rest of the dashboard (home #168, live session
/// #169, bundles #170).
///
/// Two panes on a shared hairline: a list of classes (each row a quiet
/// instrument line, the school year shown as a mono spec) and, beside it, the
/// roster pane for the selected class — its scope row (school + class code), the
/// add-student / import affordances, and a hairline-bounded roster of members
/// whose role reads as a mono spec chip.
///
/// Magenta is the single spark, reserved for the constructive commits a teacher
/// makes here: Save (the scope binding), Create (a new class), and the Add
/// confirmation inside the student search. Every other affordance — New class,
/// Import CSV, Populate from Graph, Archive / Restore, Delete / Remove — stays
/// calm ink so the page reads like an instrument, not a console of buttons.
///
/// Last year's classes are archived rather than kept in the list forever
/// (#395): an archived class leaves the list and Home's class picker, and comes
/// back with the Archived switch above the list, to restore it. Deleting a
/// class with sessions deletes them too, so that dialog says how many and asks
/// for the class's name first.
///
/// Purely presentational: the roster/scope/import logic (validation, scope
/// gating per #96, CSV parse, bulk import) is untouched.
class ClassesPage extends StatefulWidget {
  const ClassesPage({super.key, required this.sessions, required this.classes});

  final SessionsApi sessions;
  final ClassesApi classes;

  @override
  State<ClassesPage> createState() => _ClassesPageState();
}

class _ClassesPageState extends State<ClassesPage> {
  List<ClassSummary>? _classes;
  ClassSummary? _selected;
  ClassMembersResponse? _roster;
  List<String>? _schools;
  bool _loadingClasses = false;
  bool _loadingRoster = false;
  bool _loadingSchools = false;
  bool _savingCodes = false;
  bool _bulkImporting = false;
  bool _changingArchive = false;

  /// Whether the list shows archived classes too (#395). Off, it lists what
  /// Home's picker offers.
  bool _showArchived = false;
  ApiErrorMessage? _error;
  // Kept separate from [_error] (which carries class/roster failures): a failed
  // school-tag load is non-blocking, so it surfaces inline next to the School
  // selector with a Retry rather than taking over the page. Without this a
  // consent/502 gap (#281) rendered as a silently empty dropdown.
  ApiErrorMessage? _schoolsError;
  List<ClassMembershipImportResult>? _lastImportResults;

  // Editable copies of the selected class's schoolTag / classCode. Sit
  // alongside [_selected] (which mirrors the server) until the teacher hits
  // Save; allows them to walk away from edits by re-selecting a class.
  String? _editSchoolTag;
  final _classCodeController = TextEditingController();

  @override
  void initState() {
    super.initState();
    // Defer to the first frame: these loaders read AppLocalizations.of(context),
    // which depends on an inherited widget and so can't be touched synchronously
    // during initState.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      _loadClasses();
      _loadSchools();
    });
  }

  @override
  void dispose() {
    _classCodeController.dispose();
    super.dispose();
  }

  Future<void> _loadClasses() async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _loadingClasses = true;
      _error = null;
    });
    try {
      // Archived classes only when asked for (#395): the default list is the
      // one Home's picker gets, which leaves them out.
      final list = _showArchived
          ? await widget.classes.listIncludingArchived()
          : await widget.sessions.classes();
      if (!mounted) return;
      final previous = _selected;
      ClassSummary? kept;
      for (final c in list) {
        if (c.id == previous?.id) kept = c;
      }
      setState(() {
        _classes = list;
        // The Archived switch reloads the list: keep the class in the roster
        // pane (and any unsaved scope edit) when it is still listed.
        if (kept != null) {
          _selected = kept;
        } else if (list.isNotEmpty) {
          _selectClass(list.first, refreshRoster: false);
        } else {
          _selected = null;
          _roster = null;
          _lastImportResults = null;
        }
      });
      final selected = _selected;
      if (selected != null && selected.id != previous?.id) {
        await _loadRoster(selected);
      }
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _error = describeApiError(
          e,
          generic: l10n.classesLoadError,
          notAuthorized: l10n.apiError403,
        ),
      );
    } finally {
      if (mounted) setState(() => _loadingClasses = false);
    }
  }

  Future<void> _loadSchools() async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _loadingSchools = true;
      _schoolsError = null;
    });
    try {
      final schools = await widget.classes.schools();
      if (!mounted) return;
      setState(() => _schools = schools);
    } catch (e) {
      // Non-blocking — listing school tags is a discovery convenience, not a
      // gate (a class's existing binding is still editable). But don't swallow
      // it: surface an inline hint + Retry next to the selector so a consent/502
      // gap can't masquerade as "this tenant has no schools" (#281).
      if (!mounted) return;
      setState(() {
        _schools = const <String>[];
        _schoolsError = describeApiError(
          e,
          generic: l10n.classesLoadSchoolsError,
          notAuthorized: l10n.apiError403,
        );
      });
    } finally {
      if (mounted) setState(() => _loadingSchools = false);
    }
  }

  Future<void> _loadRoster(ClassSummary klass) async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _loadingRoster = true;
      _error = null;
      _lastImportResults = null;
    });
    try {
      final roster = await widget.classes.members(klass.id);
      if (!mounted) return;
      setState(() => _roster = roster);
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _error = describeApiError(
          e,
          generic: l10n.classesLoadRosterError,
          notAuthorized: l10n.apiError403,
        ),
      );
    } finally {
      if (mounted) setState(() => _loadingRoster = false);
    }
  }

  void _selectClass(ClassSummary klass, {bool refreshRoster = true}) {
    setState(() {
      _selected = klass;
      _roster = null;
      _lastImportResults = null;
      _editSchoolTag = klass.schoolTag;
      _classCodeController.text = klass.classCode ?? '';
    });
    if (refreshRoster) _loadRoster(klass);
  }

  Future<void> _createClass() async {
    final created = await showDialog<ClassSummary>(
      context: context,
      builder: (_) => _NewClassDialog(
        schools: _schools ?? const <String>[],
        onCreate:
            ({
              required String name,
              required String schoolYear,
              String? schoolTag,
              String? classCode,
            }) => widget.classes.createClass(
              name: name,
              schoolYear: schoolYear,
              schoolTag: schoolTag,
              classCode: classCode,
            ),
      ),
    );
    if (created == null) return;
    if (!mounted) return;
    setState(() {
      _classes = [...?_classes, created]
        ..sort((a, b) => a.name.compareTo(b.name));
    });
    _selectClass(created);
  }

  void _toggleArchived(bool show) {
    setState(() => _showArchived = show);
    _loadClasses();
  }

  /// Takes a class out of the list and selects the first one left.
  void _dropClass(String classId) {
    setState(() {
      _classes = _classes
          ?.where((c) => c.id != classId)
          .toList(growable: false);
      _selected = null;
      _roster = null;
      _lastImportResults = null;
      _error = null;
    });
    final remaining = _classes;
    if (remaining != null && remaining.isNotEmpty) {
      _selectClass(remaining.first);
    }
  }

  Future<void> _archiveClass() async {
    final klass = _selected;
    if (klass == null) return;
    final l10n = AppLocalizations.of(context);
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(l10n.classesArchiveTitle),
        content: SizedBox(
          width: 460,
          child: Text(l10n.classesArchiveBody(klass.name, klass.schoolYear)),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: Text(l10n.actionCancel),
          ),
          FilledButton(
            key: const Key('classes-archive-confirm-button'),
            onPressed: () => Navigator.of(ctx).pop(true),
            child: Text(l10n.classesArchive),
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    await _setArchived(klass, archived: true);
  }

  Future<void> _restoreClass() async {
    final klass = _selected;
    if (klass == null) return;
    await _setArchived(klass, archived: false);
  }

  Future<void> _setArchived(
    ClassSummary klass, {
    required bool archived,
  }) async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _changingArchive = true;
      _error = null;
    });
    try {
      final updated = archived
          ? await widget.classes.archiveClass(klass.id)
          : await widget.classes.unarchiveClass(klass.id);
      if (!mounted) return;
      if (updated.isArchived && !_showArchived) {
        // Out of the list, as it is out of Home's picker.
        _dropClass(updated.id);
      } else {
        setState(() {
          _classes = _classes
              ?.map((c) => c.id == updated.id ? updated : c)
              .toList(growable: false);
          if (_selected?.id == updated.id) _selected = updated;
        });
      }
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _error = describeApiError(
          e,
          generic: archived
              ? l10n.classesArchiveError
              : l10n.classesRestoreError,
          notAuthorized: l10n.apiError403,
        ),
      );
    } finally {
      if (mounted) setState(() => _changingArchive = false);
    }
  }

  Future<void> _deleteClass() async {
    final klass = _selected;
    if (klass == null) return;
    final l10n = AppLocalizations.of(context);
    final choice = await showDialog<_DeleteChoice>(
      context: context,
      builder: (_) => _DeleteClassDialog(klass: klass),
    );
    if (choice == _DeleteChoice.archive) {
      await _setArchived(klass, archived: true);
      return;
    }
    if (choice != _DeleteChoice.delete) return;
    // Only a delete the teacher confirmed with the session count in front of
    // them takes sessions with it (#395). Without the flag the backend refuses
    // a class that has sessions after all.
    final includeSessions = klass.sessionCount > 0;
    try {
      await widget.classes.deleteClass(
        klass.id,
        includeSessions: includeSessions,
      );
      if (!mounted) return;
      _dropClass(klass.id);
    } catch (e) {
      if (!mounted) return;
      // A 409: with sessions, one of them is still running; without, one
      // started since the list loaded, so the dialog didn't count it.
      final conflict = e is ApiException && e.statusCode == 409;
      setState(
        () => _error = conflict
            ? ApiErrorMessage(
                includeSessions
                    ? l10n.classesDeleteRunningError
                    : l10n.classesDeleteHasSessionsError,
              )
            : describeApiError(
                e,
                generic: l10n.classesDeleteError,
                notAuthorized: l10n.apiError403,
              ),
      );
    }
  }

  Future<void> _saveCodes() async {
    final klass = _selected;
    if (klass == null) return;
    final l10n = AppLocalizations.of(context);
    setState(() {
      _savingCodes = true;
      _error = null;
    });
    try {
      final updated = await widget.classes.updateCodes(
        klass.id,
        schoolTag: _editSchoolTag,
        classCode: _classCodeController.text.trim().isEmpty
            ? null
            : _classCodeController.text.trim(),
      );
      if (!mounted) return;
      setState(() {
        _selected = updated;
        _classes = _classes
            ?.map((c) => c.id == updated.id ? updated : c)
            .toList(growable: false);
        _editSchoolTag = updated.schoolTag;
        _classCodeController.text = updated.classCode ?? '';
      });
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _error = describeApiError(
          e,
          generic: l10n.classesSaveCodesError,
          notAuthorized: l10n.apiError403,
        ),
      );
    } finally {
      if (mounted) setState(() => _savingCodes = false);
    }
  }

  Future<void> _addMember(String entraOid, String? displayName) async {
    final klass = _selected;
    if (klass == null) return;
    final l10n = AppLocalizations.of(context);
    try {
      await widget.classes.addMember(
        klass.id,
        entraOid: entraOid,
        displayName: displayName,
      );
      await _loadRoster(klass);
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _error = describeApiError(
          e,
          generic: l10n.classesAddMemberError,
          notAuthorized: l10n.apiError403,
        ),
      );
    }
  }

  Future<void> _removeMember(ClassMember member) async {
    final klass = _selected;
    if (klass == null) return;
    final l10n = AppLocalizations.of(context);
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(l10n.classesRemoveMemberTitle),
        content: Text(
          l10n.classesRemoveMemberBody(member.displayName, klass.name),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: Text(l10n.actionCancel),
          ),
          FilledButton(
            style: FilledButton.styleFrom(
              backgroundColor: Theme.of(ctx).colorScheme.error,
            ),
            onPressed: () => Navigator.of(ctx).pop(true),
            child: Text(l10n.actionRemove),
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    try {
      await widget.classes.removeMember(klass.id, member.userId);
      await _loadRoster(klass);
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _error = describeApiError(
          e,
          generic: l10n.classesRemoveMemberError,
          notAuthorized: l10n.apiError403,
        ),
      );
    }
  }

  Future<void> _importCsv() async {
    final klass = _selected;
    if (klass == null) return;
    final l10n = AppLocalizations.of(context);
    final pasted = await showDialog<String>(
      context: context,
      builder: (_) => const _CsvPasteDialog(),
    );
    if (pasted == null || pasted.trim().isEmpty) return;

    final parsed = parseRosterCsv(pasted, l10n);
    if (parsed.rows.isEmpty) {
      setState(
        () => _error = ApiErrorMessage(parsed.error ?? l10n.classesCsvNoRows),
      );
      return;
    }
    try {
      final results = await widget.classes.importMembers(klass.id, parsed.rows);
      if (!mounted) return;
      setState(() => _lastImportResults = results);
      await _loadRoster(klass);
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _error = describeApiError(
          e,
          generic: l10n.classesImportError,
          notAuthorized: l10n.apiError403,
        ),
      );
    }
  }

  Future<void> _bulkImportFromGraph() async {
    final klass = _selected;
    if (klass == null) return;
    final l10n = AppLocalizations.of(context);
    setState(() {
      _bulkImporting = true;
      _error = null;
    });
    try {
      final results = await widget.classes.bulkImportFromDirectory(klass.id);
      if (!mounted) return;
      setState(() => _lastImportResults = results);
      await _loadRoster(klass);
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _error = describeApiError(
          e,
          generic: l10n.classesGraphImportError,
          notAuthorized: l10n.apiError403,
        ),
      );
    } finally {
      if (mounted) setState(() => _bulkImporting = false);
    }
  }

  Future<List<DirectoryUser>> _searchUsers(String query) {
    // Always scope by the class's saved schoolTag — never by the unsaved
    // edit, which could surface students from a school the class isn't
    // actually bound to.
    return widget.classes.searchUsers(query, company: _selected?.schoolTag);
  }

  bool get _codesDirty {
    final klass = _selected;
    if (klass == null) return false;
    final currentCode = _classCodeController.text.trim();
    final savedCode = klass.classCode ?? '';
    return _editSchoolTag != klass.schoolTag || currentCode != savedCode;
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: PlinkColors.paper,
      body: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          SizedBox(
            width: 260,
            child: _ClassList(
              classes: _classes,
              selected: _selected,
              loading: _loadingClasses,
              showArchived: _showArchived,
              onToggleArchived: _toggleArchived,
              onSelect: _selectClass,
              onCreate: _createClass,
            ),
          ),
          // A vertical hairline between the panes — the system separates with
          // rules, never shadows.
          const SizedBox(
            width: PlinkBorders.width,
            child: ColoredBox(color: PlinkColors.hairline),
          ),
          Expanded(
            child: _selected == null
                // No class selected: surface a load failure (e.g. a 403) here
                // rather than letting it vanish — the roster pane that would
                // otherwise render the error never mounts without a selection.
                ? Center(
                    child: Padding(
                      padding: const EdgeInsets.all(PlinkSpacing.s6),
                      child: _error != null
                          ? ApiErrorText(_error!, textAlign: TextAlign.center)
                          : Text(
                              AppLocalizations.of(context).classesPickClass,
                              style: monoLabel(PlinkColors.muted),
                            ),
                    ),
                  )
                : _RosterPane(
                    klass: _selected!,
                    onDeleteClass: _deleteClass,
                    onArchiveClass: _archiveClass,
                    onRestoreClass: _restoreClass,
                    changingArchive: _changingArchive,
                    roster: _roster,
                    schools: _schools,
                    loadingSchools: _loadingSchools,
                    schoolsError: _schoolsError,
                    onReloadSchools: _loadSchools,
                    loadingRoster: _loadingRoster,
                    savingCodes: _savingCodes,
                    bulkImporting: _bulkImporting,
                    codesDirty: _codesDirty,
                    editSchoolTag: _editSchoolTag,
                    classCodeController: _classCodeController,
                    onSchoolChanged: (v) => setState(() => _editSchoolTag = v),
                    onClassCodeChanged: (_) => setState(() {}),
                    onSaveCodes: _saveCodes,
                    onBulkImport: _bulkImportFromGraph,
                    error: _error,
                    lastImportResults: _lastImportResults,
                    onSearch: _searchUsers,
                    onAdd: _addMember,
                    onRemove: _removeMember,
                    onImport: _importCsv,
                  ),
          ),
        ],
      ),
    );
  }
}

class _ClassList extends StatelessWidget {
  const _ClassList({
    required this.classes,
    required this.selected,
    required this.loading,
    required this.showArchived,
    required this.onToggleArchived,
    required this.onSelect,
    required this.onCreate,
  });

  final List<ClassSummary>? classes;
  final ClassSummary? selected;
  final bool loading;
  final bool showArchived;
  final void Function(bool) onToggleArchived;
  final void Function(ClassSummary) onSelect;
  final Future<void> Function() onCreate;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final list = classes ?? const <ClassSummary>[];
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(
            PlinkSpacing.s4,
            PlinkSpacing.s4,
            PlinkSpacing.s4,
            PlinkSpacing.s3,
          ),
          child: Row(
            children: [
              Expanded(
                child: Text(
                  l10n.classesListHeader,
                  style: monoLabel(PlinkColors.ink60),
                ),
              ),
              // The way back to archived classes (#395), as on the Bundles
              // page: a quiet switch on the header line.
              Text(l10n.badgeArchived, style: monoLabel(PlinkColors.muted)),
              const SizedBox(width: PlinkSpacing.s2),
              Tooltip(
                message: l10n.classesShowArchived,
                child: Transform.scale(
                  scale: 0.8,
                  child: Switch(
                    key: const Key('classes-show-archived'),
                    value: showArchived,
                    onChanged: onToggleArchived,
                  ),
                ),
              ),
            ],
          ),
        ),
        Padding(
          padding: const EdgeInsets.fromLTRB(
            PlinkSpacing.s4,
            0,
            PlinkSpacing.s4,
            PlinkSpacing.s4,
          ),
          child: SizedBox(
            width: double.infinity,
            // Calm ink action — creating a class is navigation into a dialog,
            // not the constructive commit. The magenta spark stays on Create.
            child: OutlinedButton.icon(
              key: const Key('classes-new-button'),
              icon: const Icon(Icons.add, size: 18),
              label: Text(l10n.classesNewClass),
              onPressed: () => onCreate(),
            ),
          ),
        ),
        const _Hairline(),
        Expanded(
          child: loading && classes == null
              ? const Center(child: CircularProgressIndicator())
              // A null list means the load didn't complete (errored or not yet
              // run); only an actually-loaded empty list is "no classes".
              : classes == null
              ? const SizedBox.shrink()
              : list.isEmpty
              ? Padding(
                  padding: const EdgeInsets.all(PlinkSpacing.s4),
                  child: Text(
                    l10n.classesEmpty,
                    style: monoLabel(PlinkColors.muted),
                  ),
                )
              : ListView.separated(
                  padding: EdgeInsets.zero,
                  itemCount: list.length,
                  separatorBuilder: (_, _) => const _Hairline(),
                  itemBuilder: (_, i) {
                    final c = list[i];
                    return _ClassRow(
                      summary: c,
                      selected: selected?.id == c.id,
                      onTap: () => onSelect(c),
                    );
                  },
                ),
        ),
      ],
    );
  }
}

/// One class row — a hairline instrument line. The class name reads first; the
/// school year sits beneath as a mono spec. The selected row is marked by a
/// paper-tint fill and a magenta edge tick (the same spark the nav uses as its
/// active indicator), never a heavy highlight.
class _ClassRow extends StatelessWidget {
  const _ClassRow({
    required this.summary,
    required this.selected,
    required this.onTap,
  });

  final ClassSummary summary;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: onTap,
      child: ColoredBox(
        color: selected ? PlinkColors.paper2 : PlinkColors.paper,
        child: Row(
          children: [
            // The magenta active tick — mirrors the app-bar's nav indicator.
            SizedBox(
              width: 3,
              height: 52,
              child: selected
                  ? const ColoredBox(color: PlinkColors.magenta)
                  : null,
            ),
            Expanded(
              child: Padding(
                padding: const EdgeInsets.fromLTRB(
                  PlinkSpacing.s4 - 3,
                  PlinkSpacing.s3,
                  PlinkSpacing.s4,
                  PlinkSpacing.s3,
                ),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    Text(
                      summary.name,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: Theme.of(
                        context,
                      ).textTheme.bodyLarge?.copyWith(color: PlinkColors.ink),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      summary.schoolYear,
                      style: monoSpec(PlinkColors.ink60),
                    ),
                  ],
                ),
              ),
            ),
            if (summary.isArchived)
              Padding(
                padding: const EdgeInsets.only(right: PlinkSpacing.s4),
                child: PlinkBadge(AppLocalizations.of(context).badgeArchived),
              ),
          ],
        ),
      ),
    );
  }
}

class _RosterPane extends StatelessWidget {
  const _RosterPane({
    required this.klass,
    required this.onDeleteClass,
    required this.onArchiveClass,
    required this.onRestoreClass,
    required this.changingArchive,
    required this.roster,
    required this.schools,
    required this.loadingSchools,
    required this.schoolsError,
    required this.onReloadSchools,
    required this.loadingRoster,
    required this.savingCodes,
    required this.bulkImporting,
    required this.codesDirty,
    required this.editSchoolTag,
    required this.classCodeController,
    required this.onSchoolChanged,
    required this.onClassCodeChanged,
    required this.onSaveCodes,
    required this.onBulkImport,
    required this.error,
    required this.lastImportResults,
    required this.onSearch,
    required this.onAdd,
    required this.onRemove,
    required this.onImport,
  });

  final ClassSummary klass;
  final Future<void> Function() onDeleteClass;
  final Future<void> Function() onArchiveClass;
  final Future<void> Function() onRestoreClass;
  final bool changingArchive;
  final ClassMembersResponse? roster;
  final List<String>? schools;
  final bool loadingSchools;
  final ApiErrorMessage? schoolsError;
  final Future<void> Function() onReloadSchools;
  final bool loadingRoster;
  final bool savingCodes;
  final bool bulkImporting;
  final bool codesDirty;
  final String? editSchoolTag;
  final TextEditingController classCodeController;
  final void Function(String?) onSchoolChanged;
  final void Function(String) onClassCodeChanged;
  final Future<void> Function() onSaveCodes;
  final Future<void> Function() onBulkImport;
  final ApiErrorMessage? error;
  final List<ClassMembershipImportResult>? lastImportResults;
  final Future<List<DirectoryUser>> Function(String query) onSearch;
  final Future<void> Function(String entraOid, String? displayName) onAdd;
  final Future<void> Function(ClassMember member) onRemove;
  final Future<void> Function() onImport;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final scopeReady =
        klass.schoolTag != null && (klass.classCode ?? '').isNotEmpty;
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        PlinkSpacing.s6,
        PlinkSpacing.s5,
        PlinkSpacing.s6,
        PlinkSpacing.s5,
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // A Wrap, not a Row: with Archive beside Delete (#395), the actions
          // drop under the title in a narrow pane instead of overflowing it.
          Wrap(
            alignment: WrapAlignment.spaceBetween,
            crossAxisAlignment: WrapCrossAlignment.center,
            spacing: PlinkSpacing.s3,
            runSpacing: PlinkSpacing.s3,
            children: [
              Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Flexible(
                    child: Text(
                      '${klass.name} (${klass.schoolYear})',
                      style: Theme.of(
                        context,
                      ).textTheme.titleLarge?.copyWith(color: PlinkColors.ink),
                    ),
                  ),
                  if (klass.isArchived) ...[
                    const SizedBox(width: PlinkSpacing.s3),
                    PlinkBadge(l10n.badgeArchived),
                  ],
                ],
              ),
              Wrap(
                spacing: PlinkSpacing.s2,
                runSpacing: PlinkSpacing.s2,
                children: [
                  // Archive is the reversible way out of the lists (#395); an
                  // archived class offers Restore in its place. Calm ink, like
                  // Delete.
                  OutlinedButton.icon(
                    key: Key(
                      klass.isArchived
                          ? 'classes-restore-button'
                          : 'classes-archive-button',
                    ),
                    icon: Icon(
                      klass.isArchived
                          ? Icons.unarchive_outlined
                          : Icons.archive_outlined,
                      size: 18,
                    ),
                    label: Text(
                      klass.isArchived
                          ? l10n.classesRestore
                          : l10n.classesArchive,
                    ),
                    onPressed: changingArchive
                        ? null
                        : () => klass.isArchived
                              ? onRestoreClass()
                              : onArchiveClass(),
                  ),
                  // Calm ink — the destructive action never wears the spark.
                  OutlinedButton.icon(
                    icon: const Icon(Icons.delete_outline, size: 18),
                    label: Text(l10n.classesDeleteClass),
                    onPressed: () => onDeleteClass(),
                  ),
                ],
              ),
            ],
          ),
          const SizedBox(height: PlinkSpacing.s4),
          _ScopeRow(
            schools: schools,
            loadingSchools: loadingSchools,
            schoolsError: schoolsError,
            onReloadSchools: onReloadSchools,
            saving: savingCodes,
            dirty: codesDirty,
            editSchoolTag: editSchoolTag,
            classCodeController: classCodeController,
            onSchoolChanged: onSchoolChanged,
            onClassCodeChanged: onClassCodeChanged,
            onSave: onSaveCodes,
          ),
          const SizedBox(height: PlinkSpacing.s5),
          Wrap(
            spacing: PlinkSpacing.s3,
            runSpacing: PlinkSpacing.s3,
            crossAxisAlignment: WrapCrossAlignment.start,
            children: [
              AddStudentSearch(
                onSearch: onSearch,
                onAdd: onAdd,
                disabled: !scopeReady,
                disabledReason: l10n.classesSetScopeFirst,
              ),
              Padding(
                padding: const EdgeInsets.only(top: PlinkSpacing.s2),
                child: OutlinedButton.icon(
                  icon: const Icon(Icons.upload_file, size: 18),
                  label: Text(l10n.classesImportCsv),
                  onPressed: scopeReady ? onImport : null,
                ),
              ),
              Padding(
                padding: const EdgeInsets.only(top: PlinkSpacing.s2),
                // Calm ink — populating from the directory is a fetch, not the
                // page's constructive spark.
                child: OutlinedButton.icon(
                  icon: bulkImporting
                      ? const SizedBox(
                          width: 16,
                          height: 16,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Icon(Icons.cloud_download, size: 18),
                  label: Text(l10n.classesPopulateFromGraph),
                  onPressed: (scopeReady && !bulkImporting)
                      ? onBulkImport
                      : null,
                ),
              ),
            ],
          ),
          if (error != null) ...[
            const SizedBox(height: PlinkSpacing.s3),
            ApiErrorText(error!),
          ],
          if (lastImportResults != null) ...[
            const SizedBox(height: PlinkSpacing.s3),
            _ImportResultsBar(results: lastImportResults!),
          ],
          const SizedBox(height: PlinkSpacing.s4),
          Expanded(
            child: loadingRoster && roster == null
                ? const Center(child: CircularProgressIndicator())
                : roster == null
                ? const SizedBox.shrink()
                : _RosterTable(members: roster!.members, onRemove: onRemove),
          ),
        ],
      ),
    );
  }
}

class _ScopeRow extends StatelessWidget {
  const _ScopeRow({
    required this.schools,
    required this.loadingSchools,
    required this.schoolsError,
    required this.onReloadSchools,
    required this.saving,
    required this.dirty,
    required this.editSchoolTag,
    required this.classCodeController,
    required this.onSchoolChanged,
    required this.onClassCodeChanged,
    required this.onSave,
  });

  final List<String>? schools;
  final bool loadingSchools;
  final ApiErrorMessage? schoolsError;
  final Future<void> Function() onReloadSchools;
  final bool saving;
  final bool dirty;
  final String? editSchoolTag;
  final TextEditingController classCodeController;
  final void Function(String?) onSchoolChanged;
  final void Function(String) onClassCodeChanged;
  final Future<void> Function() onSave;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final options = schools ?? const <String>[];
    // If the class is bound to a tag the directory no longer reports, keep
    // it in the dropdown so the teacher doesn't silently lose the binding.
    final entries = <String>{
      ...options,
      if (editSchoolTag != null && editSchoolTag!.isNotEmpty) editSchoolTag!,
    }.toList();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        _buildRow(entries, l10n),
        // A failed school-tag load is non-blocking (the binding stays editable),
        // but surface it inline with a Retry so a consent/502 gap can't read as
        // an empty directory (#281).
        if (schoolsError != null && !loadingSchools)
          Padding(
            padding: const EdgeInsets.only(top: PlinkSpacing.s2),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Flexible(child: ApiErrorText(schoolsError!)),
                const SizedBox(width: PlinkSpacing.s2),
                TextButton(
                  key: const Key('classes-schools-retry-button'),
                  onPressed: onReloadSchools,
                  child: Text(l10n.actionRetry),
                ),
              ],
            ),
          ),
      ],
    );
  }

  Widget _buildRow(List<String> entries, AppLocalizations l10n) {
    return Wrap(
      spacing: PlinkSpacing.s3,
      runSpacing: PlinkSpacing.s3,
      crossAxisAlignment: WrapCrossAlignment.end,
      children: [
        SizedBox(
          width: 220,
          child: DropdownButtonFormField<String?>(
            initialValue: editSchoolTag,
            isDense: true,
            decoration: InputDecoration(
              labelText: l10n.classesSchoolLabel,
              isDense: true,
              helperText: loadingSchools ? l10n.commonLoading : null,
            ),
            items: [
              DropdownMenuItem<String?>(
                value: null,
                child: Text(l10n.commonNone),
              ),
              for (final s in entries)
                DropdownMenuItem<String?>(value: s, child: Text(s)),
            ],
            onChanged: saving ? null : onSchoolChanged,
          ),
        ),
        SizedBox(
          width: 160,
          child: TextField(
            controller: classCodeController,
            enabled: !saving,
            onChanged: onClassCodeChanged,
            decoration: InputDecoration(
              labelText: l10n.classesClassCodeLabel,
              hintText: l10n.classesHint3A,
              isDense: true,
            ),
          ),
        ),
        // Bottom-pad the Save button so it lines up with the field baselines
        // when the controls sit on one row; harmless once they wrap.
        Padding(
          padding: const EdgeInsets.only(bottom: PlinkSpacing.s1),
          // The one constructive commit in the scope row: persisting the
          // school + code binding. The DS theme paints ElevatedButton in the
          // magenta spark.
          child: ElevatedButton(
            key: const Key('classes-save-codes-button'),
            onPressed: (!saving && dirty) ? onSave : null,
            child: saving
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
        ),
      ],
    );
  }
}

class _RosterTable extends StatelessWidget {
  const _RosterTable({required this.members, required this.onRemove});

  final List<ClassMember> members;
  final Future<void> Function(ClassMember member) onRemove;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    if (members.isEmpty) {
      return Align(
        alignment: Alignment.topLeft,
        child: Text(l10n.classesNoMembers, style: monoLabel(PlinkColors.muted)),
      );
    }
    // A hairline-bounded panel, not a raised card — the system uses borders,
    // never shadows. Members read as quiet instrument lines.
    return Container(
      decoration: BoxDecoration(
        border: Border.all(
          color: PlinkColors.hairline,
          width: PlinkBorders.width,
        ),
        borderRadius: BorderRadius.circular(PlinkRadius.base),
      ),
      clipBehavior: Clip.antiAlias,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(
              PlinkSpacing.s4,
              PlinkSpacing.s3,
              PlinkSpacing.s4,
              PlinkSpacing.s3,
            ),
            child: Row(
              children: [
                Expanded(
                  child: Text(
                    l10n.classesDisplayName,
                    style: monoLabel(PlinkColors.ink60),
                  ),
                ),
                ConstrainedBox(
                  constraints: const BoxConstraints(minWidth: 96),
                  child: Text(
                    l10n.classesRole,
                    style: monoLabel(PlinkColors.ink60),
                  ),
                ),
                const SizedBox(width: 40),
              ],
            ),
          ),
          const _Hairline(),
          Expanded(
            child: ListView.separated(
              padding: EdgeInsets.zero,
              itemCount: members.length,
              separatorBuilder: (_, _) => const _Hairline(),
              itemBuilder: (_, i) {
                final m = members[i];
                return Padding(
                  padding: const EdgeInsets.fromLTRB(
                    PlinkSpacing.s4,
                    PlinkSpacing.s2,
                    PlinkSpacing.s2,
                    PlinkSpacing.s2,
                  ),
                  child: Row(
                    children: [
                      Expanded(
                        child: Text(
                          m.displayName,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: Theme.of(context).textTheme.bodyMedium
                              ?.copyWith(color: PlinkColors.ink),
                        ),
                      ),
                      ConstrainedBox(
                        // A min-width slot keeps the role column roughly aligned
                        // with the header without capping the badge — so a wide
                        // test font can't clip "TEACHER" into an overflow.
                        constraints: const BoxConstraints(minWidth: 96),
                        child: Align(
                          alignment: Alignment.centerLeft,
                          widthFactor: 1,
                          // Role reads as a mono spec chip.
                          child: PlinkBadge(m.userRole),
                        ),
                      ),
                      IconButton(
                        icon: const Icon(Icons.close, size: 18),
                        color: PlinkColors.ink60,
                        tooltip: l10n.actionRemove,
                        onPressed: () => onRemove(m),
                      ),
                    ],
                  ),
                );
              },
            ),
          ),
        ],
      ),
    );
  }
}

class _ImportResultsBar extends StatelessWidget {
  const _ImportResultsBar({required this.results});

  final List<ClassMembershipImportResult> results;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final added = results
        .where((r) => r.status == ClassMembershipImportStatus.added)
        .length;
    final already = results
        .where((r) => r.status == ClassMembershipImportStatus.alreadyMember)
        .length;
    final unresolved = results
        .where((r) => r.status == ClassMembershipImportStatus.notFoundInEntra)
        .toList();
    final wrongSchool = results
        .where((r) => r.status == ClassMembershipImportStatus.wrongSchool)
        .toList();
    return Wrap(
      spacing: PlinkSpacing.s2,
      runSpacing: PlinkSpacing.s2,
      children: [
        // The constructive result is the one spark; the rest are calm specs.
        PlinkBadge(
          l10n.classesImportAdded(added),
          variant: BadgeVariant.accent,
        ),
        PlinkBadge(l10n.classesImportAlready(already)),
        if (unresolved.isNotEmpty)
          Tooltip(
            message: unresolved
                .map((r) => r.upn ?? r.entraOid ?? l10n.classesBlank)
                .join('\n'),
            child: PlinkBadge(l10n.classesImportUnresolved(unresolved.length)),
          ),
        if (wrongSchool.isNotEmpty)
          Tooltip(
            message: wrongSchool
                .map(
                  (r) =>
                      '${r.upn ?? r.entraOid ?? l10n.classesBlank} — ${r.detail ?? ''}',
                )
                .join('\n'),
            child: PlinkBadge(
              l10n.classesImportWrongSchool(wrongSchool.length),
            ),
          ),
      ],
    );
  }
}

/// The school year, as `YYYY-YYYY`, that the New class form fills in on
/// [date]. Belgian school years run Sept–June, so anything from August on
/// belongs to the year that just started; earlier months still belong to the
/// previous September's year.
///
/// Public so tests can work out the year the form will show today instead of
/// hard-coding one (#375).
String schoolYearFor(DateTime date) {
  final start = date.month >= 8 ? date.year : date.year - 1;
  return '$start-${start + 1}';
}

typedef CreateClassCallback =
    Future<ClassSummary> Function({
      required String name,
      required String schoolYear,
      String? schoolTag,
      String? classCode,
    });

/// Collects the fields for a new class and calls [onCreate], popping with the
/// resulting [ClassSummary] on success. Keeps its own busy/error state so a
/// duplicate-name 409 surfaces in-dialog rather than dismissing the form.
class _NewClassDialog extends StatefulWidget {
  const _NewClassDialog({required this.schools, required this.onCreate});

  final List<String> schools;
  final CreateClassCallback onCreate;

  @override
  State<_NewClassDialog> createState() => _NewClassDialogState();
}

class _NewClassDialogState extends State<_NewClassDialog> {
  final _name = TextEditingController();
  final _schoolYear = TextEditingController();
  final _classCode = TextEditingController();
  String? _schoolTag;
  bool _saving = false;
  ApiErrorMessage? _error;

  @override
  void initState() {
    super.initState();
    _schoolYear.text = schoolYearFor(DateTime.now());
  }

  @override
  void dispose() {
    _name.dispose();
    _schoolYear.dispose();
    _classCode.dispose();
    super.dispose();
  }

  bool get _valid =>
      _name.text.trim().isNotEmpty && _schoolYear.text.trim().isNotEmpty;

  Future<void> _submit() async {
    if (!_valid) return;
    final l10n = AppLocalizations.of(context);
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final created = await widget.onCreate(
        name: _name.text.trim(),
        schoolYear: _schoolYear.text.trim(),
        schoolTag: _schoolTag,
        classCode: _classCode.text.trim().isEmpty
            ? null
            : _classCode.text.trim(),
      );
      if (!mounted) return;
      Navigator.of(context).pop(created);
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _saving = false;
        // The name and year belong to an archived class (#395): say so, so
        // the teacher restores it rather than retrying.
        _error = isArchivedClassConflict(e)
            ? ApiErrorMessage(
                l10n.classesCreateArchivedClash(
                  _name.text.trim(),
                  _schoolYear.text.trim(),
                ),
              )
            : describeApiError(
                e,
                generic: l10n.classesCreateError,
                notAuthorized: l10n.apiError403,
              );
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return AlertDialog(
      title: Text(l10n.classesNewClass),
      content: SizedBox(
        width: 420,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            TextField(
              controller: _name,
              autofocus: true,
              enabled: !_saving,
              onChanged: (_) => setState(() {}),
              decoration: InputDecoration(
                labelText: l10n.classesNameLabel,
                hintText: l10n.classesHint3A,
              ),
            ),
            const SizedBox(height: PlinkSpacing.s3),
            TextField(
              controller: _schoolYear,
              enabled: !_saving,
              onChanged: (_) => setState(() {}),
              decoration: InputDecoration(
                labelText: l10n.classesSchoolYearLabel,
                hintText: l10n.classesSchoolYearHint,
              ),
            ),
            const SizedBox(height: PlinkSpacing.s3),
            DropdownButtonFormField<String?>(
              initialValue: _schoolTag,
              decoration: InputDecoration(
                labelText: l10n.classesSchoolOptionalLabel,
              ),
              items: [
                DropdownMenuItem<String?>(
                  value: null,
                  child: Text(l10n.commonNone),
                ),
                for (final s in widget.schools)
                  DropdownMenuItem<String?>(value: s, child: Text(s)),
              ],
              onChanged: _saving ? null : (v) => setState(() => _schoolTag = v),
            ),
            const SizedBox(height: PlinkSpacing.s3),
            TextField(
              controller: _classCode,
              enabled: !_saving,
              decoration: InputDecoration(
                labelText: l10n.classesClassCodeOptionalLabel,
                hintText: l10n.classesHint3A,
              ),
            ),
            if (_error != null) ...[
              const SizedBox(height: PlinkSpacing.s3),
              ApiErrorText(_error!),
            ],
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: _saving ? null : () => Navigator.of(context).pop(),
          child: Text(l10n.actionCancel),
        ),
        // The constructive commit wears the magenta spark.
        ElevatedButton(
          key: const Key('classes-create-button'),
          onPressed: (_valid && !_saving) ? _submit : null,
          child: _saving
              ? const SizedBox(
                  width: 16,
                  height: 16,
                  child: CircularProgressIndicator(
                    strokeWidth: 2,
                    color: PlinkColors.onInk,
                  ),
                )
              : Text(l10n.actionCreate),
        ),
      ],
    );
  }
}

/// What the teacher chose in [_DeleteClassDialog].
enum _DeleteChoice { delete, archive }

/// Confirms deleting a class (#395). A class without sessions gets a plain
/// confirm. One with sessions says how many go with it, and the students'
/// activity data with them, offers Archive as the way that keeps them, and
/// enables Delete only once the teacher has typed the class's name.
class _DeleteClassDialog extends StatefulWidget {
  const _DeleteClassDialog({required this.klass});

  final ClassSummary klass;

  @override
  State<_DeleteClassDialog> createState() => _DeleteClassDialogState();
}

class _DeleteClassDialogState extends State<_DeleteClassDialog> {
  final _typedName = TextEditingController();

  @override
  void dispose() {
    _typedName.dispose();
    super.dispose();
  }

  bool get _hasSessions => widget.klass.sessionCount > 0;

  bool get _confirmed =>
      !_hasSessions || _typedName.text.trim() == widget.klass.name;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final klass = widget.klass;
    return AlertDialog(
      title: Text(l10n.classesDeleteClass),
      content: !_hasSessions
          ? Text(l10n.classesDeleteClassBody(klass.name, klass.schoolYear))
          : SizedBox(
              width: 460,
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    l10n.classesDeleteWithSessionsBody(
                      klass.sessionCount,
                      klass.name,
                      klass.schoolYear,
                    ),
                  ),
                  if (!klass.isArchived) ...[
                    const SizedBox(height: PlinkSpacing.s3),
                    Text(l10n.classesDeleteArchiveInstead),
                  ],
                  const SizedBox(height: PlinkSpacing.s4),
                  TextField(
                    key: const Key('classes-delete-type-name'),
                    controller: _typedName,
                    autofocus: true,
                    onChanged: (_) => setState(() {}),
                    decoration: InputDecoration(
                      labelText: l10n.classesDeleteTypeName(klass.name),
                    ),
                  ),
                ],
              ),
            ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: Text(l10n.actionCancel),
        ),
        if (_hasSessions && !klass.isArchived)
          OutlinedButton(
            key: const Key('classes-delete-archive-instead'),
            onPressed: () => Navigator.of(context).pop(_DeleteChoice.archive),
            child: Text(l10n.classesArchive),
          ),
        FilledButton(
          key: const Key('classes-delete-confirm-button'),
          style: FilledButton.styleFrom(
            backgroundColor: Theme.of(context).colorScheme.error,
          ),
          onPressed: _confirmed
              ? () => Navigator.of(context).pop(_DeleteChoice.delete)
              : null,
          child: Text(
            _hasSessions ? l10n.classesDeleteWithSessions : l10n.actionDelete,
          ),
        ),
      ],
    );
  }
}

class _CsvPasteDialog extends StatefulWidget {
  const _CsvPasteDialog();

  @override
  State<_CsvPasteDialog> createState() => _CsvPasteDialogState();
}

class _CsvPasteDialogState extends State<_CsvPasteDialog> {
  final _controller = TextEditingController();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return AlertDialog(
      title: Text(l10n.classesCsvDialogTitle),
      content: SizedBox(
        width: 560,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(l10n.classesCsvDialogBody),
            const SizedBox(height: PlinkSpacing.s3),
            TextField(
              controller: _controller,
              maxLines: 12,
              decoration: InputDecoration(
                border: const OutlineInputBorder(),
                hintText: l10n.classesCsvHint,
              ),
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(null),
          child: Text(l10n.actionCancel),
        ),
        // Committing the paste into an import is the constructive action.
        ElevatedButton(
          onPressed: () => Navigator.of(context).pop(_controller.text),
          child: Text(l10n.actionImport),
        ),
      ],
    );
  }
}

/// A full-width 1px instrument rule — the system separates with hairlines,
/// never shadows.
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

/// A space-mono label style (sentence-case microcopy / specs) — the quiet
/// headers and counts that read like an instrument, never shouting. Mirrors the
/// live-session and bundles treatment.
TextStyle monoLabel(Color color) =>
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

/// Tabular-figure mono for values that read as a spec (the school year).
TextStyle monoSpec(Color color) => TextStyle(
  fontFamily: PlinkType.monoFamily,
  package: PlinkType.fontPackage,
  fontFamilyFallback: PlinkType.monoFallback,
  fontSize: PlinkType.textSm,
  color: color,
  height: 1.4,
  fontFeatures: const <FontFeature>[FontFeature.tabularFigures()],
);

class CsvParseResult {
  CsvParseResult({required this.rows, this.error});
  final List<ImportRow> rows;
  final String? error;
}

/// Parses a CSV roster keyed on `upn` (header required, order-insensitive).
/// Tolerates leading/trailing whitespace and quoted values, skips blank lines.
/// The backend resolves each UPN to an Entra OID + display name at import time
/// and reports any that don't resolve, so we don't validate UPN shape here.
CsvParseResult parseRosterCsv(String csv, AppLocalizations l10n) {
  final lines = csv
      .split(RegExp(r'\r?\n'))
      .where((l) => l.trim().isNotEmpty)
      .toList();
  if (lines.isEmpty) {
    return CsvParseResult(rows: const [], error: l10n.classesCsvEmpty);
  }
  final header = _splitCsvLine(
    lines.first,
  ).map((s) => s.toLowerCase()).toList();
  final upnIdx = header.indexOf('upn');
  if (upnIdx < 0) {
    return CsvParseResult(rows: const [], error: l10n.classesCsvHeaderMissing);
  }
  final rows = <ImportRow>[];
  for (var i = 1; i < lines.length; i++) {
    final cells = _splitCsvLine(lines[i]);
    if (cells.length <= upnIdx) continue;
    final upn = cells[upnIdx].trim();
    if (upn.isEmpty) continue;
    rows.add(ImportRow(upn: upn));
  }
  return CsvParseResult(rows: rows);
}

List<String> _splitCsvLine(String line) {
  final out = <String>[];
  final buf = StringBuffer();
  var inQuotes = false;
  for (var i = 0; i < line.length; i++) {
    final c = line[i];
    if (inQuotes) {
      if (c == '"') {
        if (i + 1 < line.length && line[i + 1] == '"') {
          buf.write('"');
          i++;
        } else {
          inQuotes = false;
        }
      } else {
        buf.write(c);
      }
    } else if (c == '"') {
      inQuotes = true;
    } else if (c == ',') {
      out.add(buf.toString().trim());
      buf.clear();
    } else {
      buf.write(c);
    }
  }
  out.add(buf.toString().trim());
  return out;
}
