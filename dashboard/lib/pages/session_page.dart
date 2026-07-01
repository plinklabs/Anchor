import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:go_router/go_router.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../api/auth_token_store.dart';
import '../api/bundles_api.dart';
import '../api/sessions_api.dart';
import '../l10n/app_localizations.dart';
import '../realtime/session_hub_client.dart';

class SessionPage extends StatefulWidget {
  const SessionPage({
    super.key,
    required this.sessionId,
    required this.tokens,
    required this.sessions,
    required this.bundles,
    required this.apiBaseUrl,
    this.hubClientFactory,
  });

  final String sessionId;
  final AuthTokenStore tokens;
  final SessionsApi sessions;
  final BundlesApi bundles;
  final Uri apiBaseUrl;

  /// Overrides how the live feed is built (#132). Null in production — the
  /// real [SessionHubClient] is used; an integration test injects a stubbed
  /// feed here to push roster / unblock events at the real app.
  final SessionHubClientFactory? hubClientFactory;

  @override
  State<SessionPage> createState() => _SessionPageState();
}

/// What the teacher chose in the back-arrow guard dialog (#126).
enum _ExitChoice { endSession, leaveRunning, cancel }

class _SessionPageState extends State<SessionPage> {
  late final SessionHubClient _hub;
  StreamSubscription<SessionEvent>? _eventsSub;
  final List<SessionEvent> _events = [];
  bool _connecting = true;
  bool _ending = false;
  bool _ended = false;
  String? _error;
  SessionDetail? _detail;
  List<UnblockRequestSummary> _pendingRequests = const [];
  final Set<String> _approving = {};
  String? _unblockError;
  List<BundleSummary>? _availableBundles;
  bool _updatingBundles = false;
  String? _bundleError;

  @override
  void initState() {
    super.initState();
    final buildHub = widget.hubClientFactory ?? SessionHubClient.new;
    _hub = buildHub(
      apiBaseUrl: widget.apiBaseUrl,
      tokenProvider: () async => widget.tokens.token,
    );
    _bootstrap();
  }

  Future<void> _bootstrap() async {
    await _loadDetail();
    // Old bookmarks / direct links to /session/:id of a session that has since
    // ended would otherwise hit Hub.JoinSession and surface a scary exception.
    // The past-session view at /history/:id is the read-only review surface
    // for these — redirect there before touching the hub.
    if (!mounted) return;
    if (_ended) {
      context.go('/history/${widget.sessionId}');
      return;
    }
    await Future.wait([_connect(), _loadPendingRequests(), _loadBundles()]);
  }

  Set<String> get _selectedBundleIds => {
    for (final b in _detail?.bundles ?? const <SessionBundleInfo>[]) b.id,
  };

  Future<void> _loadBundles() async {
    try {
      final list = await widget.bundles.list();
      if (!mounted) return;
      setState(() => _availableBundles = list);
    } catch (_) {
      // Non-fatal: the picker just won't render. The session still runs with
      // whatever bundles it already has.
    }
  }

