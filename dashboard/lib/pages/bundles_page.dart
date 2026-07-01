import 'package:flutter/material.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../api/bundles_api.dart';
import '../api/sessions_api.dart';
import '../bundles/bundle_file_io.dart';
import '../bundles/bundle_format.dart';
import '../l10n/app_localizations.dart';

/// Admin-only catalogue editor for bundles (#75), redesigned to the paper
/// treatment (AD5, #170).
///
/// Two panes on the same flush-left margin as the shell: a hairline list of
/// bundles (each row a quiet instrument line, version + archived shown as mono
/// spec chips) and, beside it, the editor for the selected (or new) bundle —
/// the name field, separate Domains and Apps sections, and a Test field that
/// checks the current draft against a URL or process name without saving.
///
/// Magenta is the single spark, reserved for the one constructive commit on the
/// page: the Save / Create button. Every other affordance (New bundle, Add
/// entry, Check, Delete / Archive) stays calm ink so the editor reads like an
/// instrument, not a console of buttons. Edits take effect at the next session
/// start (footer); live updates to active sessions are out of scope.
class BundlesPage extends StatefulWidget {
  const BundlesPage({
    super.key,
    required this.bundles,
    required this.sessions,
    this.fileIo,
  });

  final BundlesApi bundles;
  final SessionsApi sessions;

  /// Browser file-IO seam for import/export (#304). Null in production — the
  /// page lazily builds the real `package:web` implementation; an integration
  /// test injects a fake so the flow runs without an OS file dialog.
  final BundleFileIo? fileIo;

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
  bool _porting = false;

