import 'dart:async';

import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../api/auth_token_store.dart';
import '../api/sessions_api.dart';
import '../l10n/app_localizations.dart';
import '../widgets/api_error_text.dart';

/// The dashboard home page (AD3, #168) — paper treatment + brand voice.
///
/// Home is the teacher's instrument panel: it sits inside the app shell (which
/// already carries the identity rule, app-bar, and the "01 · HOME" eyebrow), so
/// this page is just the flush-left editorial column beneath that eyebrow. Any
/// still-running sessions read first as quiet hairline instrument cards (#126),
/// then the start-session composer — one oversized Fraunces line, the class
/// picker, when the session ends (#447), and the single magenta spark on the
/// page: the Start button.
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

  // When the session ends (#447): the period ends the selected class's school
  // offers right now (null while they load), the teacher's choice, and the
  // time typed for a specific end. [_endOptionsLoad] counts the loads, so a
  // slow answer for a class the teacher has since switched away from is
  // dropped.
  SessionEndOptions? _endOptions;
  int _endOptionsLoad = 0;
  _EndChoice _endChoice = _EndChoice.specificTime;
  final TextEditingController _endTime = TextEditingController();

  bool _didLoad = false;

  @override
  void dispose() {
    _endTime.dispose();
    super.dispose();
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    // The initial load reads AppLocalizations (an inherited widget), so it must
    // run here rather than in initState, where inherited-widget lookups aren't
    // yet valid. Guard so it fires only once.
    if (!_didLoad) {
      _didLoad = true;
      _loadInitialData();
    }
  }

  Future<void> _loadInitialData() async {
    final l10n = AppLocalizations.of(context);
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
      await Future.wait([_loadActiveSessions(), _loadEndOptions(preferred)]);
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _error = describeApiError(
          e,
          generic: l10n.homeLoadError,
          notAuthorized: l10n.apiError403,
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

  /// Loads when a session of [klass] can end if it starts now (#447): the end
  /// of this and the next lesson period at its school. The end of this period
  /// is the default; without one, the teacher picks a time, which starts out
  /// [_defaultSessionLength] away. Non-fatal: if the options don't load, the
  /// teacher still picks a time.
  Future<void> _loadEndOptions(ClassSummary? klass) async {
    if (klass == null) return;
    final load = ++_endOptionsLoad;
    setState(() => _endOptions = null);
    SessionEndOptions options;
    try {
      options = await widget.sessions.endOptions(klass.id);
    } catch (_) {
      options = SessionEndOptions();
    }
    if (!mounted || load != _endOptionsLoad) return;
    setState(() {
      _endOptions = options;
      _endChoice = options.thisPeriodEndsAt != null
          ? _EndChoice.thisPeriod
          : _EndChoice.specificTime;
      _endTime.text = _clockTime(_defaultEndTime(options, DateTime.now()));
    });
  }

  /// When the session ends if the teacher picks the time in [_endTime],
  /// starting at [now]: today, or tomorrow once today's has passed. Null when
  /// the field doesn't hold a time.
  DateTime? _pickedEnd(DateTime now) {
    final match = RegExp(
      r'^(\d{1,2}):(\d{2})$',
    ).firstMatch(_endTime.text.trim());
    if (match == null) return null;
    final hour = int.parse(match.group(1)!);
    final minute = int.parse(match.group(2)!);
    if (hour > 23 || minute > 59) return null;
    final today = DateTime(now.year, now.month, now.day, hour, minute);
    return today.isAfter(now)
        ? today
        : DateTime(now.year, now.month, now.day + 1, hour, minute);
  }

  /// What's wrong with the picked end time, or null when nothing is (or the
  /// teacher ends the session at a period's end).
  String? _endTimeProblem(AppLocalizations l10n, DateTime now) {
    if (_endChoice != _EndChoice.specificTime) return null;
    final at = _pickedEnd(now);
    if (at == null) return l10n.homeEndTimeInvalid;
    final latest = _endOptions?.latestEndsAt?.toLocal();
    if (latest != null && at.isAfter(latest)) {
      return l10n.homeEndTimeTooLate(_clockTime(latest));
    }
    return null;
  }

  Future<void> _startSession() async {
    final l10n = AppLocalizations.of(context);
    final klass = _selected;
    if (klass == null) return;
    // The end is worked out at the click: a time picked earlier may have
    // passed in the meantime.
    final now = DateTime.now();
    final SessionEnd end;
    switch (_endChoice) {
      case _EndChoice.thisPeriod:
        end = const SessionEnd.thisPeriod();
      case _EndChoice.nextPeriod:
        end = const SessionEnd.nextPeriod();
      case _EndChoice.specificTime:
        final at = _pickedEnd(now);
        if (at == null || _endTimeProblem(l10n, now) != null) {
          setState(() {});
          return;
        }
        end = SessionEnd.at(at);
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      // Sessions now start with no bundles — baseline-only enforcement. The
      // teacher adds bundles from the live session view (#93).
      final session = await widget.sessions.startSession(klass.id, end: end);
      if (!mounted) return;
      context.go('/session/${session.id}');
    } catch (e) {
      if (!mounted) return;
      // The lesson period ran out while the teacher was choosing: say so,
      // and offer what applies now.
      final periodChanged = isNoLessonPeriodConflict(e);
      setState(
        () => _error = periodChanged
            ? ApiErrorMessage(l10n.homeEndPeriodChanged)
            : describeApiError(
                e,
                generic: l10n.homeStartError,
                notAuthorized: l10n.apiError403,
              ),
      );
      if (periodChanged) unawaited(_loadEndOptions(klass));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// Ends a running session straight from the home screen so the teacher
  /// doesn't have to resume into it just to stop it (#126). On success the card
  /// drops out; the running-now panel hides itself once none remain.
  Future<void> _endActiveSession(String sessionId) async {
    final l10n = AppLocalizations.of(context);
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
          generic: l10n.homeEndError,
          notAuthorized: l10n.apiError403,
        ),
      );
    } finally {
      if (mounted) setState(() => _endingSessions.remove(sessionId));
    }
  }

  String _classNameFor(String classId, AppLocalizations l10n) {
    for (final c in _classes ?? const <ClassSummary>[]) {
      if (c.id == classId) return c.name;
    }
    // Teacher may no longer be listed on the class yet the session runs on —
    // still worth surfacing so it can be reached and ended.
    return l10n.homeActiveSessionFallback;
  }

  @override
  Widget build(BuildContext context) {
    final AppLocalizations l10n = AppLocalizations.of(context);
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
                    AppLocalizations.of(
                      context,
                    ).homeStillRunning(_activeSessions.length),
                  ),
                  const SizedBox(height: PlinkSpacing.s4),
                  for (final ActiveSession s in _activeSessions) ...<Widget>[
                    _ActiveSessionCard(
                      className: _classNameFor(s.classId, l10n),
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
                  l10n.homeHeadline,
                  key: const Key('home-headline'),
                  style: text.displaySmall,
                ),
                const SizedBox(height: PlinkSpacing.s4),
                Text(
                  l10n.homeSubtitle,
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
                    l10n.homeNoClasses,
                    style: text.bodyLarge?.copyWith(color: PlinkColors.ink60),
                  )
                else if (classes != null && classes.isNotEmpty) ...<Widget>[
                  if (department != null && department.isNotEmpty) ...<Widget>[
                    Text(
                      l10n.homeYourDepartment(department.toUpperCase()),
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
                      decoration: InputDecoration(
                        labelText: l10n.homeClassLabel,
                      ),
                      items: <DropdownMenuItem<ClassSummary>>[
                        for (final ClassSummary c in classes)
                          DropdownMenuItem<ClassSummary>(
                            value: c,
                            child: Text('${c.name} (${c.schoolYear})'),
                          ),
                      ],
                      onChanged: _busy
                          ? null
                          : (ClassSummary? value) {
                              setState(() => _selected = value);
                              _loadEndOptions(value);
                            },
                    ),
                  ),
                  const SizedBox(height: PlinkSpacing.s4),
                  _EndPicker(
                    // Built afresh for each answer, so the field starts on
                    // the choice made for the options it offers.
                    key: ValueKey<SessionEndOptions?>(_endOptions),
                    options: _endOptions,
                    choice: _endChoice,
                    endTime: _endTime,
                    problem: _endTimeProblem(l10n, DateTime.now()),
                    enabled: !_busy,
                    onChoice: (choice) => setState(() => _endChoice = choice),
                    onEndTimeChanged: () => setState(() {}),
                  ),
                  const SizedBox(height: PlinkSpacing.s5),
                  // The single primary (magenta) action — the one spark on the
                  // page. The DS theme paints ElevatedButton in the spark.
                  ElevatedButton(
                    key: const Key('start-session'),
                    onPressed:
                        _busy ||
                            _selected == null ||
                            _endOptions == null ||
                            _endTimeProblem(l10n, DateTime.now()) != null
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
                                ? l10n.homeSelectClass
                                : l10n.homeStartSessionFor(_selected!.name),
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

/// How a session the teacher starts ends (#447).
enum _EndChoice { thisPeriod, nextPeriod, specificTime }

/// How far away a picked end time starts out when no period end applies: about
/// a lesson period.
const Duration _defaultSessionLength = Duration(minutes: 50);

/// The end time the picker starts out with at [now]: the end of this period if
/// there is one, otherwise [_defaultSessionLength] away, rounded up to five
/// minutes; never later than the latest end the backend takes.
DateTime _defaultEndTime(SessionEndOptions options, DateTime now) {
  var end = options.thisPeriodEndsAt?.toLocal();
  if (end == null) {
    final later = now.add(_defaultSessionLength);
    final extra = (5 - later.minute % 5) % 5;
    end = DateTime(
      later.year,
      later.month,
      later.day,
      later.hour,
      later.minute + extra,
    );
  }
  final latest = options.latestEndsAt?.toLocal();
  return latest != null && end.isAfter(latest) ? latest : end;
}

/// A local wall-clock time as `HH:mm`.
String _clockTime(DateTime at) {
  final local = at.toLocal();
  return '${local.hour.toString().padLeft(2, '0')}:'
      '${local.minute.toString().padLeft(2, '0')}';
}

/// When the session ends (#447): at the end of this lesson period (the default
/// while there is one), of the next one, or at a time the teacher types. The
/// period choices name their end time; a school without lesson periods, or a
/// start outside every period, offers only a time. While the options load the
/// choice is disabled.
class _EndPicker extends StatelessWidget {
  const _EndPicker({
    super.key,
    required this.options,
    required this.choice,
    required this.endTime,
    required this.problem,
    required this.enabled,
    required this.onChoice,
    required this.onEndTimeChanged,
  });

  final SessionEndOptions? options;
  final _EndChoice choice;
  final TextEditingController endTime;
  final String? problem;
  final bool enabled;
  final ValueChanged<_EndChoice> onChoice;
  final VoidCallback onEndTimeChanged;

  @override
  Widget build(BuildContext context) {
    final AppLocalizations l10n = AppLocalizations.of(context);
    final SessionEndOptions? loaded = options;
    final DateTime? thisPeriod = loaded?.thisPeriodEndsAt;
    final DateTime? nextPeriod = loaded?.nextPeriodEndsAt;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: <Widget>[
        ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 420),
          child: DropdownButtonFormField<_EndChoice>(
            key: const Key('end-picker'),
            initialValue: choice,
            isExpanded: true,
            decoration: InputDecoration(labelText: l10n.homeEndLabel),
            items: <DropdownMenuItem<_EndChoice>>[
              if (thisPeriod != null)
                DropdownMenuItem<_EndChoice>(
                  value: _EndChoice.thisPeriod,
                  child: Text(
                    l10n.homeEndThisPeriod(_clockTime(thisPeriod)),
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
              if (nextPeriod != null)
                DropdownMenuItem<_EndChoice>(
                  value: _EndChoice.nextPeriod,
                  child: Text(
                    l10n.homeEndNextPeriod(_clockTime(nextPeriod)),
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
              DropdownMenuItem<_EndChoice>(
                value: _EndChoice.specificTime,
                child: Text(
                  l10n.homeEndSpecificTime,
                  overflow: TextOverflow.ellipsis,
                ),
              ),
            ],
            onChanged: enabled && loaded != null
                ? (_EndChoice? value) {
                    if (value != null) onChoice(value);
                  }
                : null,
          ),
        ),
        if (loaded != null && choice == _EndChoice.specificTime) ...<Widget>[
          const SizedBox(height: PlinkSpacing.s3),
          ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 420),
            child: TextField(
              key: const Key('end-time'),
              controller: endTime,
              enabled: enabled,
              keyboardType: TextInputType.datetime,
              onChanged: (_) => onEndTimeChanged(),
              decoration: InputDecoration(
                labelText: l10n.homeEndTimeLabel,
                hintText: '11:30',
                errorText: problem,
                errorMaxLines: 2,
              ),
            ),
          ),
        ],
      ],
    );
  }
}

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

  static String _startedTime(DateTime startedAt) {
    final DateTime local = startedAt.toLocal();
    final String h = local.hour.toString().padLeft(2, '0');
    final String mi = local.minute.toString().padLeft(2, '0');
    return '$h:$mi';
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
              PlinkBadge(
                AppLocalizations.of(context).badgeLive,
                variant: BadgeVariant.spark,
                dot: true,
              ),
              const SizedBox(width: PlinkSpacing.s3),
              Text(
                AppLocalizations.of(
                  context,
                ).homeStartedAt(_startedTime(startedAt)),
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
                child: Text(AppLocalizations.of(context).homeResume),
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
                OutlinedButton(
                  onPressed: onEnd,
                  child: Text(AppLocalizations.of(context).homeEnd),
                ),
            ],
          ),
        ],
      ),
    );
  }
}
