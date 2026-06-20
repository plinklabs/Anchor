import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../api/sessions_api.dart';
import '../widgets/api_error_text.dart';
import 'past_session_shared.dart';

/// History list (AD7, #172), redesigned to the paper treatment so it reads like
/// the rest of the dashboard (home #168, live session #169, bundles #170,
/// classes #171). The same instrument-panel feed treatment as the live session
/// (AD4, #169), but in a muted/archived register: where the live page leads with
/// the magenta LIVE spark, the archive is calm ink throughout — each past
/// session is a quiet hairline row marked by an "Ended" outline badge, never the
/// spark. The page never paints magenta: nothing here is live or constructive.
///
/// Purely presentational: the pagination logic (load-more, exhaustion, partial
/// failure) is untouched.
class HistoryPage extends StatefulWidget {
  const HistoryPage({super.key, required this.sessions});

  final SessionsApi sessions;

  @override
  State<HistoryPage> createState() => _HistoryPageState();
}

class _HistoryPageState extends State<HistoryPage> {
  static const int _pageSize = 50;

  final List<SessionHistoryEntry> _entries = [];
  bool _loading = false;
  bool _exhausted = false;
  ApiErrorMessage? _error;

  @override
  void initState() {
    super.initState();
    _loadMore();
  }

  Future<void> _loadMore() async {
    if (_loading || _exhausted) return;
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final page = await widget.sessions.history(
        limit: _pageSize,
        offset: _entries.length,
      );
      if (!mounted) return;
      setState(() {
        _entries.addAll(page);
        if (page.length < _pageSize) _exhausted = true;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = describeApiError(
            e,
            generic: 'Could not load past sessions. Please try again.',
          ));
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: PlinkColors.paper,
      body: _buildBody(),
    );
  }

  Widget _buildBody() {
    if (_entries.isEmpty && _loading) {
      return const Center(child: CircularProgressIndicator());
    }
    if (_entries.isEmpty && _error != null) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(PlinkSpacing.s6),
          child: ApiErrorText(_error!, textAlign: TextAlign.center),
        ),
      );
    }
    if (_entries.isEmpty) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(PlinkSpacing.s6),
          child: Text(
            'No past sessions yet. Sessions appear here after you end them.',
            style: pastMonoLabel(PlinkColors.muted),
            textAlign: TextAlign.center,
          ),
        ),
      );
    }

    return Align(
      alignment: Alignment.topCenter,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 760),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: <Widget>[
            const Padding(
              padding: EdgeInsets.fromLTRB(
                PlinkSpacing.s6,
                PlinkSpacing.s4,
                PlinkSpacing.s6,
                PlinkSpacing.s3,
              ),
              child: _ArchiveLabel(),
            ),
            const PastHairline(),
            Expanded(
              child: ListView.separated(
                padding: EdgeInsets.zero,
                // +1 row to host either the "load more" affordance or a trailing
                // error banner when pagination fails partway.
                itemCount: _entries.length + 1,
                separatorBuilder: (_, _) => const PastHairline(),
                itemBuilder: (context, index) {
                  if (index == _entries.length) return _buildTrailing();
                  final entry = _entries[index];
                  return _HistoryRow(
                    entry: entry,
                    onTap: () => context.go('/history/${entry.id}'),
                  );
                },
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildTrailing() {
    if (_error != null) {
      return Padding(
        padding: const EdgeInsets.all(PlinkSpacing.s5),
        child: Column(
          children: [
            ApiErrorText(_error!, textAlign: TextAlign.center),
            const SizedBox(height: PlinkSpacing.s3),
            // Calm ink — retrying a fetch is never the magenta spark.
            OutlinedButton(
              onPressed: _loadMore,
              child: const Text('Retry'),
            ),
          ],
        ),
      );
    }
    if (_exhausted) {
      return const SizedBox.shrink();
    }
    return Padding(
      padding: const EdgeInsets.all(PlinkSpacing.s5),
      child: Center(
        child: _loading
            ? const CircularProgressIndicator()
            // Calm ink — loading the next archive page is navigation, not a
            // constructive commit. The magenta spark never appears here.
            : OutlinedButton(
                onPressed: _loadMore,
                child: const Text('Load more'),
              ),
      ),
    );
  }
}

/// The quiet list header — a mono spec label, like the live feed's "Activity"
/// but in the archive's muted register.
class _ArchiveLabel extends StatelessWidget {
  const _ArchiveLabel();

  @override
  Widget build(BuildContext context) {
    return Text('Past sessions', style: pastMonoLabel(PlinkColors.ink60));
  }
}

/// One past-session row — a hairline instrument line in the archived register.
/// The class name reads first (ink); beneath it a mono tabular date/time spec.
/// An "Ended" outline badge marks the archived state (the muted counterpart to
/// the live page's magenta LIVE spark), and a chevron signals the row opens the
/// read-only review.
class _HistoryRow extends StatelessWidget {
  const _HistoryRow({required this.entry, required this.onTap});

  final SessionHistoryEntry entry;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final started = entry.startedAt.toLocal();
    final ended = entry.endedAt.toLocal();
    return InkWell(
      onTap: onTap,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(
          PlinkSpacing.s6,
          PlinkSpacing.s3,
          PlinkSpacing.s5,
          PlinkSpacing.s3,
        ),
        child: Row(
          children: <Widget>[
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: <Widget>[
                  Text(
                    entry.className.isEmpty ? 'Session' : entry.className,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: theme.textTheme.bodyLarge?.copyWith(
                      color: PlinkColors.ink,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    '${pastFormatDate(started)}  ·  '
                    '${pastFormatTime(started)} – ${pastFormatTime(ended)}',
                    style: pastMonoSpec(PlinkColors.ink60, PlinkType.textSm),
                  ),
                ],
              ),
            ),
            const SizedBox(width: PlinkSpacing.s3),
            // The archived marker — an outline badge, the muted counterpart to
            // the live page's magenta LIVE spark. Never the spark itself.
            const PlinkBadge('Ended', variant: BadgeVariant.outline),
            const SizedBox(width: PlinkSpacing.s2),
            const Icon(Icons.chevron_right, color: PlinkColors.muted),
          ],
        ),
      ),
    );
  }
}
