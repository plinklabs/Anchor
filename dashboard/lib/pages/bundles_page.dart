import 'package:flutter/material.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../api/bundles_api.dart';
import '../api/sessions_api.dart';
import '../bundles/bundle_file_io.dart';
import '../bundles/bundle_format.dart';
import '../l10n/app_localizations.dart';
import '../widgets/api_error_text.dart';

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
  bool _denied = false;
  bool _includeArchived = false;
  List<BundleSummary>? _list;
  BundleDetail? _selected;
  bool _isNewDraft = false;

  /// A failure of the editor's own actions (validation, save, archive,
  /// delete), drawn in the editor under the tester. It belongs to the bundle
  /// or draft the editor holds, and goes when the editor leaves it (#385).
  ApiErrorMessage? _error;

  /// A failed catalogue load: `me()` or `list()` (#384). Drawn in the list
  /// pane, where the catalogue goes, so it shows whether or not a bundle is
  /// open, and a catalogue that never loaded doesn't read as "No bundles.".
  ApiErrorMessage? _loadError;

  /// A failed open of one bundle (#384) and the row it was for, which Retry
  /// opens again. Drawn in the editor pane in place of the select-a-bundle
  /// placeholder.
  ApiErrorMessage? _openError;
  BundleSummary? _openFailed;

  /// The open the admin asked for last (#387). Each open ([_openBundle],
  /// including a failed open's Retry) takes the next number, and New bundle
  /// and Reopen move it on too ([_supersedeOpen]), since they move the editor
  /// themselves. An open whose `get()` answers with the number moved on was
  /// superseded: it leaves the editor, [_opening] and a failed-open notice
  /// alone, so clicking B and then C ends on C even when B answers last.
  int _openRequest = 0;

  /// Whether the open the admin asked for last ([_openRequest]) is still
  /// waiting on `get()`. An earlier open that answers meanwhile doesn't turn
  /// it off (#387).
  bool _opening = false;

  /// Which bundle or draft the editor holds, bumped each time it leaves one
  /// ([_leaveEditor]). Save, Archive and Delete note it before they wait on
  /// the backend, and when the answer lands after the admin has opened
  /// another bundle or started a new one ([_editorStillOn]), they leave that
  /// editor alone: a slow Save on A that fails must not show its error under
  /// B, and one that succeeds must not put A back in the editor (#385). A
  /// failure is reported naming A instead ([_reportLateFailure], #386).
  int _editorGeneration = 0;

  /// The page's own messenger, so its snack bars go with the page: a late
  /// failure's Reopen ([_reportLateFailure]) can't be tapped once the page,
  /// and with it the draft it puts back, is gone (#386). A failure that lands
  /// after the admin has left the page goes to the app's messenger instead
  /// (#388), as does the outcome of an Import or Export all (#392).
  final GlobalKey<ScaffoldMessengerState> _messenger =
      GlobalKey<ScaffoldMessengerState>();

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
      final l10n = AppLocalizations.of(context);
      setState(
        () => _loadError = describeApiError(
          e,
          generic: l10n.bundlesLoadListError,
          notAuthorized: l10n.apiError403Admin,
        ),
      );
    }
  }

  /// Retry after a failed catalogue load. Runs the whole bootstrap again, as
  /// the failure may have been the admin check (`me()`) rather than the list.
  void _retryLoad() {
    setState(() => _loadError = null);
    _bootstrap();
  }

  Future<void> _refreshList() async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _loadError = null;
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
      setState(
        () => _loadError = describeApiError(
          e,
          generic: l10n.bundlesLoadListError,
          notAuthorized: l10n.apiError403Admin,
        ),
      );
    }
  }

  Future<void> _openBundle(BundleSummary summary) async {
    final l10n = AppLocalizations.of(context);
    // This open supersedes any the admin asked for before it (#387).
    final request = ++_openRequest;
    setState(() {
      _opening = true;
      _openError = null;
      _openFailed = null;
    });
    try {
      final detail = await widget.bundles.get(summary.id);
      // The admin has since asked for another bundle, New bundle or Reopen:
      // that one owns the editor and the spinner now (#387).
      if (!mounted || request != _openRequest) return;
      setState(() {
        _leaveEditor();
        _opening = false;
        _selected = detail;
        _isNewDraft = false;
        _nameController.text = detail.name;
        _entries = detail.entries.map(_EntryRow.fromEntry).toList();
      });
    } catch (e) {
      // A superseded open's failure isn't the admin's concern any more: it
      // must not replace what they asked for since, or that one's failed-open
      // notice. It needs no notice of its own either, as nothing of theirs
      // was lost: the row is still there to open again (#387).
      if (!mounted || request != _openRequest) return;
      // The admin asked to leave whatever was open for this bundle, as a
      // successful open would have. Say why it didn't open where the editor
      // goes, not under another bundle's editor or nowhere at all.
      _clearEditor();
      setState(() {
        _opening = false;
        _openError = describeApiError(
          e,
          generic: l10n.bundlesLoadOneError,
          notAuthorized: l10n.apiError403Admin,
        );
        _openFailed = summary;
      });
    }
  }

  /// Drops any open still waiting on `get()`, because the admin has moved
  /// the editor some other way (New bundle, Reopen): when it answers, it
  /// finds [_openRequest] moved on and leaves the editor alone (#387). Call
  /// inside setState.
  ///
  /// [_clearEditor] doesn't call this. Its one caller that can run with an
  /// open pending is the catalogue reload finding the held bundle gone, and
  /// the admin didn't ask for that: the open they asked for still lands.
  void _supersedeOpen() {
    _openRequest++;
    _opening = false;
  }

  void _startNew() {
    setState(() {
      _supersedeOpen();
      _leaveEditor();
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
    });
  }

  void _clearEditor() {
    setState(() {
      _leaveEditor();
      _selected = null;
      _isNewDraft = false;
      _nameController.text = '';
      _entries = [];
    });
  }

  /// The editor leaves the bundle or draft it held, for another one or for
  /// nothing. Call inside setState. What belonged to the one it leaves goes
  /// with it: the error from its Save, Archive or Delete (#385), any of those
  /// still waiting on the backend, a failed open, and the tester's probe and
  /// result. A failed catalogue load ([_loadError]) is about the list, not a
  /// bundle, so it stays.
  void _leaveEditor() {
    _editorGeneration++;
    _error = null;
    _openError = null;
    _openFailed = null;
    _testController.clear();
    _testResult = null;
  }

  /// Whether the answer to an action of the editor (Save, Archive, Delete)
  /// that noted [generation] before it waited on the backend is still for
  /// the bundle or draft the admin is on: the editor holds the same one, and
  /// the admin hasn't asked to open another ([_opening], which is only ever
  /// about the open they asked for last, #387). If not, a success leaves the
  /// editor alone (#385), and a failure is reported naming its bundle
  /// (#386), not shown under an editor that holds, or is about to hold,
  /// another bundle.
  bool _editorStillOn(int generation) =>
      generation == _editorGeneration && !_opening;

  /// Whether the admin is still on this page (#388): it is mounted, and
  /// neither its route nor a route around it (the admin area's, the app
  /// shell's) has been taken off its navigator. Going to another page (Home,
  /// or Admins in the admin sub-nav) takes one off at once, but the page stays
  /// mounted while it animates out, so a failure that lands then would show
  /// on a page the admin has already left, and go with it.
  ///
  /// A route under a dialog or a dropdown menu is still on its navigator, so
  /// those don't count as leaving.
  bool get _onPage {
    if (!mounted) return false;
    BuildContext? at = context;
    while (at != null) {
      if (ModalRoute.isActiveOf(at) == false) return false;
      at = at.findAncestorStateOfType<NavigatorState>()?.context;
    }
    return true;
  }

  /// The app's own messenger, the root one that MaterialApp provides. An
  /// action notes it before it waits on the backend, while the page's
  /// `context` can still look it up: it outlives the page, so a failure that
  /// lands after the admin has left can still be reported (#388).
  ScaffoldMessengerState? _appMessenger() =>
      context.findRootAncestorStateOfType<ScaffoldMessengerState>();

  /// The app's root navigator. An Import notes it with the app's messenger
  /// ([_appMessenger]) before it waits, so that a notice of what it could
  /// not do, landing after the admin has left the page, can open the list
  /// over whichever page they are on (#392).
  NavigatorState? _rootNavigator() =>
      Navigator.maybeOf(context, rootNavigator: true);

  /// Shows a snack bar where the admin is (#388, #392), which [build] makes,
  /// told whether that is still this page ([_onPage]). If it is, it goes
  /// through the page's own messenger, and goes with the page (#386). If the
  /// admin has left, that messenger is gone or going with the page, so it
  /// goes through [app], the app's messenger the action noted before it
  /// waited on the backend ([_appMessenger]).
  void _tell(
    ScaffoldMessengerState? app,
    SnackBar Function(bool onPage) build,
  ) {
    if (_onPage) {
      _messenger.currentState?.showSnackBar(build(true));
    } else if (app != null && app.mounted) {
      app.showSnackBar(build(false));
    }
  }

  /// Reports a failure of an action on a bundle or draft the admin has moved
  /// on from: another bundle or a new draft (#386), or another page (#388).
  /// It can't go under the editor (#385), so a snack bar names the bundle
  /// instead: [failed] says what failed for which bundle ("Could not save
  /// "Exam apps"."), and the reason follows, [reason] when the caller knows it
  /// (a 409) or else [describeApiError]'s, with the calm admin wording for a
  /// 403. It lands while the admin is elsewhere, so it stays until they close
  /// it.
  ///
  /// With the admin still on the page, it goes through the page's messenger,
  /// and [onReopen], given when the unsaved edit can be put back in the
  /// editor, is its action. With the admin gone from the page, it goes
  /// through [app], the app's messenger noted before the action waited
  /// ([_appMessenger]), and has no Reopen: the draft and the editor it would
  /// go back in went with the page. [l10n] was noted then too, as `context`
  /// can't be used once the page is gone.
  void _reportLateFailure(
    AppLocalizations l10n,
    ScaffoldMessengerState? app,
    String failed,
    Object error, {
    String? reason,
    VoidCallback? onReopen,
  }) {
    final why =
        reason ??
        describeApiError(
          error,
          generic: l10n.bundlesTryAgain,
          notAuthorized: l10n.apiError403Admin,
        ).text;
    _tell(
      app,
      (onPage) => SnackBar(
        content: Text('$failed $why'),
        persist: true,
        showCloseIcon: true,
        action: onPage && onReopen != null
            ? SnackBarAction(label: l10n.bundlesReopen, onPressed: onReopen)
            : null,
      ),
    );
  }

  /// Puts a Save's draft back in the editor after the Save failed with the
  /// admin on another bundle (#386): the bundle it was for ([bundle], null
  /// for a new draft) with the name and entries the Save sent, and [error],
  /// what the editor would have shown had the admin stayed. Reopen is the
  /// admin's latest ask, so an open still pending can't replace the draft
  /// when it answers (#387).
  void _reopenDraft(
    BundleDetail? bundle,
    String name,
    List<BundleEntry> entries,
    ApiErrorMessage error,
  ) {
    if (!mounted) return;
    setState(() {
      _supersedeOpen();
      _leaveEditor();
      _selected = bundle;
      _isNewDraft = bundle == null;
      _nameController.text = name;
      _entries = entries.map(_EntryRow.fromEntry).toList();
      _error = error;
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
      setState(() => _error = ApiErrorMessage(l10n.bundlesNameRequired));
      return;
    }
    final entries = <BundleEntry>[];
    for (final row in _entries) {
      final value = row.controller.text.trim();
      if (value.isEmpty) {
        setState(
          () => _error = ApiErrorMessage(l10n.bundlesEntryValueRequired),
        );
        return;
      }
      final validation = _validateEntry(l10n, row.kind, row.matchType, value);
      if (validation != null) {
        setState(() => _error = ApiErrorMessage(validation));
        return;
      }
      entries.add(
        BundleEntry(kind: row.kind, value: value, matchType: row.matchType),
      );
    }
    if (entries.isEmpty) {
      setState(() => _error = ApiErrorMessage(l10n.bundlesEntryAtLeastOne));
      return;
    }

    final generation = _editorGeneration;
    final app = _appMessenger();
    // The bundle this Save updates, or null for a new draft. A failure that
    // lands after the admin has moved on names it, and can put the draft
    // back in the editor (#386).
    final bundle = _isNewDraft ? null : _selected;
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final saved = bundle == null
          ? await widget.bundles.create(name, entries)
          : await widget.bundles.update(bundle.id, name, entries);
      // A Save that succeeds after the admin has left the page needs no
      // notice: the catalogue shows its new version when they come back.
      if (!mounted) return;
      // If the admin has moved on, the editor holds another bundle; the
      // catalogue reload still shows the saved one's new version.
      if (_editorStillOn(generation)) {
        setState(() {
          _selected = saved;
          _isNewDraft = false;
          _nameController.text = saved.name;
          _entries = saved.entries.map(_EntryRow.fromEntry).toList();
        });
      }
      await _refreshList();
    } catch (e) {
      // A 409 is the one failure the admin can fix here: another bundle has
      // that name.
      final nameTaken = e is ApiException && e.statusCode == 409;
      final error = nameTaken
          ? ApiErrorMessage(l10n.bundlesNameTaken)
          : describeApiError(
              e,
              generic: l10n.bundlesSaveError,
              notAuthorized: l10n.apiError403Admin,
            );
      if (_onPage && _editorStillOn(generation)) {
        setState(() => _error = error);
      } else {
        // The failure belongs to the bundle or draft the admin has left, not
        // the one in the editor now (#385), or to a page the admin has left
        // (#388), but the admin must still learn the Save didn't go through.
        // On the page, the draft it sent can go back in the editor, so the
        // edit isn't lost (#386).
        _reportLateFailure(
          l10n,
          app,
          l10n.bundlesSaveFailedFor(bundle?.name ?? name),
          e,
          reason: nameTaken ? l10n.bundlesNameTakenBy(name) : null,
          onReopen: () => _reopenDraft(bundle, name, entries, error),
        );
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _archive() async {
    final l10n = AppLocalizations.of(context);
    final selected = _selected;
    if (selected == null) return;
    final generation = _editorGeneration;
    final app = _appMessenger();
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
      // Nothing to say after the admin has left the page (#388).
      if (!mounted) return;
      // Only clear the editor if it still holds the archived bundle (#385).
      if (_editorStillOn(generation)) _clearEditor();
      await _refreshList();
    } catch (e) {
      if (_onPage && _editorStillOn(generation)) {
        setState(
          () => _error = describeApiError(
            e,
            generic: l10n.bundlesArchiveError,
            notAuthorized: l10n.apiError403Admin,
          ),
        );
      } else {
        // The admin has moved on, to another bundle (#385) or another page
        // (#388); say which bundle wasn't archived (#386). There's no edit
        // to put back: its row is still in the list.
        _reportLateFailure(
          l10n,
          app,
          l10n.bundlesArchiveFailedFor(selected.name),
          e,
        );
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _delete() async {
    final l10n = AppLocalizations.of(context);
    final selected = _selected;
    if (selected == null) return;
    final generation = _editorGeneration;
    final app = _appMessenger();
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
      // Nothing to say after the admin has left the page (#388).
      if (!mounted) return;
      // Only clear the editor if it still holds the deleted bundle (#385).
      if (_editorStillOn(generation)) _clearEditor();
      await _refreshList();
    } catch (e) {
      // A 409: a session started with this bundle since the list loaded, and
      // a used bundle can only be archived.
      final used = e is ApiException && e.statusCode == 409;
      if (_onPage && _editorStillOn(generation)) {
        setState(
          () => _error = used
              ? ApiErrorMessage(l10n.bundlesDeleteUsedError)
              : describeApiError(
                  e,
                  generic: l10n.bundlesDeleteError,
                  notAuthorized: l10n.apiError403Admin,
                ),
        );
      } else {
        // The admin has moved on, to another bundle (#385) or another page
        // (#388); say which bundle wasn't deleted (#386). There's no edit to
        // put back: its row is still in the list.
        _reportLateFailure(
          l10n,
          app,
          l10n.bundlesDeleteFailedFor(selected.name),
          e,
          reason: used ? l10n.bundlesUsedArchiveInstead : null,
        );
      }
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
  ///
  /// It runs to the end after the admin has left the page, and the file still
  /// downloads. Its sentence then goes on the app's messenger, noted with the
  /// strings before it waits, as `context` can't be used once the page is
  /// gone (#392).
  Future<void> _exportAll() async {
    final l10n = AppLocalizations.of(context);
    final app = _appMessenger();
    final list = _list;
    if (list == null || list.isEmpty) {
      _snack(app, l10n.bundlesNothingToExport);
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
      _snack(app, l10n.bundlesExported(data.length));
    } catch (e) {
      _snack(
        app,
        describeApiError(
          e,
          generic: l10n.bundlesExportError,
          notAuthorized: l10n.apiError403Admin,
        ).text,
        failed: true,
      );
    } finally {
      if (mounted) setState(() => _porting = false);
    }
  }

  /// Picks a JSON file, validates it, and upserts each bundle by name: an
  /// existing name (archived or not) is updated, a new one is created.
  ///
  /// It runs to the end after the admin has left the page, and its outcome
  /// goes where they are then: the count, or what it could not do
  /// ([_reportImportErrors]). The app's messenger and root navigator, and the
  /// strings, are noted before it waits, on the file and then on the backend,
  /// as `context` can't be used once the page is gone (#392).
  Future<void> _import() async {
    final l10n = AppLocalizations.of(context);
    final app = _appMessenger();
    final root = _rootNavigator();
    final raw = await _fileIo.pickJsonFile();
    if (raw == null) return; // No file chosen.

    final parsed = parseBundlesJson(raw);
    if (!parsed.ok) {
      await _reportImportErrors(
        l10n,
        app,
        root,
        parsed.errors,
        title: l10n.bundlesImportRejected,
      );
      return;
    }

    if (mounted) {
      setState(() {
        _porting = true;
        _error = null;
      });
    }
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
          failures.add(
            describeApiError(
              e,
              generic: l10n.bundlesImportOneError(bundle.name),
              notAuthorized: l10n.apiError403Admin,
            ).text,
          );
        }
      }

      // The catalogue shows what the import did, on a page the admin is still
      // on. One they have left must not be touched, and loads the catalogue
      // again when they come back (#392).
      if (_onPage) await _refreshList();
      if (failures.isEmpty) {
        _snack(
          app,
          l10n.bundlesImported(parsed.bundles.length, created, updated),
        );
      } else {
        await _reportImportErrors(
          l10n,
          app,
          root,
          failures,
          title: l10n.bundlesImportedWithFailures(
            failures.length,
            created,
            updated,
          ),
        );
      }
    } catch (e) {
      _snack(
        app,
        describeApiError(
          e,
          generic: l10n.bundlesImportError,
          notAuthorized: l10n.apiError403Admin,
        ).text,
        failed: true,
      );
    } finally {
      if (mounted) setState(() => _porting = false);
    }
  }

  /// An Import's or Export all's sentence, where the admin is ([_tell]): on
  /// the page, or on the app's messenger, [app], once they have left it
  /// (#392). One that says the action [failed] then stays until they close
  /// it, as it lands while they are busy elsewhere. On the page, each goes
  /// after a moment, as before.
  void _snack(
    ScaffoldMessengerState? app,
    String message, {
    bool failed = false,
  }) {
    _tell(
      app,
      (onPage) => SnackBar(
        content: Text(message),
        persist: failed && !onPage,
        showCloseIcon: failed && !onPage,
      ),
    );
  }

  /// Lists what an import could not do under [title]: each bundle it could
  /// not save (#383), or why the file was rejected. With the admin on the
  /// page, in a dialog, as before.
  ///
  /// Once they have left it, a dialog can't open on the page, so [title]
  /// goes on the app's messenger, [app], and stays until they close it. Its
  /// Details opens the list from the app's root navigator, [root], over
  /// whichever page they are on by then (#392). [errors] and [title] were
  /// worded with [l10n], noted before the import waited, and the dialog
  /// looks up its own strings where it opens, so nothing reads the page's
  /// `context` once the page is gone.
  Future<void> _reportImportErrors(
    AppLocalizations l10n,
    ScaffoldMessengerState? app,
    NavigatorState? root,
    List<String> errors, {
    required String title,
  }) async {
    if (_onPage) {
      await _showImportErrors(context, errors, title: title);
      return;
    }
    _tell(
      app,
      (_) => SnackBar(
        content: Text(title),
        persist: true,
        showCloseIcon: true,
        action: root == null
            ? null
            : SnackBarAction(
                label: l10n.bundlesImportDetails,
                onPressed: () {
                  if (root.mounted) {
                    _showImportErrors(root.context, errors, title: title);
                  }
                },
              ),
      ),
    );
  }

  /// The dialog that lists an import's [errors] under [title], opened from
  /// [context]: the page's, or the app's root navigator's once the admin has
  /// left the page (#392). It reads its strings from its own context.
  static Future<void> _showImportErrors(
    BuildContext context,
    List<String> errors, {
    required String title,
  }) async {
    await showDialog<void>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(title),
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

    return ScaffoldMessenger(
      key: _messenger,
      child: Scaffold(
        backgroundColor: PlinkColors.paper,
        body: Row(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            SizedBox(width: 300, child: _buildList()),
            // A vertical hairline between the panes — the system separates
            // with rules, never shadows.
            const SizedBox(
              width: PlinkBorders.width,
              child: ColoredBox(color: PlinkColors.hairline),
            ),
            Expanded(child: _buildEditor()),
          ],
        ),
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
        Expanded(child: _buildCatalogue(list)),
      ],
    );
  }

  Widget _buildCatalogue(List<BundleSummary>? list) {
    final loadError = _loadError;
    if (list == null) {
      // No catalogue yet: the load failed, so say so, or it is still running.
      // Only a list that actually loaded can be "No bundles." (#384).
      return Center(
        child: loadError == null
            ? const CircularProgressIndicator()
            : Padding(
                padding: const EdgeInsets.all(PlinkSpacing.s4),
                child: _LoadFailure(
                  message: loadError,
                  retryKey: const Key('bundles-load-retry-button'),
                  onRetry: _retryLoad,
                ),
              ),
      );
    }
    final rows = list.isEmpty
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
          );
    if (loadError == null) return rows;
    // A reload failed with a catalogue already on screen (the archived toggle,
    // or the reload after a save): keep the rows, and say above them that the
    // reload failed.
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Padding(
          padding: const EdgeInsets.all(PlinkSpacing.s4),
          child: _LoadFailure(
            message: loadError,
            retryKey: const Key('bundles-load-retry-button'),
            onRetry: _retryLoad,
          ),
        ),
        const _Hairline(),
        Expanded(child: rows),
      ],
    );
  }

  Widget _buildEditor() {
    final l10n = AppLocalizations.of(context);
    if (_selected == null && !_isNewDraft) {
      final openError = _openError;
      final openFailed = _openFailed;
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(PlinkSpacing.s6),
          child: _opening
              ? const CircularProgressIndicator()
              // A bundle that failed to open says so here, where its editor
              // would be, not as the select-a-bundle placeholder (#384).
              : openError != null
              ? _LoadFailure(
                  message: openError,
                  retryKey: const Key('bundles-open-retry-button'),
                  onRetry: openFailed == null
                      ? null
                      : () => _openBundle(openFailed),
                )
              : Text(
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
                ApiErrorText(_error!),
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

/// A failed load, the way Home, Classes and History show one (#278, #384):
/// the human sentence from [describeApiError], centred, with a calm ink Retry.
/// A 403 is the no-admin-access notice, which a retry can't clear, so it gets
/// no Retry.
class _LoadFailure extends StatelessWidget {
  const _LoadFailure({
    required this.message,
    required this.retryKey,
    required this.onRetry,
  });

  final ApiErrorMessage message;
  final Key retryKey;
  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) {
    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        ApiErrorText(message, textAlign: TextAlign.center),
        if (!message.isAuthorization && onRetry != null) ...[
          const SizedBox(height: PlinkSpacing.s3),
          // Calm ink: retrying a fetch is never the magenta spark.
          OutlinedButton(
            key: retryKey,
            onPressed: onRetry,
            child: Text(AppLocalizations.of(context).actionRetry),
          ),
        ],
      ],
    );
  }
}

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