  Future<void> _updateBundles(Set<String> bundleIds) async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _updatingBundles = true;
      _bundleError = null;
    });
    try {
      await widget.sessions.updateBundles(
        widget.sessionId,
        bundleIds.toList(growable: false),
      );
      // Re-fetch so the chips reflect the source of truth even if this request
      // raced another change.
      await _loadDetail();
    } catch (e) {
      if (!mounted) return;
      setState(() => _bundleError = l10n.sessionUpdateBundlesError('$e'));
    } finally {
      if (mounted) setState(() => _updatingBundles = false);
    }
  }

  Future<void> _loadDetail() async {
    try {
      final detail = await widget.sessions.getSession(widget.sessionId);
      if (!mounted) return;
      setState(() {
        _detail = detail;
        // Navigating to an already-ended session never triggers a SessionEnded
        // broadcast, so seed _ended from persisted state. Without this the
        // summary panel only ever renders during the live-end transition.
        if (detail.endedAt != null) _ended = true;
      });
    } catch (_) {
      // Non-fatal: the live event stream still works without the detail block.
      // The join-code panel just won't render.
    }
  }

  Future<void> _loadPendingRequests() async {
    try {
      final list = await widget.sessions.unblockRequests(widget.sessionId);
      if (!mounted) return;
      setState(() => _pendingRequests = list);
    } catch (_) {
      // Non-fatal: the panel just stays empty on initial load. Subsequent
      // UnblockRequested pushes will still populate it.
    }
  }

  Future<void> _approveHost(UnblockRequestSummary summary) async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _approving.add(summary.host);
      _unblockError = null;
    });
    try {
      // One POST per pending student. Run sequentially: the volume is small
      // (a class is rarely > 30 kids) and serial avoids tripping any
      // backend rate limit we might add later.
      for (final requester in summary.requesters) {
        await widget.sessions.approveUnblock(
          widget.sessionId,
          requester.userId,
          summary.host,
        );
      }
      // Refresh from the source of truth so a request that arrived between
      // initial load and approval doesn't get accidentally hidden.
      await _loadPendingRequests();
    } catch (e) {
      if (!mounted) return;
      setState(() => _unblockError = l10n.sessionApproveError('$e'));
    } finally {
      if (mounted) setState(() => _approving.remove(summary.host));
    }
  }

  /// Whole-class approval (#101): one POST that adds the host to the live
  /// session allowlist for everyone, rather than a grant per requesting student.
  Future<void> _approveHostForClass(UnblockRequestSummary summary) async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _approving.add(summary.host);
      _unblockError = null;
    });
    try {
      await widget.sessions.approveUnblockForClass(
        widget.sessionId,
        summary.host,
      );
      await _loadPendingRequests();
    } catch (e) {
      if (!mounted) return;
      setState(() => _unblockError = l10n.sessionApproveError('$e'));
    } finally {
      if (mounted) setState(() => _approving.remove(summary.host));
    }
  }

  Future<void> _copyJoinCode(String code) async {
    final l10n = AppLocalizations.of(context);
    await Clipboard.setData(ClipboardData(text: code));
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(l10n.sessionCopiedCode(code)),
        duration: const Duration(seconds: 2),
      ),
    );
  }

  Future<void> _connect() async {
    final l10n = AppLocalizations.of(context);
    try {
      await _hub.connect();
      await _hub.joinSession(widget.sessionId);
      _eventsSub = _hub.events.listen((evt) {
        if (!mounted) return;
        setState(() {
          _events.insert(0, evt);
          if (evt.kind == 'SessionEnded' &&
              evt.payload['sessionId'] == widget.sessionId) {
            _ended = true;
          }
        });
        // UnblockRequested = a student just clicked Request access. Re-fetch
        // the pending list rather than maintain a separate in-memory tracker:
        // the GET endpoint already de-dupes per (student, host) and filters
        // out already-granted entries, so this is the cheapest way to stay
        // consistent with the source of truth.
        if (evt.kind == 'UnblockRequested') {
          _loadPendingRequests();
        }
        // Roster transitions (#100): a member joined/declined/left, or their
        // agent stopped/resumed reporting. TamperDetected (#105) likewise flips
        // the server-computed `tampered` flag. Re-fetch the detail so the roster
        // reflects the server-computed per-student state.
        if (evt.kind == 'ParticipantStateChanged' ||
            evt.kind == 'HeartbeatLost' ||
            evt.kind == 'AgentReconnected' ||
            evt.kind == 'TamperDetected') {
          _loadDetail();
        }
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = l10n.sessionConnectError('$e'));
    } finally {
      if (mounted) setState(() => _connecting = false);
    }
  }

  Future<void> _endSession() async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _ending = true;
      _error = null;
    });
    try {
      await widget.sessions.endSession(widget.sessionId);
      // Re-fetch so the post-end summary panel has data. The End response
      // doesn't carry summaries; the GET path does.
      await _loadDetail();
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = l10n.sessionEndError('$e'));
    } finally {
      if (mounted) setState(() => _ending = false);
    }
  }

  /// Back-arrow guard (#126): leaving an active session without a decision is
  /// how it gets orphaned, so ask the teacher to either end it for everyone or
  /// leave it running (reachable later from the home banner). An already-ended
  /// session has nothing to guard — just go home.
  Future<void> _confirmExit() async {
    if (_ended) {
      context.go('/');
      return;
    }

    final l10n = AppLocalizations.of(context);
    final choice = await showDialog<_ExitChoice>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(l10n.sessionLeaveTitle),
        content: Text(l10n.sessionLeaveBody),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, _ExitChoice.cancel),
            child: Text(l10n.actionCancel),
          ),
          TextButton(
            onPressed: () =>
                Navigator.pop(dialogContext, _ExitChoice.leaveRunning),
            child: Text(l10n.sessionLeaveRunning),
          ),
          FilledButton(
            onPressed: () =>
                Navigator.pop(dialogContext, _ExitChoice.endSession),
            child: Text(l10n.sessionEndSession),
          ),
        ],
      ),
    );

    if (!mounted || choice == null || choice == _ExitChoice.cancel) return;

    if (choice == _ExitChoice.leaveRunning) {
      context.go('/');
      return;
    }

    // End, then leave. Unlike the AppBar's End button (which stays to show the
    // summary), the teacher asked to leave — so navigate home on success.
    setState(() {
      _ending = true;
      _error = null;
    });
    try {
      await widget.sessions.endSession(widget.sessionId);
      if (!mounted) return;
      context.go('/');
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = l10n.sessionEndError('$e'));
    } finally {
      if (mounted) setState(() => _ending = false);
    }
  }

  @override
  void dispose() {
    _eventsSub?.cancel();
    _hub.dispose();
    super.dispose();
  }

  String _titleText(AppLocalizations l10n) {
    final started = _detail?.startedAt.toLocal();
    if (started == null) return l10n.sessionTitleFallback;
    final y = started.year.toString().padLeft(4, '0');
    final mo = started.month.toString().padLeft(2, '0');
    final d = started.day.toString().padLeft(2, '0');
    final h = started.hour.toString().padLeft(2, '0');
    final mi = started.minute.toString().padLeft(2, '0');
    final datetime = '$y-$mo-$d $h:$mi';
    return l10n.sessionTitleSpec(datetime);
  }

  @override
  Widget build(BuildContext context) {
    final TextTheme text = Theme.of(context).textTheme;
    final String className = _detail?.className ?? '';
    final String headline = className.isEmpty
        ? AppLocalizations.of(context).sessionHeadlineFallback
        : className;

    // The instrument panels above the feed — only what applies to the session's
    // current state. The feed itself is the hero and fills the rest below.
    final List<Widget> panels = <Widget>[
      if (!_ended && (_detail?.joinCode.isNotEmpty ?? false))
        _JoinCodePanel(
          code: _detail!.joinCode,
          onCopy: () => _copyJoinCode(_detail!.joinCode),
        ),
      if (!_ended && _pendingRequests.isNotEmpty)
        _PendingRequestsPanel(
          requests: _pendingRequests,
          approving: _approving,
          error: _unblockError,
          onApprove: _approveHost,
          onApproveClass: _approveHostForClass,
        ),
      if (!_ended && (_detail?.participants.isNotEmpty ?? false))
        _RosterPanel(participants: _detail!.participants),
      if (!_ended && _availableBundles != null)
        _LiveBundlePanel(
          available: _availableBundles!,
          selectedIds: _selectedBundleIds,
          busy: _updatingBundles,
          error: _bundleError,
          onToggle: (id, selected) {
            final next = {..._selectedBundleIds};
            if (selected) {
              next.add(id);
            } else {
              next.remove(id);
            }
            _updateBundles(next);
          },
        ),
      if (_ended) const _EndedBanner(),
      if (_ended && (_detail?.summaries.isNotEmpty ?? false))
        _SessionSummaryPanel(summaries: _detail!.summaries),
    ];

    return Scaffold(
      backgroundColor: PlinkColors.paper,
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: <Widget>[
          _SessionHeader(
            headline: headline,
            spec: _titleText(AppLocalizations.of(context)),
            ended: _ended,
            ending: _ending,
            onLeave: _confirmExit,
            onEnd: _endSession,
            headlineStyle: text.displaySmall,
          ),
          const _Hairline(),
          if (_connecting)
            _StatusLine(AppLocalizations.of(context).sessionConnecting),
          if (_error != null) _ErrorBanner(_error!),
          for (final Widget panel in panels) ...<Widget>[
            panel,
            const _Hairline(),
          ],
          // The live feed — the instrument's read-out, filling the rest.
          _PanelLabel(AppLocalizations.of(context).sessionActivity),
          Expanded(child: _Feed(events: _events)),
        ],
      ),
    );
  }
}

