import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../api/sessions_api.dart';
import '../l10n/app_localizations.dart';
import 'past_session_shared.dart';

/// Read-only review of an ended session (AD7, #172), redesigned to the paper
/// treatment. It mirrors the live session's instrument-panel layout (AD4, #169)
/// — a header, hairline-separated panels each headed by a quiet mono label, and
/// an event log read-out — but in a muted/archived register: no liveness ping,
/// no magenta spark, no Approve/End actions. Where the live page leads with the
/// magenta LIVE badge, the archive leads with a calm "Ended" outline badge; the
/// page is paper throughout and paints no spark, because nothing here is live
/// or constructive.
///
/// Distinct from the live SessionPage: no hub connection, no live event stream —
/// just an audit trail (header → bundles → participants → activity summary →
/// approved exceptions → unapproved requests → event log).
///
/// Purely presentational: the load/redirect logic (bounce to the live view if
/// the session hasn't actually ended) is untouched.
class PastSessionPage extends StatefulWidget {
  const PastSessionPage({
    super.key,
    required this.sessionId,
    required this.sessions,
  });

  final String sessionId;
  final SessionsApi sessions;

  @override
  State<PastSessionPage> createState() => _PastSessionPageState();
}

class _PastSessionPageState extends State<PastSessionPage> {
  SessionDetail? _detail;
  List<UnblockRequestSummary> _unapprovedRequests = const [];
  bool _loading = true;
  String? _error;

  bool _didInitialLoad = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (!_didInitialLoad) {
      _didInitialLoad = true;
      _load();
    }
  }

  Future<void> _load() async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      // The unblock-requests endpoint already filters out anything that
      // received a grant, so on an ended session it returns exactly the
      // requests the teacher never acted on.
      final detailFuture = widget.sessions.getSession(widget.sessionId);
      final unapprovedFuture = widget.sessions.unblockRequests(
        widget.sessionId,
      );
      final detail = await detailFuture;
      final unapproved = await unapprovedFuture;
      if (!mounted) return;
      // If a teacher opens /history/<id> for a session that hasn't actually
      // ended yet (race against the live view, or a manually typed URL),
      // bounce them to the live page rather than render half a review.
      if (detail.endedAt == null) {
        context.go('/session/${widget.sessionId}');
        return;
      }
      setState(() {
        _detail = detail;
        _unapprovedRequests = unapproved;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = l10n.pastLoadError('$e'));
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(backgroundColor: PlinkColors.paper, body: _buildBody());
  }

  Widget _buildBody() {
    if (_loading && _detail == null) {
      return const Center(child: CircularProgressIndicator());
    }
    if (_detail == null) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(PlinkSpacing.s6),
          child: Text(
            _error ?? AppLocalizations.of(context).pastNotAvailable,
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
              color: Theme.of(context).colorScheme.error,
            ),
            textAlign: TextAlign.center,
          ),
        ),
      );
    }
    final detail = _detail!;
    return Align(
      alignment: Alignment.topCenter,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 920),
        child: ListView(
          padding: EdgeInsets.zero,
          children: <Widget>[
            _Header(detail: detail),
            const PastHairline(),
            if (detail.bundles.isNotEmpty) ...<Widget>[
              _PanelLabel(AppLocalizations.of(context).pastBundlesUsed),
              _BundlesChips(bundles: detail.bundles),
              const PastHairline(),
            ],
            if (detail.participants.isNotEmpty) ...<Widget>[
              _PanelLabel(AppLocalizations.of(context).pastParticipants),
              _ParticipantsList(participants: detail.participants),
              const PastHairline(),
            ],
            if (detail.summaries.isNotEmpty) ...<Widget>[
              _PanelLabel(AppLocalizations.of(context).pastActivitySummary),
              _SummaryList(
                summaries: detail.summaries,
                participants: detail.participants,
              ),
              const PastHairline(),
            ],
            if (detail.grants.isNotEmpty) ...<Widget>[
              _PanelLabel(AppLocalizations.of(context).pastApprovedExceptions),
              _GrantsList(grants: detail.grants),
              const PastHairline(),
            ],
            if (_unapprovedRequests.isNotEmpty) ...<Widget>[
              _PanelLabel(AppLocalizations.of(context).pastUnapprovedRequests),
              _UnapprovedList(requests: _unapprovedRequests),
              const PastHairline(),
            ],
            _PanelLabel(AppLocalizations.of(context).pastEventLog),
            _EventLog(
              events: detail.recentEvents,
              participants: detail.participants,
            ),
          ],
        ),
      ),
    );
  }
}

/// Horizontal page gutter — keeps the archive content on the same flush-left
/// margin as the shell's eyebrow and the live-session page (the editorial
/// column).
const double _gutter = PlinkSpacing.s6; // 32

