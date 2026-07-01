import 'package:flutter/material.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../api/admins_api.dart';
import '../api/sessions_api.dart' show ApiException;
import '../l10n/app_localizations.dart';

/// Admin-only "Manage admins" sub-tab (#300), in the paper treatment (AD5).
///
/// One readable column: an add control that searches signed-in users and
/// promotes the chosen one, above the list of current admins — each a quiet
/// instrument row with a calm-ink Remove action. The route already gates the
/// area on `isAdmin`; this page assumes an admin caller and surfaces the
/// server's guards (e.g. the refusal to remove the last admin) inline.
class ManageAdminsPage extends StatefulWidget {
  const ManageAdminsPage({super.key, required this.admins});

  final AdminsApi admins;

  @override
  State<ManageAdminsPage> createState() => _ManageAdminsPageState();
}

class _ManageAdminsPageState extends State<ManageAdminsPage> {
  bool _loading = false;
  String? _error;
  List<AdminUser>? _admins;

  final TextEditingController _searchController = TextEditingController();
  List<AdminUser> _candidates = const [];
  bool _searching = false;
  // Monotonic token so a slow earlier search can't clobber a newer result.
  int _searchSeq = 0;

  // Ids with an in-flight promote/demote, so their row can show progress and
  // stay un-tappable without freezing the rest of the page.
  final Set<String> _busy = <String>{};

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (_admins == null && !_loading && _error == null) {
      _loadAdmins();
    }
  }

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  Future<void> _loadAdmins() async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final list = await widget.admins.listAdmins();
      if (!mounted) return;
      setState(() => _admins = list);
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = l10n.adminsLoadError('$e'));
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _onSearchChanged(String raw) async {
    final l10n = AppLocalizations.of(context);
    final query = raw.trim();
    final seq = ++_searchSeq;
    if (query.isEmpty) {
      setState(() {
        _candidates = const [];
        _searching = false;
      });
      return;
    }
    setState(() => _searching = true);
    try {
      final results = await widget.admins.searchCandidates(query);
      if (!mounted || seq != _searchSeq) return;
      setState(() {
        _candidates = results;
        _searching = false;
      });
    } catch (e) {
      if (!mounted || seq != _searchSeq) return;
      setState(() {
        _error = l10n.adminsSearchError('$e');
        _searching = false;
      });
    }
  }

  Future<void> _promote(AdminUser candidate) async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _busy.add(candidate.id);
      _error = null;
    });
    try {
      await widget.admins.promote(candidate.id);
      if (!mounted) return;
      // The promoted user is now an admin, so drop the search and refresh the
      // list — leaving them in the candidate results would be misleading.
      _searchController.clear();
      setState(() {
        _candidates = const [];
        _searchSeq++;
      });
      await _loadAdmins();
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _error = l10n.adminsPromoteError(candidate.displayName, '$e'),
      );
    } finally {
      if (mounted) setState(() => _busy.remove(candidate.id));
    }
  }

  Future<void> _remove(AdminUser admin) async {
    final l10n = AppLocalizations.of(context);
    final confirm = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(l10n.adminsRemoveTitle),
        content: Text(l10n.adminsRemoveBody(admin.displayName)),
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
            child: Text(l10n.actionRemove),
          ),
        ],
      ),
    );
    if (confirm != true) return;

    setState(() {
      _busy.add(admin.id);
      _error = null;
    });
    try {
      await widget.admins.demote(admin.id);
      if (!mounted) return;
      await _loadAdmins();
    } catch (e) {
      if (!mounted) return;
      // The last-admin guard (409) is the expected, explainable failure here —
      // give it a human message rather than echoing the raw exception.
      final message = e is ApiException && e.statusCode == 409
          ? l10n.adminsLastAdminError
          : l10n.adminsRemoveError(admin.displayName, '$e');
      setState(() => _error = message);
    } finally {
      if (mounted) setState(() => _busy.remove(admin.id));
    }
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
                _buildAddControl(),
                const SizedBox(height: PlinkSpacing.s7),
                if (_error != null) ...[
                  Text(
                    _error!,
                    key: const Key('manage-admins-error'),
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: Theme.of(context).colorScheme.error,
                    ),
                  ),
                  const SizedBox(height: PlinkSpacing.s4),
                ],
                Text(
                  AppLocalizations.of(context).adminsCurrentAdmins,
                  style: _monoLabel(PlinkColors.ink60),
                ),
                const SizedBox(height: PlinkSpacing.s3),
                _buildAdminList(),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildAddControl() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(
          AppLocalizations.of(context).adminsAddAdmin,
          style: _monoLabel(PlinkColors.ink60),
        ),
        const SizedBox(height: PlinkSpacing.s2),
        Text(
          AppLocalizations.of(context).adminsAddHint,
          style: Theme.of(
            context,
          ).textTheme.bodySmall?.copyWith(color: PlinkColors.ink60),
        ),
        const SizedBox(height: PlinkSpacing.s3),
        TextField(
          key: const Key('manage-admins-search'),
          controller: _searchController,
          decoration: InputDecoration(
            hintText: AppLocalizations.of(context).adminsSearchByName,
            isDense: true,
            prefixIcon: const Icon(Icons.search, size: 18),
          ),
          onChanged: _onSearchChanged,
        ),
        const SizedBox(height: PlinkSpacing.s3),
        _buildCandidates(),
      ],
    );
  }

  Widget _buildCandidates() {
    if (_searchController.text.trim().isEmpty) {
      return const SizedBox.shrink();
    }
    if (_searching) {
      return Padding(
        padding: const EdgeInsets.symmetric(vertical: PlinkSpacing.s3),
        child: Text(
          AppLocalizations.of(context).adminsSearching,
          style: _monoLabel(PlinkColors.muted),
        ),
      );
    }
    if (_candidates.isEmpty) {
      return Padding(
        padding: const EdgeInsets.symmetric(vertical: PlinkSpacing.s3),
        child: Text(
          AppLocalizations.of(context).adminsNoCandidates,
          key: const Key('manage-admins-no-candidates'),
          style: _monoLabel(PlinkColors.muted),
        ),
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
          for (var i = 0; i < _candidates.length; i++) ...[
            if (i > 0) const _Hairline(),
            _CandidateRow(
              candidate: _candidates[i],
              busy: _busy.contains(_candidates[i].id),
              onAdd: () => _promote(_candidates[i]),
            ),
          ],
        ],
      ),
    );
  }

  Widget _buildAdminList() {
    final admins = _admins;
    if (_loading && admins == null) {
      return const Padding(
        padding: EdgeInsets.symmetric(vertical: PlinkSpacing.s6),
        child: Center(child: CircularProgressIndicator()),
      );
    }
    if (admins == null || admins.isEmpty) {
      return Text(
        AppLocalizations.of(context).adminsNoAdmins,
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
          for (var i = 0; i < admins.length; i++) ...[
            if (i > 0) const _Hairline(),
            _AdminRow(
              admin: admins[i],
              busy: _busy.contains(admins[i].id),
              onRemove: () => _remove(admins[i]),
            ),
          ],
        ],
      ),
    );
  }
}

