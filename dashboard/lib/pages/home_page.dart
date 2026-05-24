import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../api/auth_token_store.dart';
import '../api/bundles_api.dart';
import '../api/sessions_api.dart';
import '../auth/msal_auth_service.dart';
import '../storage/bundle_prefs.dart';

class HomePage extends StatefulWidget {
  const HomePage({
    super.key,
    required this.tokens,
    required this.auth,
    required this.sessions,
    required this.bundles,
  });

  final AuthTokenStore tokens;
  final MsalAuthService auth;
  final SessionsApi sessions;
  final BundlesApi bundles;

  @override
  State<HomePage> createState() => _HomePageState();
}

class _HomePageState extends State<HomePage> {
  final BundlePrefs _prefs = BundlePrefs();
  List<ClassSummary>? _classes;
  ClassSummary? _selected;
  List<BundleSummary>? _bundles;
  final Set<String> _selectedBundleIds = <String>{};
  bool _busy = false;
  String? _error;
  bool _isAdmin = false;

  @override
  void initState() {
    super.initState();
    _loadInitialData();
  }

  Future<void> _loadInitialData() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      // Provision the user in the backend before any role-gated call —
      // /me upserts based on the Entra oid + role claim, idempotently.
      final me = await widget.sessions.me();
      final classesFuture = widget.sessions.classes();
      final bundlesFuture = widget.bundles.list();
      final classes = await classesFuture;
      final bundles = await bundlesFuture;
      final department = widget.tokens.account?.department;
      ClassSummary? preferred;
      if (department != null && department.isNotEmpty) {
        for (final c in classes) {
          if (c.name == department) {
            preferred = c;
            break;
          }
        }
      }
      preferred ??= classes.isNotEmpty ? classes.first : null;

      final accountKey = widget.tokens.account?.homeAccountId;
      final remembered = accountKey == null
          ? null
          : _prefs.readSelection(accountKey);
      final availableIds = bundles.map((b) => b.id).toSet();
      final restored = (remembered ?? const <String>[])
          .where(availableIds.contains)
          .toSet();

      if (!mounted) return;
      setState(() {
        _classes = classes;
        _selected = preferred;
        _bundles = bundles;
        _isAdmin = me.isAdmin;
        _selectedBundleIds
          ..clear()
          ..addAll(restored);
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Could not load start-session data: $e');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _startSession() async {
    final klass = _selected;
    if (klass == null) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final bundleIds = _selectedBundleIds.toList(growable: false);
      final accountKey = widget.tokens.account?.homeAccountId;
      if (accountKey != null) {
        _prefs.writeSelection(accountKey, bundleIds);
      }
      final session = await widget.sessions.startSession(
        klass.id,
        bundleIds: bundleIds,
      );
      if (!mounted) return;
      context.go('/session/${session.id}');
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Failed to start session: $e');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _signOut() async {
    try {
      await widget.auth.signOut();
    } finally {
      widget.tokens.clear();
      if (mounted) context.go('/login');
    }
  }

  @override
  Widget build(BuildContext context) {
    final account = widget.tokens.account;
    final classes = _classes;
    final bundles = _bundles;
    return Scaffold(
      appBar: AppBar(
        title: const Text('Anchor'),
        actions: [
          TextButton.icon(
            icon: const Icon(Icons.group),
            label: const Text('Manage classes'),
            onPressed: () => context.go('/classes'),
          ),
          if (_isAdmin)
            TextButton.icon(
              icon: const Icon(Icons.collections_bookmark),
              label: const Text('Bundles'),
              onPressed: () => context.go('/bundles'),
            ),
          if (account != null)
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12),
              child: Center(child: Text(account.displayName)),
            ),
          IconButton(
            tooltip: 'Sign out',
            icon: const Icon(Icons.logout),
            onPressed: _signOut,
          ),
        ],
      ),
      body: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 480),
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                if (account?.department != null) ...[
                  Text(
                    'Your department: ${account!.department}',
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 16),
                ],
                if (classes == null && _busy)
                  const Center(child: CircularProgressIndicator())
                else if (classes == null || classes.isEmpty)
                  const Text(
                    'No classes assigned to you yet.',
                    textAlign: TextAlign.center,
                  )
                else ...[
                  DropdownButtonFormField<ClassSummary>(
                    initialValue: _selected,
                    decoration: const InputDecoration(labelText: 'Class'),
                    items: [
                      for (final c in classes)
                        DropdownMenuItem(
                          value: c,
                          child: Text('${c.name} (${c.schoolYear})'),
                        ),
                    ],
                    onChanged: _busy
                        ? null
                        : (value) => setState(() => _selected = value),
                  ),
                  const SizedBox(height: 16),
                  _BundlePicker(
                    bundles: bundles,
                    selectedIds: _selectedBundleIds,
                    enabled: !_busy,
                    onToggle: (id, selected) {
                      setState(() {
                        if (selected) {
                          _selectedBundleIds.add(id);
                        } else {
                          _selectedBundleIds.remove(id);
                        }
                      });
                    },
                  ),
                  const SizedBox(height: 24),
                  FilledButton.icon(
                    onPressed: _busy || _selected == null ? null : _startSession,
                    icon: _busy
                        ? const SizedBox(
                            width: 16,
                            height: 16,
                            child: CircularProgressIndicator(strokeWidth: 2),
                          )
                        : const Icon(Icons.play_arrow),
                    label: Text(
                      _selected == null
                          ? 'Select a class'
                          : 'Start session for class ${_selected!.name}',
                    ),
                  ),
                ],
                if (_error != null) ...[
                  const SizedBox(height: 16),
                  Text(
                    _error!,
                    style: TextStyle(color: Theme.of(context).colorScheme.error),
                    textAlign: TextAlign.center,
                  ),
                ],
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _BundlePicker extends StatelessWidget {
  const _BundlePicker({
    required this.bundles,
    required this.selectedIds,
    required this.enabled,
    required this.onToggle,
  });

  final List<BundleSummary>? bundles;
  final Set<String> selectedIds;
  final bool enabled;
  final void Function(String id, bool selected) onToggle;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final list = bundles;
    if (list == null) {
      return const SizedBox.shrink();
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text('Allowed bundles', style: theme.textTheme.labelLarge),
        const SizedBox(height: 8),
        if (list.isEmpty)
          Text(
            'No bundles available yet.',
            style: theme.textTheme.bodySmall,
          )
        else
          Wrap(
            spacing: 8,
            runSpacing: 4,
            children: [
              for (final b in list)
                FilterChip(
                  label: Text(b.name),
                  selected: selectedIds.contains(b.id),
                  onSelected: enabled
                      ? (selected) => onToggle(b.id, selected)
                      : null,
                ),
            ],
          ),
      ],
    );
  }
}
