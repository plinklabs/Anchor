import 'dart:async';

import 'package:flutter/material.dart';

import '../api/classes_api.dart';

/// Typeahead that searches the directory (MS Graph) and adds the selected
/// person to the class. The Entra OID travels with the selection but is never
/// shown — it's an internal identifier with no meaning to the teacher.
class AddStudentSearch extends StatefulWidget {
  const AddStudentSearch({
    super.key,
    required this.onSearch,
    required this.onAdd,
    this.disabled = false,
    this.disabledReason,
  });

  /// Performs a directory search. Returns the matching directory users.
  /// The roster page wires this so the class's school tag scopes the search.
  final Future<List<DirectoryUser>> Function(String query) onSearch;
  final Future<void> Function(String entraOid, String? displayName) onAdd;

  /// Disables the input — typically because the class doesn't have a school
  /// tag set yet, so a Graph search would be unscoped (#96).
  final bool disabled;
  final String? disabledReason;

  @override
  State<AddStudentSearch> createState() => _AddStudentSearchState();
}

class _AddStudentSearchState extends State<AddStudentSearch> {
  static const minChars = 2;
  static const debounce = Duration(milliseconds: 300);
  static const _fieldWidth = 420.0;

  final _controller = TextEditingController();
  Timer? _debounce;
  // Monotonic counter so a slow in-flight search can't overwrite the results
  // of a later keystroke (or a completed add).
  int _searchSeq = 0;
  List<DirectoryUser> _results = const [];
  bool _searching = false;
  bool _adding = false;
  bool _showResults = false;
  String? _error;

  @override
  void dispose() {
    _debounce?.cancel();
    _controller.dispose();
    super.dispose();
  }

  void _onChanged(String text) {
    _debounce?.cancel();
    final query = text.trim();
    if (query.length < minChars) {
      _searchSeq++;
      setState(() {
        _results = const [];
        _searching = false;
        _showResults = false;
        _error = null;
      });
      return;
    }
    setState(() {
      _searching = true;
      _showResults = true;
      _error = null;
    });
    _debounce = Timer(debounce, () => _runSearch(query));
  }

  Future<void> _runSearch(String query) async {
    final seq = ++_searchSeq;
    try {
      final results = await widget.onSearch(query);
      if (!mounted || seq != _searchSeq) return;
      setState(() {
        _results = results;
        _searching = false;
      });
    } catch (e) {
      if (!mounted || seq != _searchSeq) return;
      setState(() {
        _error = 'Directory search unavailable.';
        _results = const [];
        _searching = false;
      });
    }
  }

  Future<void> _select(DirectoryUser user) async {
    setState(() => _adding = true);
    try {
      await widget.onAdd(user.entraOid, user.displayName);
    } finally {
      if (mounted) {
        _searchSeq++;
        _controller.clear();
        setState(() {
          _results = const [];
          _showResults = false;
          _searching = false;
          _adding = false;
          _error = null;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        SizedBox(
          width: _fieldWidth,
          child: TextField(
            controller: _controller,
            enabled: !_adding && !widget.disabled,
            onChanged: _onChanged,
            decoration: InputDecoration(
              labelText: 'Add student',
              hintText: widget.disabled
                  ? (widget.disabledReason ?? 'Search disabled')
                  : 'Search by name',
              prefixIcon: const Icon(Icons.search),
              suffixIcon: _searching
                  ? const Padding(
                      padding: EdgeInsets.all(12),
                      child: SizedBox(
                        width: 16,
                        height: 16,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      ),
                    )
                  : null,
            ),
          ),
        ),
        if (_showResults) ...[
          const SizedBox(height: 4),
          SizedBox(width: _fieldWidth, child: _buildResults(context)),
        ],
      ],
    );
  }

  Widget _buildResults(BuildContext context) {
    if (_error != null) {
      return Padding(
        padding: const EdgeInsets.symmetric(vertical: 8, horizontal: 4),
        child: Text(
          _error!,
          style: TextStyle(color: Theme.of(context).colorScheme.error),
        ),
      );
    }
    if (_searching && _results.isEmpty) {
      return const SizedBox.shrink();
    }
    if (_results.isEmpty) {
      return const Padding(
        padding: EdgeInsets.symmetric(vertical: 8, horizontal: 4),
        child: Text('No matches.'),
      );
    }
    return Card(
      margin: EdgeInsets.zero,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxHeight: 280),
        child: ListView.builder(
          shrinkWrap: true,
          itemCount: _results.length,
          itemBuilder: (_, i) {
            final u = _results[i];
            return ListTile(
              dense: true,
              title: Text(u.displayName),
              subtitle: (u.upn == null || u.upn!.isEmpty) ? null : Text(u.upn!),
              onTap: _adding ? null : () => _select(u),
            );
          },
        ),
      ),
    );
  }
}
