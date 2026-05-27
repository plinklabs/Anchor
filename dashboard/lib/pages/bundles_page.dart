import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../api/bundles_api.dart';
import '../api/sessions_api.dart';

/// Admin-only catalogue editor for bundles (#75).
///
/// Left pane: bundles + archived ones (toggleable).
/// Right pane: editor for the selected (or new) bundle with separate Apps
/// and Domains tables. A "Test" field checks the current draft against a
/// URL or process name without saving. Edits take effect at the next
/// session start (footer); live updates to active sessions are out of scope.
class BundlesPage extends StatefulWidget {
  const BundlesPage({super.key, required this.bundles, required this.sessions});

  final BundlesApi bundles;
  final SessionsApi sessions;

  @override
  State<BundlesPage> createState() => _BundlesPageState();
}

class _BundlesPageState extends State<BundlesPage> {
  bool _loading = false;
  bool _denied = false;
  bool _includeArchived = false;
  List<BundleSummary>? _list;
  BundleDetail? _selected;
  bool _isNewDraft = false;
  String? _error;

  // Editor draft state (separate so cancellable).
  final TextEditingController _nameController = TextEditingController();
  List<_EntryRow> _entries = [];
  final TextEditingController _testController = TextEditingController();
  String? _testResult;
  bool _saving = false;

  @override
  void initState() {
    super.initState();
    _bootstrap();
  }

  @override
  void dispose() {
    _nameController.dispose();
    _testController.dispose();
    super.dispose();
  }

  Future<void> _bootstrap() async {
    try {
      final me = await widget.sessions.me();
      if (!mounted) return;
      if (!me.isAdmin) {
        setState(() => _denied = true);
        return;
      }
      await _refreshList();
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Could not load: $e');
    }
  }

