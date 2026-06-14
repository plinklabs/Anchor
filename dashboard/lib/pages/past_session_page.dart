import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../api/sessions_api.dart';

/// Read-only review of an ended session. Distinct from the live SessionPage:
/// no hub connection, no Approve/End buttons, no live event stream — just an
/// audit-trail layout (header → bundles → participants → activity summary →
/// approved exceptions → unapproved requests → event log).
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

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      // The unblock-requests endpoint already filters out anything that
      // received a grant, so on an ended session it returns exactly the
      // requests the teacher never acted on.
      final detailFuture = widget.sessions.getSession(widget.sessionId);
      final unapprovedFuture = widget.sessions.unblockRequests(widget.sessionId);
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
      setState(() => _error = 'Could not load past session: $e');
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: _buildBody(),
    );
  }

  Widget _buildBody() {
    if (_loading && _detail == null) {
      return const Center(child: CircularProgressIndicator());
    }
    if (_detail == null) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Text(
            _error ?? 'Session not available.',
            style: TextStyle(color: Theme.of(context).colorScheme.error),
            textAlign: TextAlign.center,
          ),
        ),
      );
    }
    final detail = _detail!;
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 880),
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            _Header(detail: detail),
            const SizedBox(height: 16),
            if (detail.bundles.isNotEmpty) ...[
              _SectionTitle('Bundles used'),
              _BundlesChips(bundles: detail.bundles),
              const SizedBox(height: 16),
            ],
            if (detail.participants.isNotEmpty) ...[
              _SectionTitle('Participants'),
              _ParticipantsTable(participants: detail.participants),
              const SizedBox(height: 16),
            ],
            if (detail.summaries.isNotEmpty) ...[
              _SectionTitle('Activity summary'),
              _SummaryTable(
                summaries: detail.summaries,
                participants: detail.participants,
              ),
              const SizedBox(height: 16),
            ],
            if (detail.grants.isNotEmpty) ...[
              _SectionTitle('Approved exceptions'),
              _GrantsList(grants: detail.grants),
              const SizedBox(height: 16),
            ],
            if (_unapprovedRequests.isNotEmpty) ...[
              _SectionTitle('Unapproved requests'),
              _UnapprovedList(requests: _unapprovedRequests),
              const SizedBox(height: 16),
            ],
            _SectionTitle('Event log'),
            _EventLog(events: detail.recentEvents, participants: detail.participants),
          ],
        ),
      ),
    );
  }
}

class _Header extends StatelessWidget {
  const _Header({required this.detail});
  final SessionDetail detail;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final started = detail.startedAt.toLocal();
    final ended = detail.endedAt!.toLocal();
    final duration = ended.difference(started);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          detail.className.isEmpty ? 'Session' : detail.className,
          style: theme.textTheme.headlineSmall,
        ),
        const SizedBox(height: 4),
        Text(
          '${_formatDate(started)}  ·  '
          '${_formatTime(started)} – ${_formatTime(ended)}  ·  '
          '${_formatDuration(duration)}',
          style: theme.textTheme.bodyMedium?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
        ),
      ],
    );
  }
}

class _SectionTitle extends StatelessWidget {
  const _SectionTitle(this.title);
  final String title;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.only(top: 8, bottom: 8),
      child: Text(title, style: theme.textTheme.titleMedium),
    );
  }
}

class _BundlesChips extends StatelessWidget {
  const _BundlesChips({required this.bundles});
  final List<SessionBundleInfo> bundles;

  @override
  Widget build(BuildContext context) {
    return Wrap(
      spacing: 8,
      runSpacing: 6,
      children: [
        for (final b in bundles)
          Chip(label: Text(b.name), visualDensity: VisualDensity.compact),
      ],
    );
  }
}