/// One search result — name over a muted Entra-oid spec, with a calm-ink Add.
class _CandidateRow extends StatelessWidget {
  const _CandidateRow({
    required this.candidate,
    required this.busy,
    required this.onAdd,
  });

  final AdminUser candidate;
  final bool busy;
  final VoidCallback onAdd;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        PlinkSpacing.s4,
        PlinkSpacing.s3,
        PlinkSpacing.s3,
        PlinkSpacing.s3,
      ),
      child: Row(
        children: [
          Expanded(child: _identity(context, candidate)),
          OutlinedButton.icon(
            key: Key('admin-add-${candidate.id}'),
            icon: const Icon(Icons.add, size: 18),
            label: Text(AppLocalizations.of(context).actionAdd),
            onPressed: busy ? null : onAdd,
          ),
        ],
      ),
    );
  }
}

/// One current-admin row — name over a muted Entra-oid spec, with a calm-ink
/// Remove. Removing admin rights is a quiet maintenance action, not a
/// destructive commit, so it stays ink rather than wearing the spark.
class _AdminRow extends StatelessWidget {
  const _AdminRow({
    required this.admin,
    required this.busy,
    required this.onRemove,
  });

  final AdminUser admin;
  final bool busy;
  final VoidCallback onRemove;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        PlinkSpacing.s4,
        PlinkSpacing.s3,
        PlinkSpacing.s3,
        PlinkSpacing.s3,
      ),
      child: Row(
        children: [
          Expanded(child: _identity(context, admin)),
          OutlinedButton.icon(
            key: Key('admin-remove-${admin.id}'),
            icon: busy
                ? const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.person_remove_outlined, size: 18),
            label: Text(AppLocalizations.of(context).actionRemove),
            onPressed: busy ? null : onRemove,
          ),
        ],
      ),
    );
  }
}

/// Shared identity block: the display name, with the Entra object id as a muted
/// mono spec beneath so two people sharing a name can still be told apart.
Widget _identity(BuildContext context, AdminUser user) => Column(
  crossAxisAlignment: CrossAxisAlignment.start,
  children: [
    Text(
      user.displayName,
      maxLines: 1,
      overflow: TextOverflow.ellipsis,
      style: Theme.of(
        context,
      ).textTheme.bodyLarge?.copyWith(color: PlinkColors.ink),
    ),
    Text(
      user.entraOid,
      maxLines: 1,
      overflow: TextOverflow.ellipsis,
      style: _monoSpec(PlinkColors.muted, PlinkType.textSm),
    ),
  ],
);

/// A full-width 1px instrument rule — the system separates with hairlines,
/// never shadows. Mirrors the bundles editor.
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

/// A space-mono label style for the quiet section headers and microcopy.
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

/// Tabular-figure mono for values that read as a spec (the Entra object id).
TextStyle _monoSpec(Color color, double size) => TextStyle(
  fontFamily: PlinkType.monoFamily,
  package: PlinkType.fontPackage,
  fontFamilyFallback: PlinkType.monoFallback,
  fontSize: size,
  color: color,
  height: 1.4,
  fontFeatures: const <FontFeature>[FontFeature.tabularFigures()],
);