/// Horizontal page gutter — keeps the live-session content on the same
/// flush-left margin as the shell's eyebrow and app-bar (the editorial column).
const double _gutter = PlinkSpacing.s6; // 32

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

/// A sentence-case mono section label (Space Mono) — the quiet panel headers
/// that read like specs on an instrument, never shouting.
class _PanelLabel extends StatelessWidget {
  const _PanelLabel(this.text);

  final String text;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        _gutter,
        PlinkSpacing.s4,
        _gutter,
        PlinkSpacing.s2,
      ),
      child: Text(text, style: _monoLabel(PlinkColors.ink60)),
    );
  }
}

/// Space Mono label style — labels, specs, counts.
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

/// A tabular-figure mono style for timestamps and codes — columns line up.
TextStyle _monoSpec(Color color, double size) => TextStyle(
  fontFamily: PlinkType.monoFamily,
  package: PlinkType.fontPackage,
  fontFamilyFallback: PlinkType.monoFallback,
  fontSize: size,
  color: color,
  height: 1.3,
  fontFeatures: const <FontFeature>[FontFeature.tabularFigures()],
);

/// The header: leave-affordance, the session identity (class name + the
/// date/time spec), the liveness mark, and End session. The shell already
/// carries the "Live session" eyebrow above this, so the page leads with the
/// class it belongs to.
class _SessionHeader extends StatelessWidget {
  const _SessionHeader({
    required this.headline,
    required this.spec,
    required this.ended,
    required this.ending,
    required this.onLeave,
    required this.onEnd,
    required this.headlineStyle,
  });