  Future<void> _refreshList() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final list = await widget.bundles.list(includeArchived: _includeArchived);
      if (!mounted) return;
      setState(() {
        _list = list;
        if (_selected != null) {
          // Reload the selected bundle so version/entries reflect server state.
          final match = list.where((b) => b.id == _selected!.id).toList();
          if (match.isEmpty) {
            _clearEditor();
          }
        }
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Could not load bundles: $e');
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _openBundle(BundleSummary summary) async {
    setState(() => _loading = true);
    try {
      final detail = await widget.bundles.get(summary.id);
      if (!mounted) return;
      setState(() {
        _selected = detail;
        _isNewDraft = false;
        _nameController.text = detail.name;
        _entries = detail.entries.map(_EntryRow.fromEntry).toList();
        _testController.clear();
        _testResult = null;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Failed to load bundle: $e');
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  void _startNew() {
    setState(() {
      _selected = null;
      _isNewDraft = true;
      _nameController.text = '';
      _entries = [
        _EntryRow(
          kind: BundleEntryKind.domain,
          matchType: BundleEntryMatchType.wildcard,
          value: '',
        ),
      ];
      _testController.clear();
      _testResult = null;
    });
  }

  void _clearEditor() {
    setState(() {
      _selected = null;
      _isNewDraft = false;
      _nameController.text = '';
      _entries = [];
      _testController.clear();
      _testResult = null;
    });
  }

  void _addEntry(BundleEntryKind kind) {
    setState(() {
      _entries.add(_EntryRow(
        kind: kind,
        matchType: kind == BundleEntryKind.domain
            ? BundleEntryMatchType.wildcard
            : BundleEntryMatchType.exact,
        value: '',
      ));
    });
  }

  void _removeEntry(_EntryRow row) {
    setState(() => _entries.remove(row));
  }

  Future<void> _save() async {
    final name = _nameController.text.trim();
    if (name.isEmpty) {
      setState(() => _error = 'Name is required.');
      return;
    }
    final entries = <BundleEntry>[];
    for (final row in _entries) {
      final value = row.controller.text.trim();
      if (value.isEmpty) {
        setState(() => _error = 'Every entry must have a value.');
        return;
      }
      final validation = _validateEntry(row.kind, row.matchType, value);
      if (validation != null) {
        setState(() => _error = validation);
        return;
      }
      entries.add(BundleEntry(kind: row.kind, value: value, matchType: row.matchType));
    }
    if (entries.isEmpty) {
      setState(() => _error = 'At least one entry is required.');
      return;
    }

    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      BundleDetail saved;
      if (_isNewDraft || _selected == null) {
        saved = await widget.bundles.create(name, entries);
      } else {
        saved = await widget.bundles.update(_selected!.id, name, entries);
      }
      if (!mounted) return;
      setState(() {
        _selected = saved;
        _isNewDraft = false;
        _nameController.text = saved.name;
        _entries = saved.entries.map(_EntryRow.fromEntry).toList();
      });
      await _refreshList();
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Save failed: $e');
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _archive() async {
    final selected = _selected;
    if (selected == null) return;
    final confirm = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Archive bundle?'),
        content: Text(
          '"${selected.name}" will be hidden from the picker. '
          'Past sessions referencing it stay intact. You can restore it later by editing.',
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(ctx, false), child: const Text('Cancel')),
          FilledButton(
            onPressed: () => Navigator.pop(ctx, true),
            child: const Text('Archive'),
          ),
        ],
      ),
    );
    if (confirm != true) return;
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      await widget.bundles.archive(selected.id);
      if (!mounted) return;
      _clearEditor();
      await _refreshList();
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Archive failed: $e');
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _delete() async {
    final selected = _selected;
    if (selected == null) return;
    final confirm = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Delete bundle?'),
        content: Text(
          '"${selected.name}" will be permanently removed. '
          'This cannot be undone. Available because no session has ever used this bundle.',
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(ctx, false), child: const Text('Cancel')),
          FilledButton(
            style: FilledButton.styleFrom(
              backgroundColor: Theme.of(context).colorScheme.error,
            ),
            onPressed: () => Navigator.pop(ctx, true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
    if (confirm != true) return;
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      await widget.bundles.hardDelete(selected.id);
      if (!mounted) return;
      _clearEditor();
      await _refreshList();
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Delete failed: $e');
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  void _runTest() {
    final probe = _testController.text.trim();
    if (probe.isEmpty) {
      setState(() => _testResult = null);
      return;
    }
    _EntryRow? hit;
    for (final row in _entries) {
      if (_entryMatchesProbe(row, probe)) {
        hit = row;
        break;
      }
    }
    setState(() {
      if (hit == null) {
        _testResult = 'No entry matches "$probe".';
      } else {
        _testResult =
            'Matches "${hit.controller.text.trim()}" '
            '(${_kindLabel(hit.kind)} / ${_matchTypeLabel(hit.matchType)}).';
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    if (_denied) {
      return Scaffold(
        appBar: AppBar(
          title: const Text('Bundles'),
          leading: IconButton(
            icon: const Icon(Icons.arrow_back),
            onPressed: () => context.go('/'),
          ),
        ),
        body: const Center(
          child: Text('Admin access required.'),
        ),
      );
    }

    return Scaffold(
      appBar: AppBar(
        title: const Text('Bundles'),
        leading: IconButton(
          icon: const Icon(Icons.arrow_back),
          onPressed: () => context.go('/'),
        ),
        actions: [
          IconButton(
            tooltip: 'New bundle',
            icon: const Icon(Icons.add),
            onPressed: _startNew,
          ),
        ],
      ),
      body: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          SizedBox(width: 320, child: _buildList()),
          const VerticalDivider(width: 1),
          Expanded(child: _buildEditor()),
        ],
      ),
    );
  }

  Widget _buildList() {
    final list = _list;
    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.all(8),
          child: Row(
            children: [
              Expanded(
                child: Text(
                  'Catalogue',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
              ),
              Tooltip(
                message: 'Show archived',
                child: Switch(
                  value: _includeArchived,
                  onChanged: (v) {
                    setState(() => _includeArchived = v);
                    _refreshList();
                  },
                ),
              ),
            ],
          ),
        ),
        const Divider(height: 1),
        Expanded(
          child: _loading && list == null
              ? const Center(child: CircularProgressIndicator())
              : list == null || list.isEmpty
                  ? const Center(child: Text('No bundles.'))
                  : ListView.builder(
                      itemCount: list.length,
                      itemBuilder: (context, i) {
                        final b = list[i];
                        final selected = _selected?.id == b.id;
                        return ListTile(
                          selected: selected,
                          title: Text(b.name),
                          subtitle: Text('v${b.version}'),
                          trailing: b.isArchived
                              ? const Chip(
                                  label: Text('archived'),
                                  visualDensity: VisualDensity.compact,
                                )
                              : null,
                          onTap: () => _openBundle(b),
                        );
                      },
                    ),
        ),
      ],
    );
  }

  Widget _buildEditor() {
    if (_selected == null && !_isNewDraft) {
      return const Center(
        child: Text('Select a bundle or press + to create a new one.'),
      );
    }
    final theme = Theme.of(context);
    final domains = _entries.where((e) => e.kind == BundleEntryKind.domain).toList();
    final apps = _entries.where((e) => e.kind == BundleEntryKind.app).toList();

    return SingleChildScrollView(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: TextField(
                  controller: _nameController,
                  decoration: const InputDecoration(
                    labelText: 'Name',
                    border: OutlineInputBorder(),
                  ),
                ),
              ),
              const SizedBox(width: 12),
              if (_selected != null)
                Text(
                  'v${_selected!.version}${_selected!.isArchived ? " (archived)" : ""}',
                  style: theme.textTheme.bodyMedium,
                ),
            ],
          ),
          const SizedBox(height: 24),
          _EntrySection(
            title: 'Domains',
            rows: domains,
            kind: BundleEntryKind.domain,
            onAdd: () => _addEntry(BundleEntryKind.domain),
            onRemove: _removeEntry,
            onChanged: () => setState(() {}),
          ),
          const SizedBox(height: 24),
          _EntrySection(
            title: 'Apps',
            rows: apps,
            kind: BundleEntryKind.app,
            onAdd: () => _addEntry(BundleEntryKind.app),
            onRemove: _removeEntry,
            onChanged: () => setState(() {}),
          ),
          const SizedBox(height: 24),
          _buildTester(),
          const SizedBox(height: 24),
          if (_error != null) ...[
            Text(_error!, style: TextStyle(color: theme.colorScheme.error)),
            const SizedBox(height: 12),
          ],
          Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              _buildDestructiveAction(),
              FilledButton.icon(
                icon: _saving
                    ? const SizedBox(
                        width: 16,
                        height: 16,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Icon(Icons.save),
                label: Text(_isNewDraft ? 'Create' : 'Save'),
                onPressed: _saving ? null : _save,
              ),
            ],
          ),
          const SizedBox(height: 12),
          Text(
            'Edits take effect at the next session start.',
            style: theme.textTheme.bodySmall?.copyWith(fontStyle: FontStyle.italic),
          ),
        ],
      ),
    );
  }

  /// Per #89: surface Delete when the bundle has never been bound to a
  /// session; otherwise fall back to Archive (the historical-reproducibility
  /// guarantee makes hard delete impossible). Archived-but-never-used bundles
  /// still get the Delete option as a cleanup path.
  Widget _buildDestructiveAction() {
    final selected = _selected;
    if (selected == null) return const SizedBox.shrink();

    if (!selected.hasBeenUsed) {
      return Tooltip(
        message: 'Permanently delete — this bundle has never been used in a session.',
        child: OutlinedButton.icon(
          icon: const Icon(Icons.delete_outline),
          label: const Text('Delete'),
          onPressed: _saving ? null : _delete,
        ),
      );
    }

    if (!selected.isArchived) {
      return Tooltip(
        message: 'Hide from the picker. Hard delete is not possible because this bundle has been used in past sessions.',
        child: OutlinedButton.icon(
          icon: const Icon(Icons.archive_outlined),
          label: const Text('Archive'),
          onPressed: _saving ? null : _archive,
        ),
      );
    }

    return const SizedBox.shrink();
  }

  Widget _buildTester() {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text('Test', style: Theme.of(context).textTheme.titleSmall),
            const SizedBox(height: 4),
            Text(
              'Paste a URL or process name to see whether the current draft matches.',
              style: Theme.of(context).textTheme.bodySmall,
            ),
            const SizedBox(height: 8),
            Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _testController,
                    decoration: const InputDecoration(
                      hintText: 'e.g. https://www.geogebra.org/calc',
                      border: OutlineInputBorder(),
                      isDense: true,
                    ),
                    onSubmitted: (_) => _runTest(),
                  ),
                ),
                const SizedBox(width: 8),
                FilledButton(onPressed: _runTest, child: const Text('Check')),
              ],
            ),
            if (_testResult != null) ...[
              const SizedBox(height: 8),
              Text(_testResult!),
            ],
          ],
        ),
      ),
    );
  }

  // ---- validation + match preview (kept in sync with backend rules) ----

  static String? _validateEntry(BundleEntryKind kind, BundleEntryMatchType matchType, String value) {
    switch (kind) {
      case BundleEntryKind.domain:
        if (matchType == BundleEntryMatchType.signedPublisher) {
          return 'SignedPublisher is not valid for a domain.';
        }
        final ok = RegExp(
          r'^(\*\.)?([a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?)(\.[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?)+$',
        ).hasMatch(value);
        if (!ok) return '"$value" is not a valid domain.';
        return null;
      case BundleEntryKind.app:
        if (matchType == BundleEntryMatchType.wildcard ||
            matchType == BundleEntryMatchType.suffix) {
          return '${_matchTypeLabel(matchType)} is not valid for an app.';
        }
        if (matchType == BundleEntryMatchType.exact) {
          if (value.contains('\\') || value.contains('/')) {
            return 'Process name "$value" must not include a path.';
          }
          if (value.toLowerCase().endsWith('.exe')) {
            return 'Process name "$value" must not include the .exe suffix.';
          }
        }
        return null;
    }
  }

  bool _entryMatchesProbe(_EntryRow row, String probe) {
    final value = row.controller.text.trim();
    if (value.isEmpty) return false;
    if (row.kind == BundleEntryKind.domain) {
      String? host;
      final uri = Uri.tryParse(probe);
      if (uri != null && uri.hasScheme && uri.host.isNotEmpty) {
        host = uri.host.toLowerCase();
      } else {
        host = probe.toLowerCase();
      }
      switch (row.matchType) {
        case BundleEntryMatchType.exact:
          return host == value.toLowerCase();
        case BundleEntryMatchType.wildcard:
          final pattern = value.toLowerCase();
          if (!pattern.startsWith('*.')) return host == pattern;
          final tail = pattern.substring(2);
          return host == tail || host.endsWith('.$tail');
        case BundleEntryMatchType.suffix:
          final tail = value.toLowerCase();
          return host == tail || host.endsWith('.$tail');
        case BundleEntryMatchType.signedPublisher:
          return false;
      }
    } else {
      switch (row.matchType) {
        case BundleEntryMatchType.exact:
          return probe.toLowerCase() == value.toLowerCase();
        case BundleEntryMatchType.signedPublisher:
          return probe.toLowerCase() == value.toLowerCase();
        default:
          return false;
      }
    }
  }

  static String _kindLabel(BundleEntryKind kind) => switch (kind) {
    BundleEntryKind.domain => 'Domain',
    BundleEntryKind.app => 'App',
  };

  static String _matchTypeLabel(BundleEntryMatchType type) => switch (type) {
    BundleEntryMatchType.exact => 'Exact',
    BundleEntryMatchType.wildcard => 'Wildcard',
    BundleEntryMatchType.suffix => 'Suffix',
    BundleEntryMatchType.signedPublisher => 'SignedPublisher',
  };
}

