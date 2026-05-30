import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:go_router/go_router.dart';

import '../api/auth_token_store.dart';
import '../api/sessions_api.dart';
import '../realtime/session_hub_client.dart';

class SessionPage extends StatefulWidget {
  const SessionPage({
    super.key,
    required this.sessionId,
    required this.tokens,
    required this.sessions,
    required this.apiBaseUrl,
  });

  final String sessionId;
  final AuthTokenStore tokens;
  final SessionsApi sessions;
  final Uri apiBaseUrl;

  @override
  State<SessionPage> createState() => _SessionPageState();
}

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

  @override
  void initState() {
    super.initState();
    _hub = SessionHubClient(
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
    await Future.wait([_connect(), _loadPendingRequests()]);
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
          onPressed: () => context.go('/'),
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