  final String headline;
  final String spec;
  final bool ended;
  final bool ending;
  final VoidCallback onLeave;
  final VoidCallback onEnd;
  final TextStyle? headlineStyle;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        PlinkSpacing.s3,
        PlinkSpacing.s2,
        _gutter,
        PlinkSpacing.s4,
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          IconButton(
            icon: const Icon(Icons.arrow_back),
            color: PlinkColors.ink,
            tooltip: AppLocalizations.of(context).sessionLeaveTooltip,
            onPressed: onLeave,
          ),
          const SizedBox(width: PlinkSpacing.s2),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                // The liveness spec line: the ping motif + a LIVE/ENDED badge,
                // then the date/time spec in mono.
                Row(
                  children: <Widget>[
                    if (!ended) ...<Widget>[
                      // Static ping — the ping motif marks liveness without an
                      // ambient loop, so the page stays calm (and tests settle);
                      // the magenta badge carries the live signal (matches AD3).
                      const Ping(size: 16, mode: PingMode.static),
                      const SizedBox(width: PlinkSpacing.s2),
                      PlinkBadge(
                        AppLocalizations.of(context).badgeLive,
                        variant: BadgeVariant.spark,
                      ),
                    ] else
                      PlinkBadge(
                        AppLocalizations.of(context).badgeEnded,
                        variant: BadgeVariant.outline,
                      ),
                    const SizedBox(width: PlinkSpacing.s3),
                    Flexible(
                      child: Text(
                        spec,
                        style: _monoLabel(PlinkColors.ink60),
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: PlinkSpacing.s2),
                Text(
                  headline,
                  style: headlineStyle,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
              ],
            ),
          ),
          if (!ended) ...<Widget>[
            const SizedBox(width: PlinkSpacing.s4),
            // End is a calm ink action, never the magenta spark — ending a
            // class's session should read as deliberate, not alarming.
            OutlinedButton(
              onPressed: ending ? null : onEnd,
              child: ending
                  ? const SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : Text(AppLocalizations.of(context).sessionEndSession),
            ),
          ],
        ],
      ),
    );
  }
}

/// A quiet single-line status (e.g. connecting) — mono, muted, no spinner bar
/// competing for the eye.
class _StatusLine extends StatelessWidget {
  const _StatusLine(this.text);

  final String text;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        _gutter,
        PlinkSpacing.s3,
        _gutter,
        PlinkSpacing.s3,
      ),
      child: Text(text, style: _monoLabel(PlinkColors.muted)),
    );
  }
}