/// The header: the session identity (class name + a date/time/duration spec in
/// mono) and a calm "Ended" outline badge — the archived counterpart of the
/// live page's magenta LIVE spark. The shell already carries the "Past session"
/// eyebrow above this, so the page leads with the class it belongs to.
class _Header extends StatelessWidget {
  const _Header({required this.detail});
  final SessionDetail detail;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final started = detail.startedAt.toLocal();
    final ended = detail.endedAt!.toLocal();
    final duration = ended.difference(started);
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
          // The archived marker — an outline badge, never the magenta spark.
          PlinkBadge(
            AppLocalizations.of(context).badgeEnded,
            variant: BadgeVariant.outline,
          ),
          const SizedBox(height: PlinkSpacing.s3),
          Text(
            detail.className.isEmpty
                ? AppLocalizations.of(context).pastSessionFallback
                : detail.className,
            style: theme.textTheme.headlineSmall?.copyWith(
              color: PlinkColors.ink,
            ),
          ),
          const SizedBox(height: PlinkSpacing.s2),
          Text(
            '${pastFormatDate(started)}  ·  '
            '${pastFormatTime(started)} – ${pastFormatTime(ended)}  ·  '
            '${pastFormatDuration(duration)}',
            style: pastMonoSpec(PlinkColors.ink60, PlinkType.textSm),
          ),
        ],
      ),
    );
  }
}

/// A quiet mono section label — the archive's panel header, matching the live
/// feed's "Activity" label but in the muted register.
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
      child: Text(text, style: pastMonoLabel(PlinkColors.ink60)),
    );
  }
}

class _BundlesChips extends StatelessWidget {
  const _BundlesChips({required this.bundles});
  final List<SessionBundleInfo> bundles;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        _gutter,
        PlinkSpacing.s1,
        _gutter,
        PlinkSpacing.s4,
      ),
      child: Wrap(
        spacing: PlinkSpacing.s2,
        runSpacing: PlinkSpacing.s2,
        children: <Widget>[
          // Bundles read as calm outline spec chips — no fill, never the spark.
          for (final b in bundles)
            PlinkBadge(b.name, variant: BadgeVariant.outline),
        ],
      ),
    );
  }
}

/// A vertically-padded panel body that lays its [rows] out as hairline
/// instrument lines — the shared shape for participants / summary / grants /
/// unapproved / event-log, so they all read consistently.
class _PanelRows extends StatelessWidget {
  const _PanelRows({required this.rows});
  final List<Widget> rows;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        _gutter,
        PlinkSpacing.s1,
        _gutter,
        PlinkSpacing.s4,
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: <Widget>[
          for (var i = 0; i < rows.length; i++) ...<Widget>[
            if (i > 0) const PastHairline(),
            Padding(
              padding: const EdgeInsets.symmetric(vertical: PlinkSpacing.s3),
              child: rows[i],
            ),
          ],
        ],
      ),
    );
  }
}

class _ParticipantsList extends StatelessWidget {
  const _ParticipantsList({required this.participants});
  final List<SessionParticipantInfo> participants;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context);
    return _PanelRows(
      rows: <Widget>[
        for (final p in participants)
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              Expanded(
                flex: 3,
                child: Text(
                  p.displayName,
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: PlinkColors.ink,
                  ),
                ),
              ),
              Expanded(
                flex: 5,
                child: Text(
                  _participantStatus(l10n, p),
                  style: pastMonoSpec(PlinkColors.ink60, PlinkType.labelSm),
                ),
              ),
            ],
          ),
      ],
    );
  }

  String _participantStatus(AppLocalizations l10n, SessionParticipantInfo p) {
    if (p.declinedAt != null) {
      return l10n.pastStatusDeclinedAt(pastFormatTime(p.declinedAt!.toLocal()));
    }
    if (p.joinedAt == null) {
      return l10n.pastStatusNeverJoined;
    }
    if (p.leftAt != null) {
      return l10n.pastStatusJoinedLeft(
        pastFormatTime(p.joinedAt!.toLocal()),
        pastFormatTime(p.leftAt!.toLocal()),
      );
    }
    return l10n.pastStatusJoinedAt(pastFormatTime(p.joinedAt!.toLocal()));
  }
}

class _SummaryList extends StatelessWidget {
  const _SummaryList({required this.summaries, required this.participants});
  final List<SessionEventSummary> summaries;
  final List<SessionParticipantInfo> participants;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final nameById = {for (final p in participants) p.userId: p.displayName};
    // Group per student so a row reads "Alice — 12 ForegroundChange, 3 BlockedUrl"
    // rather than one row per (student, kind). Easier to scan at a glance.
    final byUser = <String, List<SessionEventSummary>>{};
    for (final s in summaries) {
      byUser.putIfAbsent(s.userId, () => []).add(s);
    }
    return _PanelRows(
      rows: <Widget>[
        for (final entry in byUser.entries)
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              Expanded(
                flex: 3,
                child: Text(
                  nameById[entry.key] ?? entry.key,
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: PlinkColors.ink,
                  ),
                ),
              ),
              Expanded(
                flex: 5,
                child: Text(
                  entry.value.map((s) => '${s.count} ${s.kind}').join(', '),
                  style: pastMonoSpec(PlinkColors.ink60, PlinkType.labelSm),
                ),
              ),
            ],
          ),
      ],
    );
  }
}