class _ParticipantsTable extends StatelessWidget {
  const _ParticipantsTable({required this.participants});
  final List<SessionParticipantInfo> participants;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        child: Column(
          children: [
            for (var i = 0; i < participants.length; i++) ...[
              if (i > 0) const Divider(height: 1),
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 8),
                child: Row(
                  children: [
                    Expanded(
                      flex: 3,
                      child: Text(
                        participants[i].displayName,
                        style: theme.textTheme.bodyMedium,
                      ),
                    ),
                    Expanded(
                      flex: 5,
                      child: Text(
                        _participantStatus(participants[i]),
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: theme.colorScheme.onSurfaceVariant,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }

  String _participantStatus(SessionParticipantInfo p) {
    if (p.declinedAt != null) {
      return 'declined at ${_formatTime(p.declinedAt!.toLocal())}';
    }
    if (p.joinedAt == null) {
      return 'never joined';
    }
    final joined = 'joined ${_formatTime(p.joinedAt!.toLocal())}';
    if (p.leftAt != null) {
      return '$joined  ·  left ${_formatTime(p.leftAt!.toLocal())}';
    }
    return joined;
  }
}

class _SummaryTable extends StatelessWidget {
  const _SummaryTable({required this.summaries, required this.participants});
  final List<SessionEventSummary> summaries;
  final List<SessionParticipantInfo> participants;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final nameById = {
      for (final p in participants) p.userId: p.displayName,
    };
    // Group per student so a row reads "Alice — 12 ForegroundChange, 3 BlockedUrl"
    // rather than one row per (student, kind). Easier to scan at a glance.
    final byUser = <String, List<SessionEventSummary>>{};
    for (final s in summaries) {
      byUser.putIfAbsent(s.userId, () => []).add(s);
    }
    return Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        child: Column(
          children: [
            for (var i = 0; i < byUser.entries.length; i++) ...[
              if (i > 0) const Divider(height: 1),
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 8),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(
                      flex: 3,
                      child: Text(
                        nameById[byUser.entries.elementAt(i).key] ??
                            byUser.entries.elementAt(i).key,
                        style: theme.textTheme.bodyMedium,
                      ),
                    ),
                    Expanded(
                      flex: 5,
                      child: Text(
                        byUser.entries.elementAt(i).value
                            .map((s) => '${s.count} ${s.kind}')
                            .join(', '),
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: theme.colorScheme.onSurfaceVariant,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _GrantsList extends StatelessWidget {
  const _GrantsList({required this.grants});
  final List<SessionUnblockGrantInfo> grants;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        child: Column(
          children: [
            for (var i = 0; i < grants.length; i++) ...[
              if (i > 0) const Divider(height: 1),
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 8),
                child: Row(
                  children: [
                    Expanded(
                      flex: 3,
                      child: Text(grants[i].host, style: theme.textTheme.bodyMedium),
                    ),
                    Expanded(
                      flex: 3,
                      child: Text(
                        grants[i].displayName,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: theme.colorScheme.onSurfaceVariant,
                        ),
                      ),
                    ),
                    Text(
                      _formatTime(grants[i].grantedAt.toLocal()),
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: theme.colorScheme.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _UnapprovedList extends StatelessWidget {
  const _UnapprovedList({required this.requests});
  final List<UnblockRequestSummary> requests;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        child: Column(
          children: [
            for (var i = 0; i < requests.length; i++) ...[
              if (i > 0) const Divider(height: 1),
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 8),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Expanded(
                          child: Text(
                            requests[i].host,
                            style: theme.textTheme.bodyMedium,
                          ),
                        ),
                        Text(
                          _formatTime(requests[i].latestRequestedAt.toLocal()),
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: theme.colorScheme.onSurfaceVariant,
                          ),
                        ),
                      ],
                    ),
                    const SizedBox(height: 2),
                    Text(
                      requests[i].requesters.map((r) => r.displayName).join(', '),
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: theme.colorScheme.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _EventLog extends StatelessWidget {
  const _EventLog({required this.events, required this.participants});
  final List<SessionRecentEvent> events;
  final List<SessionParticipantInfo> participants;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    if (events.isEmpty) {
      return Padding(
        padding: const EdgeInsets.symmetric(vertical: 8),
        child: Text(
          'No event detail retained for this session.',
          style: theme.textTheme.bodySmall?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
        ),
      );
    }
    final nameById = {
      for (final p in participants) p.userId: p.displayName,
    };
    return Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        child: Column(
          children: [
            for (var i = 0; i < events.length; i++) ...[
              if (i > 0) const Divider(height: 1),
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 8),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    SizedBox(
                      width: 84,
                      child: Text(
                        _formatTimeWithSeconds(events[i].occurredAt.toLocal()),
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: theme.colorScheme.onSurfaceVariant,
                          fontFeatures: const [FontFeature.tabularFigures()],
                        ),
                      ),
                    ),
                    SizedBox(
                      width: 140,
                      child: Text(
                        nameById[events[i].userId] ?? '—',
                        style: theme.textTheme.bodySmall,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                    SizedBox(
                      width: 160,
                      child: Text(events[i].kind, style: theme.textTheme.bodyMedium),
                    ),
                    Expanded(
                      child: SelectableText(
                        events[i].payloadJson,
                        style: theme.textTheme.bodySmall?.copyWith(
                          fontFamily: 'monospace',
                          color: theme.colorScheme.onSurfaceVariant,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

String _formatDate(DateTime dt) =>
    '${dt.year.toString().padLeft(4, '0')}-'
    '${dt.month.toString().padLeft(2, '0')}-'
    '${dt.day.toString().padLeft(2, '0')}';

String _formatTime(DateTime dt) =>
    '${dt.hour.toString().padLeft(2, '0')}:'
    '${dt.minute.toString().padLeft(2, '0')}';

String _formatTimeWithSeconds(DateTime dt) =>
    '${dt.hour.toString().padLeft(2, '0')}:'
    '${dt.minute.toString().padLeft(2, '0')}:'
    '${dt.second.toString().padLeft(2, '0')}';

String _formatDuration(Duration d) {
  final h = d.inHours;
  final m = d.inMinutes.remainder(60);
  if (h > 0) return '${h}h ${m}m';
  return '${d.inMinutes}m';
}
