import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../api/auth_token_store.dart';
import '../api/sessions_api.dart';
import '../auth/msal_auth_service.dart';

class HomePage extends StatefulWidget {
  const HomePage({
    super.key,
    required this.tokens,
    required this.auth,
    required this.sessions,
  });

  final AuthTokenStore tokens;
  final MsalAuthService auth;
  final SessionsApi sessions;

  @override
  State<HomePage> createState() => _HomePageState();
}

class _HomePageState extends State<HomePage> {
  List<ClassSummary>? _classes;
  ClassSummary? _selected;
  bool _busy = false;
  String? _error;
  bool _isAdmin = false;
  List<ActiveSession> _activeSessions = const [];
  final Set<String> _endingSessions = {};

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
      final classes = await widget.sessions.classes();
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

      if (!mounted) return;
      setState(() {
        _classes = classes;
        _selected = preferred;
        _isAdmin = me.isAdmin;
      });
      await _loadActiveSessions();
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Could not load start-session data: $e');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// Pulls the teacher's still-running sessions so the resume banner can offer
  /// a way back to a session that lost its URL (#126). Non-fatal: a failure
  /// here must not block starting a new session, so it never sets [_error].
  Future<void> _loadActiveSessions() async {
    try {
      final active = await widget.sessions.activeSessions();
      if (!mounted) return;
      setState(() => _activeSessions = active);
    } catch (_) {
      // Banner just won't render; the start-session form below still works.
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
      // Sessions now start with no bundles — baseline-only enforcement. The
      // teacher adds bundles from the live session view (#93).
      final session = await widget.sessions.startSession(klass.id);
      if (!mounted) return;
      context.go('/session/${session.id}');
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Failed to start session: $e');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// Ends a running session straight from the home screen so the teacher
  /// doesn't have to resume into it just to stop it (#126). On success the row
  /// drops out of the banner; the banner hides itself once none remain.
  Future<void> _endActiveSession(String sessionId) async {
    setState(() {
      _endingSessions.add(sessionId);
      _error = null;
    });
    try {
      await widget.sessions.endSession(sessionId);
      if (!mounted) return;
      setState(() {
        _activeSessions = _activeSessions
            .where((s) => s.id != sessionId)
            .toList(growable: false);
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Failed to end session: $e');
    } finally {
      if (mounted) setState(() => _endingSessions.remove(sessionId));
    }
  }

  String _classNameFor(String classId) {
    for (final c in _classes ?? const <ClassSummary>[]) {
      if (c.id == classId) return c.name;
    }
    // Teacher may no longer be listed on the class yet the session runs on —
    // still worth surfacing so it can be reached and ended.
    return 'Active session';
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
    return Scaffold(
      appBar: AppBar(
        title: const Text('Anchor'),
        actions: [
          TextButton.icon(
            icon: const Icon(Icons.group),
            label: const Text('Manage classes'),
            onPressed: () => context.go('/classes'),
          ),
          TextButton.icon(
            icon: const Icon(Icons.history),
            label: const Text('Past sessions'),
            onPressed: () => context.go('/history'),
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
                if (_activeSessions.isNotEmpty) ...[
                  _ActiveSessionsBanner(
                    sessions: _activeSessions,
                    classNameFor: _classNameFor,
                    ending: _endingSessions,
                    onResume: (id) => context.go('/session/$id'),
                    onEnd: _endActiveSession,
                  ),
                  const SizedBox(height: 24),
                ],
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

/// Surfaces the teacher's still-running session(s) with a "Resume" button so a
/// session orphaned by browser Back / refresh / relaunch is reachable and
/// endable again (#126).
class _ActiveSessionsBanner extends StatelessWidget {
  const _ActiveSessionsBanner({
    required this.sessions,
    required this.classNameFor,
    required this.ending,
    required this.onResume,
    required this.onEnd,
  });

  final List<ActiveSession> sessions;
  final String Function(String classId) classNameFor;
  final Set<String> ending;
  final void Function(String sessionId) onResume;
  final void Function(String sessionId) onEnd;

  static String _startedLabel(DateTime startedAt) {
    final local = startedAt.toLocal();
    final h = local.hour.toString().padLeft(2, '0');
    final mi = local.minute.toString().padLeft(2, '0');
    return 'Started $h:$mi';
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Container(
      width: double.infinity,
      decoration: BoxDecoration(
        color: theme.colorScheme.primaryContainer,
        borderRadius: BorderRadius.circular(12),
      ),
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(
                Icons.play_circle_outline,
                size: 18,
                color: theme.colorScheme.onPrimaryContainer,
              ),
              const SizedBox(width: 8),
              Text(
                sessions.length == 1 ? 'Active session' : 'Active sessions',
                style: theme.textTheme.titleSmall?.copyWith(
                  color: theme.colorScheme.onPrimaryContainer,
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          for (final s in sessions)
            Padding(
              padding: const EdgeInsets.only(top: 4),
              child: Row(
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          classNameFor(s.classId),
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: theme.textTheme.bodyLarge?.copyWith(
                            color: theme.colorScheme.onPrimaryContainer,
                            fontWeight: FontWeight.w500,
                          ),
                        ),
                        Text(
                          _startedLabel(s.startedAt),
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: theme.colorScheme.onPrimaryContainer,
                          ),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(width: 12),
                  // Stop a session without resuming into it first (#126).
                  if (ending.contains(s.id))
                    const Padding(
                      padding: EdgeInsets.symmetric(horizontal: 12),
                      child: SizedBox(
                        width: 18,
                        height: 18,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      ),
                    )
                  else ...[
                    OutlinedButton.icon(
                      onPressed: () => onEnd(s.id),
                      icon: const Icon(Icons.stop),
                      label: const Text('End'),
                    ),
                    const SizedBox(width: 8),
                  ],
                  FilledButton.tonalIcon(
                    onPressed: ending.contains(s.id) ? null : () => onResume(s.id),
                    icon: const Icon(Icons.login),
                    label: const Text('Resume'),
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }
}