  // Built lazily so production uses the real package:web seam while tests that
  // never touch import/export don't have to supply one.
  late final BundleFileIo _fileIo = widget.fileIo ?? createBundleFileIo();

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
      // Read l10n here (not before the first await): _bootstrap runs from
      // initState, where depending on an inherited widget is illegal until the
      // first frame. By the catch, the element is mounted and context is valid.
      setState(
        () => _error = AppLocalizations.of(context).bundlesLoadError('$e'),
      );
    }
  }

  Future<void> _refreshList() async {
    final l10n = AppLocalizations.of(context);
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
      setState(() => _error = l10n.bundlesLoadListError('$e'));
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _openBundle(BundleSummary summary) async {
    final l10n = AppLocalizations.of(context);
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
      setState(() => _error = l10n.bundlesLoadOneError('$e'));
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
      _entries.add(
        _EntryRow(
          kind: kind,
          matchType: kind == BundleEntryKind.domain
              ? BundleEntryMatchType.wildcard
              : BundleEntryMatchType.exact,
          value: '',
        ),
      );
    });
  }

  void _removeEntry(_EntryRow row) {
    setState(() => _entries.remove(row));
  }

  Future<void> _save() async {
    final l10n = AppLocalizations.of(context);
    final name = _nameController.text.trim();
    if (name.isEmpty) {
      setState(() => _error = l10n.bundlesNameRequired);
      return;
    }
    final entries = <BundleEntry>[];
    for (final row in _entries) {
      final value = row.controller.text.trim();
      if (value.isEmpty) {
        setState(() => _error = l10n.bundlesEntryValueRequired);
        return;
      }
      final validation = _validateEntry(l10n, row.kind, row.matchType, value);
      if (validation != null) {
        setState(() => _error = validation);
        return;
      }
      entries.add(
        BundleEntry(kind: row.kind, value: value, matchType: row.matchType),
      );
    }
    if (entries.isEmpty) {
      setState(() => _error = l10n.bundlesEntryAtLeastOne);
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
      setState(() => _error = l10n.bundlesSaveError('$e'));
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _archive() async {
    final l10n = AppLocalizations.of(context);
    final selected = _selected;
    if (selected == null) return;
    final confirm = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(l10n.bundlesArchiveTitle),
        content: Text(l10n.bundlesArchiveBody(selected.name)),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(ctx, false),
            child: Text(l10n.actionCancel),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(ctx, true),
            child: Text(l10n.bundlesArchive),
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
      setState(() => _error = l10n.bundlesArchiveError('$e'));
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _delete() async {
    final l10n = AppLocalizations.of(context);
    final selected = _selected;
    if (selected == null) return;
    final confirm = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(l10n.bundlesDeleteTitle),
        content: Text(l10n.bundlesDeleteBody(selected.name)),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(ctx, false),
            child: Text(l10n.actionCancel),
          ),
          FilledButton(
            style: FilledButton.styleFrom(
              backgroundColor: Theme.of(context).colorScheme.error,
            ),
            onPressed: () => Navigator.pop(ctx, true),
            child: Text(l10n.actionDelete),
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
      setState(() => _error = l10n.bundlesDeleteError('$e'));
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  void _runTest() {
    final l10n = AppLocalizations.of(context);
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
        _testResult = l10n.bundlesTestNoMatch(probe);
      } else {
        _testResult = l10n.bundlesTestMatch(
          hit.controller.text.trim(),
          _kindLabel(l10n, hit.kind),
          _matchTypeLabel(l10n, hit.matchType),
        );
      }
    });
  }

  // ---- import / export (#304) ----

  /// Downloads the selected bundle as a single bare-object JSON file.
  Future<void> _exportSelected() async {
    final selected = _selected;
    if (selected == null) return;
    final data = BundleData(
      name: selected.name,
      entries: selected.entries
          .map(
            (e) => BundleEntry(
              kind: e.kind,
              value: e.value,
              matchType: e.matchType,
            ),
          )
          .toList(),
    );
    _fileIo.downloadJson(
      '${bundleFileNameStem(selected.name)}.json',
      exportBundleToJson(data),
    );
  }

  /// Downloads every bundle in the current view as one envelope JSON file. The
  /// list only carries summaries, so this fetches each bundle's entries (N+1,
  /// fine for an admin catalogue of a handful of bundles).
  Future<void> _exportAll() async {
    final l10n = AppLocalizations.of(context);
    final list = _list;
    if (list == null || list.isEmpty) {
      _snack(l10n.bundlesNothingToExport);
      return;
    }
    setState(() {
      _porting = true;
      _error = null;
    });
    try {
      final data = <BundleData>[];
      for (final summary in list) {
        final detail = await widget.bundles.get(summary.id);
        data.add(
          BundleData(
            name: detail.name,
            entries: detail.entries
                .map(
                  (e) => BundleEntry(
                    kind: e.kind,
                    value: e.value,
                    matchType: e.matchType,
                  ),
                )
                .toList(),
          ),
        );
      }
      _fileIo.downloadJson('bundles.json', exportBundlesToJson(data));
      _snack(l10n.bundlesExported(data.length));
    } catch (e) {
      _snack(l10n.bundlesExportError('$e'));
    } finally {
      if (mounted) setState(() => _porting = false);
    }
  }

  /// Picks a JSON file, validates it, and upserts each bundle by name: an
  /// existing name (archived or not) is updated, a new one is created.
  Future<void> _import() async {
    final l10n = AppLocalizations.of(context);
    final raw = await _fileIo.pickJsonFile();
    if (raw == null) return; // No file chosen.

    final parsed = parseBundlesJson(raw);
    if (!parsed.ok) {
      await _showImportErrors(parsed.errors);
      return;
    }

    setState(() {
      _porting = true;
      _error = null;
    });
    try {
      // Match by name across the whole catalogue (including archived) so an
      // archived bundle is updated/un-archived in place rather than duplicated —
      // create only guards name uniqueness among non-archived bundles.
      final existing = await widget.bundles.list(includeArchived: true);
      final idByName = {for (final b in existing) b.name.toLowerCase(): b.id};

      var created = 0;
      var updated = 0;
      final failures = <String>[];
      for (final bundle in parsed.bundles) {
        try {
          final id = idByName[bundle.name.toLowerCase()];
          if (id == null) {
            await widget.bundles.create(bundle.name, bundle.entries);
            created++;
          } else {
            await widget.bundles.update(id, bundle.name, bundle.entries);
            updated++;
          }
        } catch (e) {
          failures.add('"${bundle.name}": $e');
        }
      }

      await _refreshList();
      if (failures.isEmpty) {
        _snack(l10n.bundlesImported(parsed.bundles.length, created, updated));
      } else {
        await _showImportErrors(
          failures,
          title: l10n.bundlesImportedWithFailures(
            failures.length,
            created,
            updated,
          ),
        );
      }
    } catch (e) {
      _snack(l10n.bundlesImportError('$e'));
    } finally {
      if (mounted) setState(() => _porting = false);
    }
  }

  void _snack(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(
      context,
    ).showSnackBar(SnackBar(content: Text(message)));
  }

  Future<void> _showImportErrors(List<String> errors, {String? title}) async {
    if (!mounted) return;
    await showDialog<void>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(title ?? AppLocalizations.of(ctx).bundlesImportRejected),
        content: SizedBox(
          width: 460,
          child: SingleChildScrollView(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: [
                for (final e in errors)
                  Padding(
                    padding: const EdgeInsets.only(bottom: PlinkSpacing.s2),
                    child: Text('• $e'),
                  ),
              ],
            ),
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(ctx),
            child: Text(AppLocalizations.of(ctx).actionClose),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    if (_denied) {
      return Scaffold(
        backgroundColor: PlinkColors.paper,
        body: Center(
          child: Text(AppLocalizations.of(context).bundlesAdminRequired),
        ),
      );
    }

    return Scaffold(
      backgroundColor: PlinkColors.paper,
      body: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          SizedBox(width: 300, child: _buildList()),
          // A vertical hairline between the panes — the system separates with
          // rules, never shadows.
          const SizedBox(
            width: PlinkBorders.width,
            child: ColoredBox(color: PlinkColors.hairline),
          ),
          Expanded(child: _buildEditor()),
        ],
      ),
    );
  }

  Widget _buildList() {
    final list = _list;
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
                  AppLocalizations.of(context).bundlesCatalogue,
                  style: _monoLabel(PlinkColors.ink60),
                ),
              ),
              Text(
                AppLocalizations.of(context).badgeArchived,
                style: _monoLabel(PlinkColors.muted),
              ),
              const SizedBox(width: PlinkSpacing.s2),
              // Compact so the toggle sits on the label baseline rather than
              // eating the row height.
              Tooltip(
                message: AppLocalizations.of(context).bundlesShowArchived,
                child: Transform.scale(
                  scale: 0.8,
                  child: Switch(
                    value: _includeArchived,
                    onChanged: (v) {
                      setState(() => _includeArchived = v);
                      _refreshList();
                    },
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
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // Calm ink action — creating a draft is navigation, not the
              // constructive commit. The magenta spark is reserved for Save.
              OutlinedButton.icon(
                key: const Key('bundles-new-button'),
                icon: const Icon(Icons.add, size: 18),
                label: Text(AppLocalizations.of(context).bundlesNewBundle),
                onPressed: _startNew,
              ),
              const SizedBox(height: PlinkSpacing.s1),
              // Import / export sit a tier quieter than New bundle — backup and
              // bulk-authoring affordances, not the primary create path (#304).
              Row(
                children: [
                  Expanded(
                    child: TextButton.icon(
                      key: const Key('bundles-import-button'),
                      icon: const Icon(Icons.upload_file, size: 18),
                      label: Text(AppLocalizations.of(context).actionImport),
                      onPressed: _porting ? null : _import,
                    ),
                  ),
                  Expanded(
                    child: TextButton.icon(
                      key: const Key('bundles-export-all-button'),
                      icon: const Icon(Icons.download, size: 18),
                      label: Text(
                        AppLocalizations.of(context).bundlesExportAll,
                      ),
                      onPressed: _porting ? null : _exportAll,
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
        const _Hairline(),
        Expanded(
          child: _loading && list == null
              ? const Center(child: CircularProgressIndicator())
              : list == null || list.isEmpty
              ? Center(
                  child: Text(
                    AppLocalizations.of(context).bundlesNoBundles,
                    style: _monoLabel(PlinkColors.muted),
                  ),
                )
              : ListView.separated(
                  padding: EdgeInsets.zero,
                  itemCount: list.length,
                  separatorBuilder: (_, _) => const _Hairline(),
                  itemBuilder: (context, i) {
                    final b = list[i];
                    return _BundleRow(
                      summary: b,
                      selected: _selected?.id == b.id,
                      onTap: () => _openBundle(b),
                    );
                  },
                ),
        ),
      ],
    );
  }

  Widget _buildEditor() {
    final l10n = AppLocalizations.of(context);
    if (_selected == null && !_isNewDraft) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(PlinkSpacing.s6),
          child: Text(
            l10n.bundlesSelectOrNew,
            style: _monoLabel(PlinkColors.muted),
            textAlign: TextAlign.center,
          ),
        ),
      );
    }
    final selected = _selected;
    final domains = _entries
        .where((e) => e.kind == BundleEntryKind.domain)
        .toList();
    final apps = _entries.where((e) => e.kind == BundleEntryKind.app).toList();

    return SingleChildScrollView(
      padding: const EdgeInsets.fromLTRB(
        PlinkSpacing.s6,
        PlinkSpacing.s5,
        PlinkSpacing.s6,
        PlinkSpacing.s8,
      ),
      child: Align(
        alignment: Alignment.centerLeft,
        child: ConstrainedBox(
          // The editor column: a readable measure that keeps the entry rows
          // from stretching uncomfortably wide on a maximised window.
          constraints: const BoxConstraints(maxWidth: 720),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                crossAxisAlignment: CrossAxisAlignment.center,
                children: [
                  Expanded(
                    child: TextField(
                      controller: _nameController,
                      style: Theme.of(context).textTheme.titleMedium,
                      decoration: InputDecoration(
                        labelText: l10n.bundlesNameLabel,
                        isDense: true,
                      ),
                    ),
                  ),
                  // Version + archived as mono spec chips — the bundle's specs,
                  // read like an instrument label, never shouting.
                  if (selected != null) ...[
                    const SizedBox(width: PlinkSpacing.s3),
                    PlinkBadge('v${selected.version}'),
                    if (selected.isArchived) ...[
                      const SizedBox(width: PlinkSpacing.s2),
                      PlinkBadge(l10n.badgeArchived),
                    ],
                  ],
                ],
              ),
              const SizedBox(height: PlinkSpacing.s6),
              _EntrySection(
                title: AppLocalizations.of(context).bundlesDomains,
                rows: domains,
                kind: BundleEntryKind.domain,
                onAdd: () => _addEntry(BundleEntryKind.domain),
                onRemove: _removeEntry,
                onChanged: () => setState(() {}),
              ),
              const SizedBox(height: PlinkSpacing.s6),
              _EntrySection(
                title: AppLocalizations.of(context).bundlesApps,
                rows: apps,
                kind: BundleEntryKind.app,
                onAdd: () => _addEntry(BundleEntryKind.app),
                onRemove: _removeEntry,
                onChanged: () => setState(() {}),
              ),
              const SizedBox(height: PlinkSpacing.s6),
              _buildTester(),
              const SizedBox(height: PlinkSpacing.s6),
              if (_error != null) ...[
                Text(
                  _error!,
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: Theme.of(context).colorScheme.error,
                  ),
                ),
                const SizedBox(height: PlinkSpacing.s4),
              ],
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      _buildDestructiveAction(),
                      // Export the saved bundle as a JSON file (#304). Only the
                      // persisted version is exportable — an unsaved draft has
                      // no stable shape to round-trip, so this hides for a new
                      // draft.
                      if (selected != null) ...[
                        const SizedBox(width: PlinkSpacing.s2),
                        Tooltip(
                          message: l10n.bundlesExportTooltip,
                          child: OutlinedButton.icon(
                            key: const Key('bundles-export-button'),
                            icon: const Icon(Icons.download, size: 18),
                            label: Text(l10n.bundlesExport),
                            onPressed: _porting ? null : _exportSelected,
                          ),
                        ),
                      ],
                    ],
                  ),
                  // The one magenta spark on the page: the constructive commit.
                  // The DS theme paints ElevatedButton in the spark.
                  ElevatedButton(
                    key: const Key('bundles-save-button'),
                    onPressed: _saving ? null : _save,
                    child: _saving
                        ? const SizedBox(
                            width: 16,
                            height: 16,
                            child: CircularProgressIndicator(
                              strokeWidth: 2,
                              color: PlinkColors.onInk,
                            ),
                          )
                        : Text(
                            _isNewDraft ? l10n.actionCreate : l10n.actionSave,
                          ),
                  ),
                ],
              ),
              const SizedBox(height: PlinkSpacing.s3),
              Text(
                l10n.bundlesEditsFooter,
                style: _monoLabel(PlinkColors.muted),
              ),
            ],
          ),
        ),
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
        message: AppLocalizations.of(context).bundlesDeleteTooltip,
        child: OutlinedButton.icon(
          icon: const Icon(Icons.delete_outline, size: 18),
          label: Text(AppLocalizations.of(context).actionDelete),
          onPressed: _saving ? null : _delete,
        ),
      );
    }

    if (!selected.isArchived) {
      return Tooltip(
        message: AppLocalizations.of(context).bundlesArchiveTooltip,
        child: OutlinedButton.icon(
          icon: const Icon(Icons.archive_outlined, size: 18),
          label: Text(AppLocalizations.of(context).bundlesArchive),
          onPressed: _saving ? null : _archive,
        ),
      );
    }

    return const SizedBox.shrink();
  }

  Widget _buildTester() {
    // A hairline-bounded panel, not a raised card — the system uses borders,
    // never shadows.
    return Container(
      decoration: BoxDecoration(
        border: Border.all(
          color: PlinkColors.hairline,
          width: PlinkBorders.width,
        ),
        borderRadius: BorderRadius.circular(PlinkRadius.base),
      ),
      padding: const EdgeInsets.all(PlinkSpacing.s4),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            AppLocalizations.of(context).bundlesTest,
            style: _monoLabel(PlinkColors.ink60),
          ),
          const SizedBox(height: PlinkSpacing.s2),
          Text(
            AppLocalizations.of(context).bundlesTestHint,
            style: Theme.of(
              context,
            ).textTheme.bodySmall?.copyWith(color: PlinkColors.ink60),
          ),
          const SizedBox(height: PlinkSpacing.s3),
          Row(
            children: [
              Expanded(
                child: TextField(
                  controller: _testController,
                  decoration: InputDecoration(
                    hintText: AppLocalizations.of(context).bundlesTestFieldHint,
                    isDense: true,
                  ),
                  onSubmitted: (_) => _runTest(),
                ),
              ),
              const SizedBox(width: PlinkSpacing.s3),
              OutlinedButton(
                onPressed: _runTest,
                child: Text(AppLocalizations.of(context).bundlesCheck),
              ),
            ],
          ),
          if (_testResult != null) ...[
            const SizedBox(height: PlinkSpacing.s3),
            Text(
              _testResult!,
              style: _monoSpec(PlinkColors.ink, PlinkType.textSm),
            ),
          ],
        ],
      ),
    );
  }

  // ---- validation + match preview (kept in sync with backend rules) ----

  static String? _validateEntry(
    AppLocalizations l10n,
    BundleEntryKind kind,
    BundleEntryMatchType matchType,
    String value,
  ) {
    switch (kind) {
      case BundleEntryKind.domain:
        if (matchType == BundleEntryMatchType.signedPublisher) {
          return l10n.bundlesValSignedPublisherDomain;
        }
        final ok = RegExp(
          r'^(\*\.)?([a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?)(\.[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?)+$',
        ).hasMatch(value);
        if (!ok) return l10n.bundlesValInvalidDomain(value);
        return null;
      case BundleEntryKind.app:
        if (matchType == BundleEntryMatchType.wildcard ||
            matchType == BundleEntryMatchType.suffix) {
          return l10n.bundlesValMatchTypeApp(_matchTypeLabel(l10n, matchType));
        }
        if (matchType == BundleEntryMatchType.exact) {
          if (value.contains('\\') || value.contains('/')) {
            return l10n.bundlesValProcessPath(value);
          }
          if (value.toLowerCase().endsWith('.exe')) {
            return l10n.bundlesValProcessExe(value);
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

  static String _kindLabel(AppLocalizations l10n, BundleEntryKind kind) =>
      switch (kind) {
        BundleEntryKind.domain => l10n.bundlesKindDomain,
        BundleEntryKind.app => l10n.bundlesKindApp,
      };

  static String _matchTypeLabel(
    AppLocalizations l10n,
    BundleEntryMatchType type,
  ) => switch (type) {
    BundleEntryMatchType.exact => l10n.bundlesMatchExact,
    BundleEntryMatchType.wildcard => l10n.bundlesMatchWildcard,
    BundleEntryMatchType.suffix => l10n.bundlesMatchSuffix,
    BundleEntryMatchType.signedPublisher => l10n.bundlesMatchSignedPublisher,
  };
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
/// live-session page treatment.
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

/// Tabular-figure mono for values that read as a spec (the match result).
TextStyle _monoSpec(Color color, double size) => TextStyle(
  fontFamily: PlinkType.monoFamily,
  package: PlinkType.fontPackage,
  fontFamilyFallback: PlinkType.monoFallback,
  fontSize: size,
  color: color,
  height: 1.4,
  fontFeatures: const <FontFeature>[FontFeature.tabularFigures()],
);

/// One catalogue row — a hairline instrument line. The bundle name reads first;
/// its version is a mono spec chip and an archived bundle wears a muted badge.
/// The selected row is marked by a paper-tint fill and a magenta edge tick (the
/// same spark the nav uses as its active indicator), never a heavy highlight.
class _BundleRow extends StatelessWidget {
  const _BundleRow({
    required this.summary,
    required this.selected,
    required this.onTap,
  });

  final BundleSummary summary;
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
                  PlinkSpacing.s3,
                  PlinkSpacing.s3,
                ),
                child: Row(
                  children: [
                    Expanded(
                      child: Text(
                        summary.name,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: Theme.of(
                          context,
                        ).textTheme.bodyLarge?.copyWith(color: PlinkColors.ink),
                      ),
                    ),
                    if (summary.isArchived) ...[
                      PlinkBadge(AppLocalizations.of(context).badgeArchived),
                      const SizedBox(width: PlinkSpacing.s2),
                    ],
                    PlinkBadge('v${summary.version}'),
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
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
            Expanded(child: Text(title, style: _monoLabel(PlinkColors.ink60))),
            // Calm ink affordance — only the Save commit wears the spark.
            TextButton.icon(
              icon: const Icon(Icons.add, size: 18),
              label: Text(AppLocalizations.of(context).actionAdd),
              onPressed: onAdd,
            ),
          ],
        ),
        const SizedBox(height: PlinkSpacing.s2),
        if (rows.isEmpty)
          Text(
            kind == BundleEntryKind.domain
                ? AppLocalizations.of(context).bundlesNoDomainEntries
                : AppLocalizations.of(context).bundlesNoAppEntries,
            style: _monoLabel(PlinkColors.muted),
          )
        else
          for (final row in rows)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: PlinkSpacing.s1),
              child: Row(
                children: [
                  SizedBox(
                    // 200 (not 160) so the widest option label
                    // "SignedPublisher" fits the dropdown's inner row without
                    // a RenderFlex overflow (#115). Fixed width keeps the
                    // match-type column aligned across rows.
                    width: 200,
                    child: DropdownButtonFormField<BundleEntryMatchType>(
                      initialValue: row.matchType,
                      isDense: true,
                      // Fill the box and ellipsize rather than overflow, so a
                      // long label can never trip a RenderFlex error even if
                      // metrics differ (font/locale) from the 200px budget.
                      isExpanded: true,
                      decoration: const InputDecoration(isDense: true),
                      items: [
                        for (final t in allowedMatchTypes)
                          DropdownMenuItem(
                            value: t,
                            child: Text(
                              _matchTypeLabel(AppLocalizations.of(context), t),
                            ),
                          ),
                      ],
                      onChanged: (v) {
                        if (v == null) return;
                        row.matchType = v;
                        onChanged();
                      },
                    ),
                  ),
                  const SizedBox(width: PlinkSpacing.s2),
                  Expanded(
                    child: TextField(
                      controller: row.controller,
                      decoration: InputDecoration(
                        hintText: kind == BundleEntryKind.domain
                            ? AppLocalizations.of(context).bundlesDomainHint
                            : AppLocalizations.of(context).bundlesAppHint,
                        isDense: true,
                      ),
                    ),
                  ),
                  IconButton(
                    icon: const Icon(Icons.remove_circle_outline),
                    color: PlinkColors.ink60,
                    tooltip: AppLocalizations.of(context).bundlesRemoveEntry,
                    onPressed: () => onRemove(row),
                  ),
                ],
              ),
            ),
      ],
    );
  }

  static String _matchTypeLabel(
    AppLocalizations l10n,
    BundleEntryMatchType type,
  ) => switch (type) {
    BundleEntryMatchType.exact => l10n.bundlesMatchExact,
    BundleEntryMatchType.wildcard => l10n.bundlesMatchWildcard,
    BundleEntryMatchType.suffix => l10n.bundlesMatchSuffix,
    BundleEntryMatchType.signedPublisher => l10n.bundlesMatchSignedPublisher,
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