/// An error strip — a hairline-bounded panel in the error colour, not a loud
/// filled banner.
class _ErrorBanner extends StatelessWidget {
  const _ErrorBanner(this.message);

  final String message;

  @override
  Widget build(BuildContext context) {
    final Color error = Theme.of(context).colorScheme.error;
    return Container(
      width: double.infinity,
      margin: const EdgeInsets.fromLTRB(
        _gutter,
        PlinkSpacing.s3,
        _gutter,
        PlinkSpacing.s3,
      ),
      padding: const EdgeInsets.all(PlinkSpacing.s3),
      decoration: BoxDecoration(
        border: Border.all(color: error, width: PlinkBorders.width),
        borderRadius: BorderRadius.circular(PlinkRadius.base),
      ),
      child: Text(
        message,
        style: Theme.of(context).textTheme.bodyMedium?.copyWith(color: error),
      ),
    );
  }
}

/// The session-ended notice — a calm hairline panel, not a loud fill.
class _EndedBanner extends StatelessWidget {
  const _EndedBanner();

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        _gutter,
        PlinkSpacing.s4,
        _gutter,
        PlinkSpacing.s4,
      ),
      child: Text(
        AppLocalizations.of(context).sessionEndedBanner,
        style: Theme.of(
          context,
        ).textTheme.bodyMedium?.copyWith(color: PlinkColors.ink60),
      ),
    );
  }
}

/// The live event feed — the instrument's read-out. Each event is a hairline
/// row: a mono tabular timestamp, a calm status dot, the raw event kind as a
/// mono technical label, and a condensed payload. Scannable, not alarming.
class _Feed extends StatelessWidget {
  const _Feed({required this.events});

  final List<SessionEvent> events;

  @override
  Widget build(BuildContext context) {
    if (events.isEmpty) {
      return Center(
        child: Text(
          AppLocalizations.of(context).sessionWaitingEvents,
          style: _monoLabel(PlinkColors.muted),
        ),
      );
    }
    return ListView.separated(
      padding: const EdgeInsets.fromLTRB(
        _gutter,
        PlinkSpacing.s1,
        _gutter,
        PlinkSpacing.s6,
      ),
      itemCount: events.length,
      separatorBuilder: (_, _) => const _Hairline(),
      itemBuilder: (context, i) => _EventRow(event: events[i]),
    );
  }
}

/// The event kinds that warrant a stronger (but small, never alarming) marker —
/// the ones a teacher might act on.
const Set<String> _attentionKinds = <String>{'HeartbeatLost', 'TamperDetected'};

class _EventRow extends StatelessWidget {
  const _EventRow({required this.event});

  final SessionEvent event;

  static String _time(DateTime at) {
    final DateTime t = at.toLocal();
    return '${t.hour.toString().padLeft(2, '0')}:'
        '${t.minute.toString().padLeft(2, '0')}:'
        '${t.second.toString().padLeft(2, '0')}';
  }

  String _payloadSummary() {
    if (event.payload.isEmpty) return '';
    return event.payload.entries.map((e) => '${e.key}=${e.value}').join('  ');
  }

