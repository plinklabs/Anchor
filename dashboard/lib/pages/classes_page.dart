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
  bool _loadingClasses = false;
  bool _loadingRoster = false;
  String? _error;
  List<ClassMembershipImportResult>? _lastImportResults;

  @override
  void initState() {
    super.initState();
    _loadClasses();
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
        _selected ??= list.isNotEmpty ? list.first : null;
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

  void _selectClass(ClassSummary klass) {
    setState(() {
      _selected = klass;
      _roster = null;
      _lastImportResults = null;
    });
    _loadRoster(klass);
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
                    loading: _loadingRoster,
                    error: _error,
                    lastImportResults: _lastImportResults,
                    onSearch: widget.classes.searchUsers,
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
    required this.loading,
    required this.error,
    required this.lastImportResults,
    required this.onSearch,
    required this.onAdd,
    required this.onRemove,
    required this.onImport,
  });

  final ClassSummary klass;
  final ClassMembersResponse? roster;
  final bool loading;
  final String? error;
  final List<ClassMembershipImportResult>? lastImportResults;
  final Future<List<DirectoryUser>> Function(String query) onSearch;
  final Future<void> Function(String entraOid, String? displayName) onAdd;
  final Future<void> Function(ClassMember member) onRemove;
  final Future<void> Function() onImport;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            '${klass.name} (${klass.schoolYear})',
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 16),
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              AddStudentSearch(onSearch: onSearch, onAdd: onAdd),
              const SizedBox(width: 12),
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: OutlinedButton.icon(
                  icon: const Icon(Icons.upload_file),
                  label: const Text('Import CSV'),
                  onPressed: onImport,
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
            child: loading && roster == null
                ? const Center(child: CircularProgressIndicator())
                : roster == null
                ? const SizedBox.shrink()
                : _RosterTable(
                    members: roster!.members,
                    onRemove: onRemove,
                  ),
          ),
        ],
      ),
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
    final failed = results
        .where((r) => r.status == ClassMembershipImportStatus.notFoundInEntra)
        .length;
    return Wrap(
      spacing: 12,
      children: [
        _chip(context, '$added added', Colors.green),
        _chip(context, '$already already member', Colors.amber),
        if (failed > 0) _chip(context, '$failed unresolved', Colors.red),
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
              'Paste a CSV with a header row. Required columns: display_name, entra_oid.',
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _controller,
              maxLines: 12,
              decoration: const InputDecoration(
                border: OutlineInputBorder(),
                hintText:
                    'display_name,entra_oid\nAlice,00000000-0000-0000-0000-000000000001\n...',
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

/// Parses a CSV roster of the shape `display_name,entra_oid` (header
/// required, order-insensitive). Tolerates leading/trailing whitespace and
/// quoted values, skips blank lines. Rows missing a valid GUID are dropped.
CsvParseResult parseRosterCsv(String csv) {
  final lines = csv
      .split(RegExp(r'\r?\n'))
      .where((l) => l.trim().isNotEmpty)
      .toList();
  if (lines.isEmpty) {
    return CsvParseResult(rows: const [], error: 'CSV is empty.');
  }
  final header = _splitCsvLine(lines.first).map((s) => s.toLowerCase()).toList();
  final oidIdx = header.indexOf('entra_oid');
  final nameIdx = header.indexOf('display_name');
  if (oidIdx < 0) {
    return CsvParseResult(
      rows: const [],
      error: 'Header must include entra_oid.',
    );
  }
  final rows = <ImportRow>[];
  for (var i = 1; i < lines.length; i++) {
    final cells = _splitCsvLine(lines[i]);
    if (cells.length <= oidIdx) continue;
    final oid = _tryParseGuid(cells[oidIdx]);
    if (oid == null) continue;
    final name = nameIdx >= 0 && cells.length > nameIdx ? cells[nameIdx] : null;
    rows.add(ImportRow(entraOid: oid, displayName: name));
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

String? _tryParseGuid(String raw) {
  final s = raw.trim();
  final match = RegExp(
    r'^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$',
  ).firstMatch(s);
  return match == null ? null : s;
}
