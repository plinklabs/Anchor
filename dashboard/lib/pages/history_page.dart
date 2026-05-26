import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../api/sessions_api.dart';

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
  String? _error;

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
      setState(() => _error = 'Could not load past sessions: $e');
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Past sessions'),
        leading: IconButton(
          icon: const Icon(Icons.arrow_back),
          onPressed: () => context.go('/'),
        ),
      ),
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
          padding: const EdgeInsets.all(24),
          child: Text(
            _error!,
            style: TextStyle(color: Theme.of(context).colorScheme.error),
            textAlign: TextAlign.center,
          ),
        ),
      );
    }
    if (_entries.isEmpty) {
      return const Center(
        child: Padding(
          padding: EdgeInsets.all(24),
          child: Text(
            'No past sessions yet. Sessions appear here after you end them.',
            textAlign: TextAlign.center,
          ),
        ),
      );
    }

    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 720),
        child: ListView.separated(
          padding: const EdgeInsets.all(16),
          // +1 row to host either the "load more" affordance or a trailing
          // error banner when pagination fails partway.
          itemCount: _entries.length + 1,
          separatorBuilder: (_, _) => const Divider(height: 1),
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
    );
  }

  Widget _buildTrailing() {
    if (_error != null) {
      return Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          children: [
            Text(
              _error!,
              style: TextStyle(color: Theme.of(context).colorScheme.error),
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 8),
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
      padding: const EdgeInsets.all(16),
      child: Center(
        child: _loading
            ? const CircularProgressIndicator()
            : OutlinedButton(
                onPressed: _loadMore,
                child: const Text('Load more'),
              ),
      ),
    );
  }
}

class _HistoryRow extends StatelessWidget {
  const _HistoryRow({required this.entry, required this.onTap});

  final SessionHistoryEntry entry;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final started = entry.startedAt.toLocal();
    final ended = entry.endedAt.toLocal();
    return ListTile(
      title: Text(entry.className),
      subtitle: Text(
        '${_formatDate(started)}  ·  '
        '${_formatTime(started)} – ${_formatTime(ended)}',
      ),
      trailing: const Icon(Icons.chevron_right),
      onTap: onTap,
    );
  }

  static String _formatDate(DateTime dt) =>
      '${dt.year.toString().padLeft(4, '0')}-'
      '${dt.month.toString().padLeft(2, '0')}-'
      '${dt.day.toString().padLeft(2, '0')}';

  static String _formatTime(DateTime dt) =>
      '${dt.hour.toString().padLeft(2, '0')}:'
      '${dt.minute.toString().padLeft(2, '0')}';
}