  @override
  Widget build(BuildContext context) {
    final bool attention = _attentionKinds.contains(event.kind);
    final Color dot = attention
        ? Theme.of(context).colorScheme.error
        : PlinkColors.muted;
    final String payload = _payloadSummary();

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: PlinkSpacing.s3),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          // Mono tabular timestamp — the columns line up like a log.
          SizedBox(
            width: 76,
            child: Text(
              _time(event.at),
              style: _monoSpec(PlinkColors.ink60, 12),
            ),
          ),
          // A small status dot — paired with the label, never colour alone.
          Padding(
            padding: const EdgeInsets.only(top: 3),
            child: Container(
              width: 7,
              height: 7,
              decoration: BoxDecoration(color: dot, shape: BoxShape.circle),
            ),
          ),
          const SizedBox(width: PlinkSpacing.s3),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Text(event.kind, style: _monoLabel(PlinkColors.ink)),
                if (payload.isNotEmpty) ...<Widget>[
                  const SizedBox(height: 2),
                  Text(
                    payload,
                    style: _monoSpec(PlinkColors.muted, PlinkType.labelSm),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// Visual treatment for one live participant state (#100). The stale state is
/// the loudest — per design §5.4 it's "agent stopped reporting", the signal
/// teachers actually act on.
class _RosterStateStyle {
  const _RosterStateStyle(this.icon, this.sortRank);
  final IconData icon;

  /// Lower sorts higher. Attention-needing states cluster at the top; the bulk
  /// of normally-joined students sits below.
  final int sortRank;

  static _RosterStateStyle of(ParticipantLiveState state) {
    switch (state) {
      case ParticipantLiveState.heartbeatStale:
        return const _RosterStateStyle(Icons.sensors_off, 0);
      case ParticipantLiveState.left:
        return const _RosterStateStyle(Icons.logout, 1);
      case ParticipantLiveState.declined:
        return const _RosterStateStyle(Icons.cancel_outlined, 2);
      case ParticipantLiveState.neverJoined:
        return const _RosterStateStyle(Icons.radio_button_unchecked, 3);
      case ParticipantLiveState.joined:
        return const _RosterStateStyle(Icons.check_circle, 4);
      case ParticipantLiveState.unknown:
        return const _RosterStateStyle(Icons.help_outline, 5);
    }
  }

  /// Brand-token colours only — no off-palette green/orange. Stale is the one
  /// state painted in the error colour (the signal to act on); everyone else is
  /// calm ink / muted, leaning on the label, never colour alone.
  Color color(ColorScheme scheme) {
    switch (icon) {
      case Icons.sensors_off:
        return scheme.error;
      case Icons.check_circle:
        return PlinkColors.ink60;
      default:
        return PlinkColors.muted;
    }
  }
}

/// The localized label for one live participant state (#100).
String _rosterStateLabel(AppLocalizations l10n, ParticipantLiveState s) {
  switch (s) {
    case ParticipantLiveState.heartbeatStale:
      return l10n.sessionStateStale;
    case ParticipantLiveState.left:
      return l10n.sessionStateLeft;
    case ParticipantLiveState.declined:
      return l10n.sessionStateDeclined;
    case ParticipantLiveState.neverJoined:
      return l10n.sessionStateNotJoined;
    case ParticipantLiveState.joined:
      return l10n.sessionStateInSession;
    case ParticipantLiveState.unknown:
      return l10n.sessionStateUnknown;
  }
}

class _RosterPanel extends StatelessWidget {
  const _RosterPanel({required this.participants});

  final List<SessionParticipantInfo> participants;

  @override
  Widget build(BuildContext context) {
    // Sort by state (attention-first) then name — the acceptance criterion.
    final sorted = [...participants]
      ..sort((a, b) {
        final byState = _RosterStateStyle.of(
          a.state,
        ).sortRank.compareTo(_RosterStateStyle.of(b.state).sortRank);
        if (byState != 0) return byState;
        return a.displayName.toLowerCase().compareTo(
          b.displayName.toLowerCase(),
        );
      });

    final joinedCount = participants
        .where((p) => p.state == ParticipantLiveState.joined)
        .length;

    return Padding(
      padding: const EdgeInsets.fromLTRB(
        _gutter,
        PlinkSpacing.s4,
        _gutter,
        PlinkSpacing.s4,
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Text(
            AppLocalizations.of(
              context,
            ).sessionStudentsCount(joinedCount, participants.length),
            style: _monoLabel(PlinkColors.ink60),
          ),
          const SizedBox(height: PlinkSpacing.s3),
          // Bounded so a 30-student class doesn't push the event feed off-screen
          // (a11y: it scrolls inside the panel, never clips).
          ConstrainedBox(
            constraints: const BoxConstraints(maxHeight: 240),
            child: ListView.builder(
              shrinkWrap: true,
              itemCount: sorted.length,
              itemBuilder: (context, i) => _RosterRow(participant: sorted[i]),
            ),
          ),
        ],
      ),
    );
  }
}

class _RosterRow extends StatelessWidget {
  const _RosterRow({required this.participant});

  final SessionParticipantInfo participant;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final style = _RosterStateStyle.of(participant.state);
    final color = style.color(theme.colorScheme);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: PlinkSpacing.s2),
      child: Row(
        children: [
          Icon(style.icon, size: 18, color: color),
          const SizedBox(width: PlinkSpacing.s3),
          Expanded(
            child: Text(
              participant.displayName,
              style: theme.textTheme.bodyMedium,
              overflow: TextOverflow.ellipsis,
            ),
          ),
          // Tamper flag (#105): soft enforcement can't prevent a student from
          // sidestepping the extension, so we make the attempt visible here.
          // Orthogonal to live state — a tampered student may still be "joined".
          if (participant.tampered) ...[
            Tooltip(
              message: AppLocalizations.of(context).sessionTamperTooltip,
              child: Icon(
                Icons.gpp_maybe,
                size: 18,
                color: theme.colorScheme.error,
              ),
            ),
            const SizedBox(width: PlinkSpacing.s2),
          ],
          Text(
            _rosterStateLabel(AppLocalizations.of(context), participant.state),
            style: _monoLabel(color),
          ),
        ],
      ),
    );
  }
}