class _EntrySection extends StatelessWidget {
  const _EntrySection({
    required this.title,
    required this.rows,
    required this.kind,
    required this.onAdd,
    required this.onRemove,
    required this.onChanged,
  });

  final String title;
  final List<_EntryRow> rows;
  final BundleEntryKind kind;
  final VoidCallback onAdd;
  final void Function(_EntryRow row) onRemove;
  final VoidCallback onChanged;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final allowedMatchTypes = kind == BundleEntryKind.domain
        ? const [
            BundleEntryMatchType.exact,
            BundleEntryMatchType.wildcard,
            BundleEntryMatchType.suffix,
          ]
        : const [
            BundleEntryMatchType.exact,
            BundleEntryMatchType.signedPublisher,
          ];

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            Expanded(child: Text(title, style: theme.textTheme.titleMedium)),
            TextButton.icon(
              icon: const Icon(Icons.add),
              label: const Text('Add'),
              onPressed: onAdd,
            ),
          ],
        ),
        const SizedBox(height: 8),
        if (rows.isEmpty)
          Text(
            'No ${title.toLowerCase()} entries.',
            style: theme.textTheme.bodySmall,
          )
        else
          for (final row in rows) ...[
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 4),
              child: Row(
                children: [
                  SizedBox(
                    width: 160,
                    child: DropdownButtonFormField<BundleEntryMatchType>(
                      initialValue: row.matchType,
                      isDense: true,
                      decoration: const InputDecoration(
                        border: OutlineInputBorder(),
                        isDense: true,
                      ),
                      items: [
                        for (final t in allowedMatchTypes)
                          DropdownMenuItem(
                            value: t,
                            child: Text(_matchTypeLabel(t)),
                          ),
                      ],
                      onChanged: (v) {
                        if (v == null) return;
                        row.matchType = v;
                        onChanged();
                      },
                    ),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: TextField(
                      controller: row.controller,
                      decoration: InputDecoration(
                        hintText: kind == BundleEntryKind.domain
                            ? 'e.g. *.geogebra.org'
                            : 'e.g. msedge',
                        border: const OutlineInputBorder(),
                        isDense: true,
                      ),
                    ),
                  ),
                  IconButton(
                    icon: const Icon(Icons.remove_circle_outline),
                    onPressed: () => onRemove(row),
                  ),
                ],
              ),
            ),
          ],
      ],
    );
  }

  static String _matchTypeLabel(BundleEntryMatchType type) => switch (type) {
    BundleEntryMatchType.exact => 'Exact',
    BundleEntryMatchType.wildcard => 'Wildcard',
    BundleEntryMatchType.suffix => 'Suffix',
    BundleEntryMatchType.signedPublisher => 'SignedPublisher',
  };
}

class _EntryRow {
  _EntryRow({
    required this.kind,
    required this.matchType,
    required String value,
  }) : controller = TextEditingController(text: value);

  factory _EntryRow.fromEntry(BundleEntry entry) => _EntryRow(
        kind: entry.kind,
        matchType: entry.matchType,
        value: entry.value,
      );

  BundleEntryKind kind;
  BundleEntryMatchType matchType;
  final TextEditingController controller;
}
