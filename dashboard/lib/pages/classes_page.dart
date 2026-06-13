import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../api/classes_api.dart';
import '../api/sessions_api.dart';
import 'add_student_search.dart';

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
  String? _error;
  List<ClassMembershipImportResult>? _lastImportResults;

  // Editable copies of the selected class's schoolTag / classCode. Sit
  // alongside [_selected] (which mirrors the server) until the teacher hits
  // Save; allows them to walk away from edits by re-selecting a class.
  String? _editSchoolTag;
  final _classCodeController = TextEditingController();

  @override
  void initState() {
    super.initState();
    _loadClasses();
    _loadSchools();
  }

  @override
  void dispose() {
    _classCodeController.dispose();
    super.dispose();
  }

  Future<void> _loadClasses() async {
    setState(() {
      _loadingClasses = true;
      _error = null;
    });
    try {
      final list = await widget.sessions.classes();
      if (!mounted) return;
      setState(() {
        _classes = list;
        if (_selected == null && list.isNotEmpty) {
          _selectClass(list.first, refreshRoster: false);
        }
      });
      if (_selected != null) {
        await _loadRoster(_selected!);
      }
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Could not load classes: $e');
    } finally {
      if (mounted) setState(() => _loadingClasses = false);
    }
  }

  Future<void> _loadSchools() async {
    setState(() => _loadingSchools = true);
    try {
      final schools = await widget.classes.schools();
      if (!mounted) return;
      setState(() => _schools = schools);
    } catch (_) {
      // Non-fatal — the selector will fall back to a free-text affordance
      // and the page surfaces other errors via [_error]. Listing the school
      // tags is a discovery convenience, not a gate.
      if (!mounted) return;
      setState(() => _schools = const <String>[]);
    } finally {
      if (mounted) setState(() => _loadingSchools = false);
    }
  }

  Future<void> _loadRoster(ClassSummary klass) async {
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
      setState(() => _error = 'Could not load roster: $e');
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

  Future<void> _saveCodes() async {
    final klass = _selected;
    if (klass == null) return;
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
      setState(() => _error = 'Could not save school + code: $e');
    } finally {
      if (mounted) setState(() => _savingCodes = false);
    }
  }

  Future<void> _addMember(String entraOid, String? displayName) async {
    final klass = _selected;
    if (klass == null) return;
    try {
      await widget.classes.addMember(
        klass.id,
        entraOid: entraOid,
        displayName: displayName,
      );
      await _loadRoster(klass);
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Failed to add member: $e');
    }
  }

  Future<void> _removeMember(ClassMember member) async {
    final klass = _selected;
    if (klass == null) return;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Remove member'),
        content: Text(
          'Remove ${member.displayName} from ${klass.name}? '
          'They will stop receiving session broadcasts on the next session start.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            child: const Text('Remove'),
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
      setState(() => _error = 'Failed to remove member: $e');
    }
  }

  Future<void> _importCsv() async {
    final klass = _selected;
    if (klass == null) return;
    final pasted = await showDialog<String>(
      context: context,
      builder: (_) => const _CsvPasteDialog(),
    );
    if (pasted == null || pasted.trim().isEmpty) return;

    final parsed = parseRosterCsv(pasted);
    if (parsed.rows.isEmpty) {
      setState(() => _error = parsed.error ?? 'No valid rows in CSV.');
      return;
    }
    try {
      final results = await widget.classes.importMembers(klass.id, parsed.rows);
      if (!mounted) return;
      setState(() => _lastImportResults = results);
      await _loadRoster(klass);
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Import failed: $e');
    }
  }

  Future<void> _bulkImportFromGraph() async {
    final klass = _selected;
    if (klass == null) return;
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
      setState(() => _error = 'Populate from Graph failed: $e');
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
      appBar: AppBar(
        title: const Text('Classes'),
        leading: IconButton(
          icon: const Icon(Icons.arrow_back),
          onPressed: () => context.go('/'),
        ),
      ),
      body: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          SizedBox(
            width: 260,
            child: _ClassList(
              classes: _classes,
              selected: _selected,
              loading: _loadingClasses,
              onSelect: _selectClass,
            ),
          ),
          const VerticalDivider(width: 1),
          Expanded(
            child: _selected == null
                ? const Center(child: Text('Pick a class on the left.'))
                : _RosterPane(
                    klass: _selected!,
                    roster: _roster,
                    schools: _schools,
                    loadingSchools: _loadingSchools,
                    loadingRoster: _loadingRoster,
                    savingCodes: _savingCodes,
                    bulkImporting: _bulkImporting,
                    codesDirty: _codesDirty,
                    editSchoolTag: _editSchoolTag,
                    classCodeController: _classCodeController,
                    onSchoolChanged: (v) =>
                        setState(() => _editSchoolTag = v),
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
    required this.onSelect,
  });

  final List<ClassSummary>? classes;
  final ClassSummary? selected;
  final bool loading;
  final void Function(ClassSummary) onSelect;

  @override
  Widget build(BuildContext context) {
    if (loading && classes == null) {
      return const Center(child: CircularProgressIndicator());
    }
    final list = classes ?? const <ClassSummary>[];
    if (list.isEmpty) {
      return const Padding(
        padding: EdgeInsets.all(16),
        child: Text('No classes you teach.'),
      );
    }
    return ListView.builder(
      itemCount: list.length,
      itemBuilder: (_, i) {
        final c = list[i];
        return ListTile(
          title: Text(c.name),
          subtitle: Text(c.schoolYear),
          selected: selected?.id == c.id,
          onTap: () => onSelect(c),
        );
      },
    );
  }
}