class _PendingRequestsPanel extends StatelessWidget {
  const _PendingRequestsPanel({
    required this.requests,
    required this.approving,
    required this.error,
    required this.onApprove,
    required this.onApproveClass,
  });

  final List<UnblockRequestSummary> requests;
  final Set<String> approving;
  final String? error;
  final void Function(UnblockRequestSummary) onApprove;
  final void Function(UnblockRequestSummary) onApproveClass;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        _gutter,
        PlinkSpacing.s4,
        _gutter,
        PlinkSpacing.s4,
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Text(
            AppLocalizations.of(context).sessionPendingRequests,
            style: _monoLabel(PlinkColors.ink60),
          ),
          if (error != null) ...[
            const SizedBox(height: PlinkSpacing.s2),
            Text(error!, style: TextStyle(color: theme.colorScheme.error)),
          ],
          const SizedBox(height: PlinkSpacing.s3),
          // Bounded with a scroll so a flurry of requests can't push the feed
          // off-screen (a11y: reflow inside the panel, never clip).
          ConstrainedBox(
            constraints: const BoxConstraints(maxHeight: 240),
            child: ListView.builder(
              shrinkWrap: true,
              itemCount: requests.length,
              itemBuilder: (context, i) => Padding(
                padding: const EdgeInsets.only(bottom: PlinkSpacing.s2),
                child: _PendingRequestRow(
                  summary: requests[i],
                  isApproving: approving.contains(requests[i].host),
                  onApprove: () => onApprove(requests[i]),
                  onApproveClass: () => onApproveClass(requests[i]),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _PendingRequestRow extends StatelessWidget {
  const _PendingRequestRow({
    required this.summary,
    required this.isApproving,
    required this.onApprove,
    required this.onApproveClass,
  });

  final UnblockRequestSummary summary;
  final bool isApproving;
  final VoidCallback onApprove;
  final VoidCallback onApproveClass;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final names = summary.requesters.map((r) => r.displayName).join(', ');
    return Row(
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              // The host is technical — set it in mono, like a spec.
              Text(
                summary.host,
                style: _monoSpec(PlinkColors.ink, PlinkType.textSm),
              ),
              const SizedBox(height: 2),
              Text(
                AppLocalizations.of(
                  context,
                ).sessionRequesters(summary.count, names),
                style: theme.textTheme.bodySmall,
              ),
            ],
          ),
        ),
        const SizedBox(width: PlinkSpacing.s3),
        // Primary action is per-student — the design's safer default (#101) —
        // and a calm ink button, not the magenta spark. The broader "whole
        // class" scope is tucked behind the kebab so it takes a deliberate tap.
        OutlinedButton(
          onPressed: isApproving ? null : onApprove,
          child: isApproving
              ? const SizedBox(
                  width: 14,
                  height: 14,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : Text(AppLocalizations.of(context).actionApprove),
        ),
        PopupMenuButton<String>(
          tooltip: AppLocalizations.of(context).sessionMoreApprovalOptions,
          enabled: !isApproving,
          onSelected: (value) {
            if (value == 'class') onApproveClass();
          },
          itemBuilder: (context) => [
            PopupMenuItem<String>(
              value: 'class',
              child: Text(
                AppLocalizations.of(context).sessionApproveWholeClass,
              ),
            ),
          ],
        ),
      ],
    );
  }
}

class _SessionSummaryPanel extends StatelessWidget {
  const _SessionSummaryPanel({required this.summaries});

