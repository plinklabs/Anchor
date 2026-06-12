import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:go_router/go_router.dart';

import '../api/auth_token_store.dart';
import '../api/bundles_api.dart';
import '../api/sessions_api.dart';
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

  Set<String> get _selectedBundleIds =>
      {for (final b in _detail?.bundles ?? const <SessionBundleInfo>[]) b.id};

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
      setState(() => _bundleError = 'Failed to update bundles: $e');
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
      setState(() => _unblockError = 'Approve failed: $e');
    } finally {
      if (mounted) setState(() => _approving.remove(summary.host));
    }
  }

  Future<void> _copyJoinCode(String code) async {
    await Clipboard.setData(ClipboardData(text: code));
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text('Copied $code'), duration: const Duration(seconds: 2)),
    );
  }

  Future<void> _connect() async {
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
        // agent stopped/resumed reporting. Re-fetch the detail so the roster
        // reflects the server-computed per-student state.
        if (evt.kind == 'ParticipantStateChanged' ||
            evt.kind == 'HeartbeatLost' ||
            evt.kind == 'AgentReconnected') {
          _loadDetail();
        }
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Could not connect to live stream: $e');
    } finally {
      if (mounted) setState(() => _connecting = false);
    }
  }

  Future<void> _endSession() async {
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
      setState(() => _error = 'Failed to end session: $e');
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

    final choice = await showDialog<_ExitChoice>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Leave this session?'),
        content: const Text(
          'This session is still running and students stay enforced. End it for '
          'everyone, or leave it running and come back to it later from the home '
          'screen?',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, _ExitChoice.cancel),
            child: const Text('Cancel'),
          ),
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, _ExitChoice.leaveRunning),
            child: const Text('Leave running'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, _ExitChoice.endSession),
            child: const Text('End session'),
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
      setState(() => _error = 'Failed to end session: $e');
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

  String _titleText() {
    final started = _detail?.startedAt.toLocal();
    if (started == null) return 'Session';
    final y = started.year.toString().padLeft(4, '0');
    final mo = started.month.toString().padLeft(2, '0');
    final d = started.day.toString().padLeft(2, '0');
    final h = started.hour.toString().padLeft(2, '0');
    final mi = started.minute.toString().padLeft(2, '0');
    return 'Session $y-$mo-$d $h:$mi';
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: Text(_titleText()),
        leading: IconButton(
          icon: const Icon(Icons.arrow_back),
          onPressed: _confirmExit,
        ),
        actions: [
          if (!_ended)
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 8),
              child: TextButton.icon(
                onPressed: _ending ? null : _endSession,
                icon: _ending
                    ? const SizedBox(
                        width: 16,
                        height: 16,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Icon(Icons.stop),
                label: const Text('End session'),
              ),
            ),
        ],
      ),
      body: Column(
        children: [
          if (_connecting) const LinearProgressIndicator(),
          if (_error != null)
            Container(
              width: double.infinity,
              color: Theme.of(context).colorScheme.errorContainer,
              padding: const EdgeInsets.all(12),
              child: Text(_error!),
            ),
          if (_detail != null && _detail!.joinCode.isNotEmpty && !_ended)
            _JoinCodePanel(
              code: _detail!.joinCode,
              onCopy: () => _copyJoinCode(_detail!.joinCode),
            ),
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
          if (!_ended && (_detail?.participants.isNotEmpty ?? false))
            _RosterPanel(participants: _detail!.participants),
          if (_ended)
            Container(
              width: double.infinity,
              color: Theme.of(context).colorScheme.surfaceContainerHighest,
              padding: const EdgeInsets.all(12),
              child: const Text('Session ended — event stream stopped.'),
            ),
          if (_ended && (_detail?.summaries.isNotEmpty ?? false))
            _SessionSummaryPanel(summaries: _detail!.summaries),
          if (!_ended && _pendingRequests.isNotEmpty)
            _PendingRequestsPanel(
              requests: _pendingRequests,
              approving: _approving,
              error: _unblockError,
              onApprove: _approveHost,
            ),
          Expanded(
            child: _events.isEmpty
                ? const Center(child: Text('Waiting for events…'))
                : ListView.separated(
                    padding: const EdgeInsets.all(12),
                    itemCount: _events.length,
                    separatorBuilder: (_, _) => const Divider(height: 8),
                    itemBuilder: (context, i) {
                      final evt = _events[i];
                      return ListTile(
                        dense: true,
                        title: Text(evt.kind),
                        subtitle: Text(evt.payload.toString()),
                        trailing: Text(
                          '${evt.at.hour.toString().padLeft(2, '0')}:'
                          '${evt.at.minute.toString().padLeft(2, '0')}:'
                          '${evt.at.second.toString().padLeft(2, '0')}',
                        ),
                      );
                    },
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
  const _RosterStateStyle(this.label, this.icon, this.sortRank);
  final String label;
  final IconData icon;

  /// Lower sorts higher. Attention-needing states cluster at the top; the bulk
  /// of normally-joined students sits below.
  final int sortRank;

  static _RosterStateStyle of(ParticipantLiveState state) {
    switch (state) {
      case ParticipantLiveState.heartbeatStale:
        return const _RosterStateStyle('Agent stopped reporting', Icons.sensors_off, 0);
      case ParticipantLiveState.left:
        return const _RosterStateStyle('Left', Icons.logout, 1);
      case ParticipantLiveState.declined:
        return const _RosterStateStyle('Declined', Icons.cancel_outlined, 2);
      case ParticipantLiveState.neverJoined:
        return const _RosterStateStyle('Not joined', Icons.radio_button_unchecked, 3);
      case ParticipantLiveState.joined:
        return const _RosterStateStyle('In session', Icons.check_circle, 4);
      case ParticipantLiveState.unknown:
        return const _RosterStateStyle('Unknown', Icons.help_outline, 5);
    }
  }

  Color color(ColorScheme scheme) {
    switch (icon) {
      case Icons.sensors_off:
        return scheme.error;
      case Icons.check_circle:
        return Colors.green;
      case Icons.cancel_outlined:
        return Colors.orange.shade800;
      default:
        return scheme.onSurfaceVariant;
    }
  }
}

class _RosterPanel extends StatelessWidget {
  const _RosterPanel({required this.participants});

  final List<SessionParticipantInfo> participants;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    // Sort by state (attention-first) then name — the acceptance criterion.
    final sorted = [...participants]..sort((a, b) {
        final byState = _RosterStateStyle.of(a.state).sortRank
            .compareTo(_RosterStateStyle.of(b.state).sortRank);
        if (byState != 0) return byState;
        return a.displayName.toLowerCase().compareTo(b.displayName.toLowerCase());
      });

    final joinedCount = participants
        .where((p) => p.state == ParticipantLiveState.joined)
        .length;

    return Container(
      width: double.infinity,
      color: theme.colorScheme.surfaceContainerHighest,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(Icons.groups, size: 18, color: theme.colorScheme.onSurfaceVariant),
              const SizedBox(width: 8),
              Text(
                'Students ($joinedCount/${participants.length} in session)',
                style: theme.textTheme.titleSmall?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          // Bounded so a 30-student class doesn't push the event log off-screen.
          ConstrainedBox(
            constraints: const BoxConstraints(maxHeight: 240),
            child: ListView.builder(
              shrinkWrap: true,
              itemCount: sorted.length,
              itemBuilder: (context, i) =>
                  _RosterRow(participant: sorted[i]),
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
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(
        children: [
          Icon(style.icon, size: 18, color: color),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              participant.displayName,
              style: theme.textTheme.bodyMedium,
              overflow: TextOverflow.ellipsis,
            ),
          ),
          Text(
            style.label,
            style: theme.textTheme.bodySmall?.copyWith(color: color),
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
  });

  final List<UnblockRequestSummary> requests;
  final Set<String> approving;
  final String? error;
  final void Function(UnblockRequestSummary) onApprove;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Container(
      width: double.infinity,
      color: theme.colorScheme.surfaceContainerHighest,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(
                Icons.pending_actions,
                size: 18,
                color: theme.colorScheme.onSurfaceVariant,
              ),
              const SizedBox(width: 8),
              Text(
                'Pending requests',
                style: theme.textTheme.titleSmall?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
              ),
            ],
          ),
          if (error != null) ...[
            const SizedBox(height: 6),
            Text(error!, style: TextStyle(color: theme.colorScheme.error)),
          ],
          const SizedBox(height: 8),
          for (final r in requests)
            Padding(
              padding: const EdgeInsets.only(bottom: 6),
              child: _PendingRequestRow(
                summary: r,
                isApproving: approving.contains(r.host),
                onApprove: () => onApprove(r),
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
  });

  final UnblockRequestSummary summary;
  final bool isApproving;
  final VoidCallback onApprove;

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
              Text(
                summary.host,
                style: theme.textTheme.bodyLarge?.copyWith(fontWeight: FontWeight.w500),
              ),
              Text(
                summary.count == 1
                    ? '1 student — $names'
                    : '${summary.count} students — $names',
                style: theme.textTheme.bodySmall?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
              ),
            ],
          ),
        ),
        FilledButton.tonalIcon(
          onPressed: isApproving ? null : onApprove,
          icon: isApproving
              ? const SizedBox(
                  width: 14,
                  height: 14,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : const Icon(Icons.check),
          label: const Text('Approve'),
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
    return Container(
      width: double.infinity,
      color: theme.colorScheme.surfaceContainerHigh,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Session summary',
            style: theme.textTheme.titleSmall?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 6),
          Text(
            lines.join(' · '),
            style: theme.textTheme.bodyMedium,
          ),
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
    return Container(
      width: double.infinity,
      color: theme.colorScheme.surfaceContainerHighest,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(
                Icons.collections_bookmark,
                size: 18,
                color: theme.colorScheme.onSurfaceVariant,
              ),
              const SizedBox(width: 8),
              Text(
                'Allowed bundles',
                style: theme.textTheme.titleSmall?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
              ),
              if (busy) ...[
                const SizedBox(width: 12),
                const SizedBox(
                  width: 14,
                  height: 14,
                  child: CircularProgressIndicator(strokeWidth: 2),
                ),
              ],
            ],
          ),
          if (error != null) ...[
            const SizedBox(height: 6),
            Text(error!, style: TextStyle(color: theme.colorScheme.error)),
          ],
          const SizedBox(height: 8),
          if (available.isEmpty)
            Text('No bundles available yet.', style: theme.textTheme.bodySmall)
          else
            Wrap(
              spacing: 8,
              runSpacing: 4,
              children: [
                for (final b in available)
                  FilterChip(
                    label: Text(b.name),
                    selected: selectedIds.contains(b.id),
                    onSelected:
                        busy ? null : (selected) => onToggle(b.id, selected),
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
    final theme = Theme.of(context);
    return Container(
      width: double.infinity,
      color: theme.colorScheme.surfaceContainerHighest,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Join code',
                  style: theme.textTheme.labelMedium?.copyWith(
                    color: theme.colorScheme.onSurfaceVariant,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  // Big enough to be legible across a classroom — fallback
                  // path for any student who didn't get the roster-based
                  // push (substitute, transferred class, etc).
                  code,
                  style: theme.textTheme.displaySmall?.copyWith(
                    fontFeatures: const [FontFeature.tabularFigures()],
                    fontWeight: FontWeight.w600,
                    letterSpacing: 8,
                  ),
                ),
              ],
            ),
          ),
          IconButton(
            tooltip: 'Copy code',
            icon: const Icon(Icons.copy),
            onPressed: onCopy,
          ),
        ],
      ),
    );
  }
}
