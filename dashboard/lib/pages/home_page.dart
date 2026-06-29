import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../api/auth_token_store.dart';
import '../api/sessions_api.dart';
import '../widgets/api_error_text.dart';

/// The dashboard home page (AD3, #168) — paper treatment + brand voice.
///
/// Home is the teacher's instrument panel: it sits inside the app shell (which
/// already carries the identity rule, app-bar, and the "01 · HOME" eyebrow), so
/// this page is just the flush-left editorial column beneath that eyebrow. Any
/// still-running sessions read first as quiet hairline instrument cards (#126),
/// then the start-session composer — one oversized Fraunces line, the class
/// picker, and the single magenta spark on the page: the Start button.
class HomePage extends StatefulWidget {
  const HomePage({super.key, required this.tokens, required this.sessions});

  final AuthTokenStore tokens;
  final SessionsApi sessions;

  @override
  State<HomePage> createState() => _HomePageState();
}

class _HomePageState extends State<HomePage> {
  List<ClassSummary>? _classes;
  ClassSummary? _selected;
  bool _busy = false;
  ApiErrorMessage? _error;
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
      // /me upserts based on the Entra oid + role claim, idempotently. The
      // admin role now drives the shared nav (the shell), not this page.
      await widget.sessions.me();
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
      });
      await _loadActiveSessions();
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _error = describeApiError(
          e,
          generic: 'Could not load start-session data. Please try again.',
        ),
      );
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// Pulls the teacher's still-running sessions so the resume cards can offer
  /// a way back to a session that lost its URL (#126). Non-fatal: a failure
  /// here must not block starting a new session, so it never sets [_error].
  Future<void> _loadActiveSessions() async {
    try {
      final active = await widget.sessions.activeSessions();
      if (!mounted) return;
      setState(() => _activeSessions = active);
    } catch (_) {
      // The cards just won't render; the start-session form below still works.
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
      setState(
        () => _error = describeApiError(
          e,
          generic: 'Failed to start session. Please try again.',
        ),
      );
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// Ends a running session straight from the home screen so the teacher
  /// doesn't have to resume into it just to stop it (#126). On success the card
  /// drops out; the running-now panel hides itself once none remain.
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
      setState(
        () => _error = describeApiError(
          e,
          generic: 'Failed to end session. Please try again.',
        ),
      );
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

  @override
  Widget build(BuildContext context) {
    final TextTheme text = Theme.of(context).textTheme;
    final String? department = widget.tokens.account?.department;
    final List<ClassSummary>? classes = _classes;

    return Scaffold(
      backgroundColor: PlinkColors.paper,
      body: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(
          _gutter,
          PlinkSpacing.s2,
          _gutter,
          PlinkSpacing.s8,
        ),
        child: Align(
          alignment: Alignment.centerLeft,
          child: ConstrainedBox(
            // The editorial column: wide enough for the Fraunces line to
            // breathe, narrow enough that the form stays a readable measure.
            constraints: const BoxConstraints(maxWidth: 640),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                // Still-running sessions read first — quiet hairline instrument
                // cards a teacher can resume into or end in place (#126).
                if (_activeSessions.isNotEmpty) ...<Widget>[
                  Eyebrow(
                    _activeSessions.length == 1
                        ? 'Still running'
                        : 'Still running — ${_activeSessions.length} sessions',
                  ),
                  const SizedBox(height: PlinkSpacing.s4),
                  for (final ActiveSession s in _activeSessions) ...<Widget>[
                    _ActiveSessionCard(
                      className: _classNameFor(s.classId),
                      startedAt: s.startedAt,
                      ending: _endingSessions.contains(s.id),
                      onResume: () => context.go('/session/${s.id}'),
                      onEnd: () => _endActiveSession(s.id),
                    ),
                    const SizedBox(height: PlinkSpacing.s3),
                  ],
                  const SizedBox(height: PlinkSpacing.s7),
                ],

                // The start-session composer — the hero of the page.
                Text(
                  'Start a focus session.',
                  key: const Key('home-headline'),
                  style: text.displaySmall,
                ),
                const SizedBox(height: PlinkSpacing.s4),
                Text(
                  'Pick a class and start. Students join with the code shown '
                  'on the session screen.',
                  style: text.bodyLarge?.copyWith(color: PlinkColors.ink60),
                ),
                const SizedBox(height: PlinkSpacing.s6),

                if (classes == null && _busy)
                  const Padding(
                    padding: EdgeInsets.symmetric(vertical: PlinkSpacing.s4),
                    child: SizedBox(
                      width: 22,
                      height: 22,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    ),
                  )
                // The genuine "no classes yet" empty state — only when the load
                // actually succeeded. A failed load (e.g. a 403) is carried by
                // the error notice below, not dressed up as an empty roster.
                else if ((classes == null || classes.isEmpty) && _error == null)
                  Text(
                    'No classes assigned to you yet.',
                    style: text.bodyLarge?.copyWith(color: PlinkColors.ink60),
                  )
                else if (classes != null && classes.isNotEmpty) ...<Widget>[
                  if (department != null && department.isNotEmpty) ...<Widget>[
                    Text(
                      'YOUR DEPARTMENT · ${department.toUpperCase()}',
                      style: text.labelSmall?.copyWith(
                        color: PlinkColors.muted,
                      ),
                    ),
                    const SizedBox(height: PlinkSpacing.s3),
                  ],
                  ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 420),
                    child: DropdownButtonFormField<ClassSummary>(
                      key: const Key('class-picker'),
                      initialValue: _selected,
                      decoration: const InputDecoration(labelText: 'Class'),
                      items: <DropdownMenuItem<ClassSummary>>[
                        for (final ClassSummary c in classes)
                          DropdownMenuItem<ClassSummary>(
                            value: c,
                            child: Text('${c.name} (${c.schoolYear})'),
                          ),
                      ],
                      onChanged: _busy
                          ? null
                          : (ClassSummary? value) =>
                                setState(() => _selected = value),
                    ),
                  ),
                  const SizedBox(height: PlinkSpacing.s5),
                  // The single primary (magenta) action — the one spark on the
                  // page. The DS theme paints ElevatedButton in the spark.
                  ElevatedButton(
                    key: const Key('start-session'),
                    onPressed: _busy || _selected == null
                        ? null
                        : _startSession,
                    child: _busy
                        ? const SizedBox(
                            width: 16,
                            height: 16,
                            child: CircularProgressIndicator(
                              strokeWidth: 2,
                              color: PlinkColors.onInk,
                            ),
                          )
                        : Text(
                            _selected == null
                                ? 'Select a class'
                                : 'Start session for ${_selected!.name}',
                          ),
                  ),
                ],

                if (_error != null) ...<Widget>[
                  const SizedBox(height: PlinkSpacing.s4),
                  ApiErrorText(_error!, key: const Key('home-error')),
                ],
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// Horizontal page gutter — keeps the home column on the same flush-left margin
/// as the shell's eyebrow and app-bar (the editorial column).
const double _gutter = PlinkSpacing.s6; // 32

/// A still-running session, drawn as a quiet hairline instrument card (#168):
/// a LIVE spark badge + the started time as a mono spec, the class name, and
/// the two ink (never magenta) actions — Resume back into it, or End it in
/// place (#126). The magenta spark stays reserved for the Start button.
class _ActiveSessionCard extends StatelessWidget {
  const _ActiveSessionCard({
    required this.className,
    required this.startedAt,
    required this.ending,
    required this.onResume,
    required this.onEnd,
  });

  final String className;
  final DateTime startedAt;
  final bool ending;
  final VoidCallback onResume;
  final VoidCallback onEnd;

  static String _startedLabel(DateTime startedAt) {
    final DateTime local = startedAt.toLocal();
    final String h = local.hour.toString().padLeft(2, '0');
    final String mi = local.minute.toString().padLeft(2, '0');
    return 'STARTED $h:$mi';
  }

  @override
  Widget build(BuildContext context) {
    final TextTheme text = Theme.of(context).textTheme;

    return Container(
      width: double.infinity,
      decoration: BoxDecoration(
        border: Border.all(
          color: PlinkColors.hairline,
          width: PlinkBorders.width,
        ),
        borderRadius: BorderRadius.circular(PlinkRadius.base),
      ),
      padding: const EdgeInsets.all(PlinkSpacing.s4),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Row(
            children: <Widget>[
              const PlinkBadge('Live', variant: BadgeVariant.spark, dot: true),
              const SizedBox(width: PlinkSpacing.s3),
              Text(
                _startedLabel(startedAt),
                style: text.labelSmall?.copyWith(color: PlinkColors.muted),
              ),
            ],
          ),
          const SizedBox(height: PlinkSpacing.s3),
          Text(
            className,
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
            style: text.titleMedium,
          ),
          const SizedBox(height: PlinkSpacing.s4),
          // Wrap so the two actions reflow onto a second line on a narrow
          // window instead of overflowing the card (a11y: reflow, never clip).
          Wrap(
            spacing: PlinkSpacing.s3,
            runSpacing: PlinkSpacing.s2,
            children: <Widget>[
              OutlinedButton(
                onPressed: ending ? null : onResume,
                child: const Text('Resume'),
              ),
              if (ending)
                const Padding(
                  padding: EdgeInsets.symmetric(
                    horizontal: PlinkSpacing.s4,
                    vertical: PlinkSpacing.s3,
                  ),
                  child: SizedBox(
                    width: 18,
                    height: 18,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  ),
                )
              else
                OutlinedButton(onPressed: onEnd, child: const Text('End')),
            ],
          ),
        ],
      ),
    );
  }
}