class _RosterPane extends StatelessWidget {
  const _RosterPane({
    required this.klass,
    required this.roster,
    required this.schools,
    required this.loadingSchools,
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
  final ClassMembersResponse? roster;
  final List<String>? schools;
  final bool loadingSchools;
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
  final String? error;
  final List<ClassMembershipImportResult>? lastImportResults;
  final Future<List<DirectoryUser>> Function(String query) onSearch;
  final Future<void> Function(String entraOid, String? displayName) onAdd;
  final Future<void> Function(ClassMember member) onRemove;
  final Future<void> Function() onImport;

  @override
  Widget build(BuildContext context) {
    final scopeReady =
        klass.schoolTag != null && (klass.classCode ?? '').isNotEmpty;
    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            '${klass.name} (${klass.schoolYear})',
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 12),
          _ScopeRow(
            schools: schools,
            loadingSchools: loadingSchools,
            saving: savingCodes,
            dirty: codesDirty,
            editSchoolTag: editSchoolTag,
            classCodeController: classCodeController,
            onSchoolChanged: onSchoolChanged,
            onClassCodeChanged: onClassCodeChanged,
            onSave: onSaveCodes,
          ),
          const SizedBox(height: 16),
          Wrap(
            spacing: 12,
            runSpacing: 12,
            crossAxisAlignment: WrapCrossAlignment.start,
            children: [
              AddStudentSearch(
                onSearch: onSearch,
                onAdd: onAdd,
                disabled: !scopeReady,
                disabledReason: 'Set school + code first',
              ),
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: OutlinedButton.icon(
                  icon: const Icon(Icons.upload_file),
                  label: const Text('Import CSV'),
                  onPressed: scopeReady ? onImport : null,
                ),
              ),
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: FilledButton.icon(
                  icon: bulkImporting
                      ? const SizedBox(
                          width: 16,
                          height: 16,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Icon(Icons.cloud_download),
                  label: const Text('Populate from Graph'),
                  onPressed: (scopeReady && !bulkImporting) ? onBulkImport : null,
                ),
              ),
            ],
          ),
          if (error != null) ...[
            const SizedBox(height: 12),
            Text(
              error!,
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
          ],
          if (lastImportResults != null) ...[
            const SizedBox(height: 12),
            _ImportResultsBar(results: lastImportResults!),
          ],
          const SizedBox(height: 16),
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
  final bool saving;
  final bool dirty;
  final String? editSchoolTag;
  final TextEditingController classCodeController;
  final void Function(String?) onSchoolChanged;
  final void Function(String) onClassCodeChanged;
  final Future<void> Function() onSave;

  @override
  Widget build(BuildContext context) {
    final options = schools ?? const <String>[];
    // If the class is bound to a tag the directory no longer reports, keep
    // it in the dropdown so the teacher doesn't silently lose the binding.
    final entries = <String>{
      ...options,
      if (editSchoolTag != null && editSchoolTag!.isNotEmpty) editSchoolTag!,
    }.toList();
    return Wrap(
      spacing: 12,
      runSpacing: 12,
      crossAxisAlignment: WrapCrossAlignment.end,
      children: [
        SizedBox(
          width: 220,
          child: DropdownButtonFormField<String?>(
            initialValue: editSchoolTag,
            decoration: InputDecoration(
              labelText: 'School',
              helperText: loadingSchools ? 'Loading…' : null,
            ),
            items: [
              const DropdownMenuItem<String?>(
                value: null,
                child: Text('(none)'),
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
            decoration: const InputDecoration(
              labelText: 'Class code',
              hintText: 'e.g. 3A',
            ),
          ),
        ),
        // Bottom-pad the Save button so it lines up with the field baselines
        // when the controls sit on one row; harmless once they wrap.
        Padding(
          padding: const EdgeInsets.only(bottom: 4),
          child: FilledButton.icon(
            icon: saving
                ? const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.save),
            label: const Text('Save'),
            onPressed: (!saving && dirty) ? onSave : null,
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
    if (members.isEmpty) {
      return const Center(child: Text('No members yet.'));
    }
    return SingleChildScrollView(
      scrollDirection: Axis.vertical,
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        child: DataTable(
          columns: const [
            DataColumn(label: Text('Display name')),
            DataColumn(label: Text('Role')),
            DataColumn(label: Text('')),
          ],
          rows: [
            for (final m in members)
              DataRow(
                cells: [
                  DataCell(Text(m.displayName)),
                  DataCell(Text(m.userRole)),
                  DataCell(
                    IconButton(
                      icon: const Icon(Icons.close),
                      tooltip: 'Remove',
                      onPressed: () => onRemove(m),
                    ),
                  ),
                ],
              ),
          ],
        ),
      ),
    );
  }
}

class _ImportResultsBar extends StatelessWidget {
  const _ImportResultsBar({required this.results});

  final List<ClassMembershipImportResult> results;

  @override
  Widget build(BuildContext context) {
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
      spacing: 12,
      children: [
        _chip(context, '$added added', Colors.green),
        _chip(context, '$already already member', Colors.amber),
        if (unresolved.isNotEmpty)
          Tooltip(
            message: unresolved
                .map((r) => r.upn ?? r.entraOid ?? '(blank)')
                .join('\n'),
            child: _chip(context, '${unresolved.length} unresolved', Colors.red),
          ),
        if (wrongSchool.isNotEmpty)
          Tooltip(
            message: wrongSchool
                .map((r) => '${r.upn ?? r.entraOid ?? '(blank)'} — ${r.detail ?? ''}')
                .join('\n'),
            child: _chip(
              context,
              '${wrongSchool.length} wrong school',
              Colors.deepOrange,
            ),
          ),
      ],
    );
  }

  Widget _chip(BuildContext context, String text, MaterialColor color) {
    return Chip(
      backgroundColor: color.shade100,
      label: Text(text, style: TextStyle(color: color.shade900)),
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
    return AlertDialog(
      title: const Text('Import roster from CSV'),
      content: SizedBox(
        width: 560,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text(
              'Paste a CSV with a header row. Required column: upn '
              '(the user principal name, e.g. student@school.be). '
              'Names are looked up in the directory automatically.',
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _controller,
              maxLines: 12,
              decoration: const InputDecoration(
                border: OutlineInputBorder(),
                hintText: 'upn\nalice@school.be\nbob@school.be\n...',
              ),
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(null),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () => Navigator.of(context).pop(_controller.text),
          child: const Text('Import'),
        ),
      ],
    );
  }
}

class CsvParseResult {
  CsvParseResult({required this.rows, this.error});
  final List<ImportRow> rows;
  final String? error;
}

/// Parses a CSV roster keyed on `upn` (header required, order-insensitive).
/// Tolerates leading/trailing whitespace and quoted values, skips blank lines.
/// The backend resolves each UPN to an Entra OID + display name at import time
/// and reports any that don't resolve, so we don't validate UPN shape here.
CsvParseResult parseRosterCsv(String csv) {
  final lines = csv
      .split(RegExp(r'\r?\n'))
      .where((l) => l.trim().isNotEmpty)
      .toList();
  if (lines.isEmpty) {
    return CsvParseResult(rows: const [], error: 'CSV is empty.');
  }
  final header = _splitCsvLine(lines.first).map((s) => s.toLowerCase()).toList();
  final upnIdx = header.indexOf('upn');
  if (upnIdx < 0) {
    return CsvParseResult(rows: const [], error: 'Header must include upn.');
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