class _GrantsList extends StatelessWidget {
  const _GrantsList({required this.grants});
  final List<SessionUnblockGrantInfo> grants;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return _PanelRows(
      rows: <Widget>[
        for (final g in grants)
          Row(
            children: <Widget>[
              Expanded(
                flex: 3,
                child: Text(
                  g.host,
                  style: pastMonoSpec(PlinkColors.ink, PlinkType.textSm),
                ),
              ),
              Expanded(
                flex: 3,
                child: Text(
                  g.displayName,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: PlinkColors.ink60,
                  ),
                ),
              ),
              Text(
                pastFormatTime(g.grantedAt.toLocal()),
                style: pastMonoSpec(PlinkColors.ink60, PlinkType.labelSm),
              ),
            ],
          ),
      ],
    );
  }
}

class _UnapprovedList extends StatelessWidget {
  const _UnapprovedList({required this.requests});
  final List<UnblockRequestSummary> requests;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return _PanelRows(
      rows: <Widget>[
        for (final r in requests)
          Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              Row(
                children: <Widget>[
                  Expanded(
                    child: Text(
                      r.host,
                      style: pastMonoSpec(PlinkColors.ink, PlinkType.textSm),
                    ),
                  ),
                  Text(
                    pastFormatTime(r.latestRequestedAt.toLocal()),
                    style: pastMonoSpec(PlinkColors.ink60, PlinkType.labelSm),
                  ),
                ],
              ),
              const SizedBox(height: 2),
              Text(
                r.requesters.map((req) => req.displayName).join(', '),
                style: theme.textTheme.bodySmall?.copyWith(
                  color: PlinkColors.ink60,
                ),
              ),
            ],
          ),
      ],
    );
  }
}

/// The event log — the archive's read-out, the muted counterpart of the live
/// feed. Each event is a hairline row: a mono tabular timestamp, the student
/// name, the raw event kind as a mono technical label, and a condensed payload.
class _EventLog extends StatelessWidget {
  const _EventLog({required this.events, required this.participants});
  final List<SessionRecentEvent> events;
  final List<SessionParticipantInfo> participants;

  @override
  Widget build(BuildContext context) {
    if (events.isEmpty) {
      return Padding(
        padding: const EdgeInsets.fromLTRB(
          _gutter,
          PlinkSpacing.s1,
          _gutter,
          PlinkSpacing.s6,
        ),
        child: Text(
          AppLocalizations.of(context).pastNoEventDetail,
          style: pastMonoLabel(PlinkColors.muted),
        ),
      );
    }
    final nameById = {for (final p in participants) p.userId: p.displayName};
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        _gutter,
        PlinkSpacing.s1,
        _gutter,
        PlinkSpacing.s6,
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: <Widget>[
          for (var i = 0; i < events.length; i++) ...<Widget>[
            if (i > 0) const PastHairline(),
            Padding(
              padding: const EdgeInsets.symmetric(vertical: PlinkSpacing.s3),
              child: _EventRow(
                event: events[i],
                name: nameById[events[i].userId],
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _EventRow extends StatelessWidget {
  const _EventRow({required this.event, required this.name});
  final SessionRecentEvent event;
  final String? name;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: <Widget>[
        // Mono tabular timestamp — the columns line up like a log.
        SizedBox(
          width: 84,
          child: Text(
            pastFormatTimeWithSeconds(event.occurredAt.toLocal()),
            style: pastMonoSpec(PlinkColors.ink60, 12),
          ),
        ),
        const SizedBox(width: PlinkSpacing.s3),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              Row(
                children: <Widget>[
                  SizedBox(
                    width: 140,
                    child: Text(
                      name ?? '—',
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: PlinkColors.ink60,
                      ),
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  const SizedBox(width: PlinkSpacing.s2),
                  Expanded(
                    child: Text(
                      event.kind,
                      style: pastMonoLabel(PlinkColors.ink),
                    ),
                  ),
                ],
              ),
              if (event.payloadJson.isNotEmpty) ...<Widget>[
                const SizedBox(height: 2),
                SelectableText(
                  event.payloadJson,
                  style: pastMonoSpec(PlinkColors.muted, PlinkType.labelSm),
                ),
              ],
            ],
          ),
        ),
      ],
    );
  }
}