  final List<SessionEventSummary> summaries;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final byKind = <String, int>{};
    for (final s in summaries) {
      byKind[s.kind] = (byKind[s.kind] ?? 0) + s.count;
    }
    final lines = byKind.entries
        .map((e) => '${e.value} ${e.key}')
        .toList(growable: false);
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        _gutter,
        PlinkSpacing.s4,
        _gutter,
        PlinkSpacing.s4,
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Text(
            AppLocalizations.of(context).sessionSummary,
            style: _monoLabel(PlinkColors.ink60),
          ),
          const SizedBox(height: PlinkSpacing.s2),
          Text(lines.join('  ·  '), style: theme.textTheme.bodyMedium),
        ],
      ),
    );
  }
}

class _LiveBundlePanel extends StatelessWidget {
  const _LiveBundlePanel({
    required this.available,
    required this.selectedIds,
    required this.busy,
    required this.error,
    required this.onToggle,
  });

  final List<BundleSummary> available;
  final Set<String> selectedIds;
  final bool busy;
  final String? error;
  final void Function(String id, bool selected) onToggle;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        _gutter,
        PlinkSpacing.s4,
        _gutter,
        PlinkSpacing.s4,
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Row(
            children: <Widget>[
              Text(
                AppLocalizations.of(context).sessionAllowedBundles,
                style: _monoLabel(PlinkColors.ink60),
              ),
              if (busy) ...<Widget>[
                const SizedBox(width: PlinkSpacing.s3),
                const SizedBox(
                  width: 14,
                  height: 14,
                  child: CircularProgressIndicator(strokeWidth: 2),
                ),
              ],
            ],
          ),
          if (error != null) ...[
            const SizedBox(height: PlinkSpacing.s2),
            Text(error!, style: TextStyle(color: theme.colorScheme.error)),
          ],
          const SizedBox(height: PlinkSpacing.s3),
          if (available.isEmpty)
            Text(
              AppLocalizations.of(context).sessionNoBundles,
              style: theme.textTheme.bodySmall,
            )
          else
            Wrap(
              spacing: PlinkSpacing.s2,
              runSpacing: PlinkSpacing.s2,
              children: <Widget>[
                for (final b in available)
                  FilterChip(
                    label: Text(b.name),
                    selected: selectedIds.contains(b.id),
                    // Calm, square-crisp chips: hairline border, paper fills, an
                    // ink check — never a magenta pill (the spark is reserved).
                    showCheckmark: true,
                    checkmarkColor: PlinkColors.ink,
                    backgroundColor: PlinkColors.paper,
                    selectedColor: PlinkColors.paper2,
                    side: const BorderSide(
                      color: PlinkColors.hairlineStrong,
                      width: PlinkBorders.width,
                    ),
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(PlinkRadius.base),
                    ),
                    labelStyle: theme.textTheme.bodySmall?.copyWith(
                      color: PlinkColors.ink,
                    ),
                    onSelected: busy
                        ? null
                        : (selected) => onToggle(b.id, selected),
                  ),
              ],
            ),
        ],
      ),
    );
  }
}

class _JoinCodePanel extends StatelessWidget {
  const _JoinCodePanel({required this.code, required this.onCopy});

  final String code;
  final VoidCallback onCopy;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        _gutter,
        PlinkSpacing.s4,
        _gutter,
        PlinkSpacing.s4,
      ),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  AppLocalizations.of(context).sessionJoinCode,
                  style: _monoLabel(PlinkColors.ink60),
                ),
                const SizedBox(height: PlinkSpacing.s2),
                Text(
                  // Big enough to be legible across a classroom — the fallback
                  // path for any student who didn't get the roster-based push
                  // (substitute, transferred class, etc). Mono tabular so the
                  // glyphs sit on an even grid.
                  code,
                  style: _monoSpec(
                    PlinkColors.ink,
                    PlinkType.display3,
                  ).copyWith(fontWeight: FontWeight.w700, letterSpacing: 8),
                ),
              ],
            ),
          ),
          IconButton(
            tooltip: AppLocalizations.of(context).sessionCopyCode,
            color: PlinkColors.ink,
            icon: const Icon(Icons.copy),
            onPressed: onCopy,
          ),
        ],
      ),
    );
  }
}
