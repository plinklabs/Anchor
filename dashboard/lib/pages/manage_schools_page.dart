import 'package:flutter/material.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../api/schools_api.dart';
import '../l10n/app_localizations.dart';

/// Admin-only "Schools" sub-tab (#301), in the paper treatment.
///
/// One readable column listing every school (Entra company) with a per-school
/// active toggle. Only active schools reach teachers in the Classes school
/// selector, so this is where an admin curates away the irrelevant ones. New
/// schools default to active, so the list mirrors what teachers see until an
/// admin deactivates a row. The route already gates the area on `isAdmin`.
class ManageSchoolsPage extends StatefulWidget {
  const ManageSchoolsPage({super.key, required this.schools});

  final SchoolsApi schools;

  @override
  State<ManageSchoolsPage> createState() => _ManageSchoolsPageState();
}

class _ManageSchoolsPageState extends State<ManageSchoolsPage> {
  bool _loading = false;
  String? _error;
  List<School>? _schools;

  // Names with an in-flight toggle, so their row can show progress and stay
  // un-tappable without freezing the rest of the page.
  final Set<String> _busy = <String>{};

  @override
  void initState() {
    super.initState();
    // Defer the first load until after the first frame, so inherited widgets
    // (Localizations) are available when _loadSchools reads them.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _loadSchools();
    });
  }

  Future<void> _loadSchools() async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final list = await widget.schools.listSchools();
      if (!mounted) return;
      setState(() => _schools = list);
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = l10n.schoolsLoadError('$e'));
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _setActive(School school, bool isActive) async {
    final l10n = AppLocalizations.of(context);
    setState(() {
      _busy.add(school.name);
      _error = null;
    });
    try {
      final updated = await widget.schools.setActive(school.name, isActive);
      if (!mounted) return;
      // Swap the one row in place — no full reload, so the list doesn't jump.
      setState(() {
        _schools = [
          for (final s in _schools ?? const <School>[])
            if (s.name == updated.name) updated else s,
        ];
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = l10n.schoolsUpdateError(school.name, '$e'));
    } finally {
      if (mounted) setState(() => _busy.remove(school.name));
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: PlinkColors.paper,
      body: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(
          PlinkSpacing.s6,
          PlinkSpacing.s5,
          PlinkSpacing.s6,
          PlinkSpacing.s8,
        ),
        child: Align(
          alignment: Alignment.centerLeft,
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 640),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(
                  AppLocalizations.of(context).schoolsHeader,
                  style: _monoLabel(PlinkColors.ink60),
                ),
                const SizedBox(height: PlinkSpacing.s2),
                Text(
                  AppLocalizations.of(context).schoolsIntro,
                  style: Theme.of(
                    context,
                  ).textTheme.bodySmall?.copyWith(color: PlinkColors.ink60),
                ),
                const SizedBox(height: PlinkSpacing.s6),
                if (_error != null) ...[
                  Text(
                    _error!,
                    key: const Key('manage-schools-error'),
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: Theme.of(context).colorScheme.error,
                    ),
                  ),
                  const SizedBox(height: PlinkSpacing.s4),
                ],
                _buildSchoolList(),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildSchoolList() {
    final schools = _schools;
    if (_loading && schools == null) {
      return const Padding(
        padding: EdgeInsets.symmetric(vertical: PlinkSpacing.s6),
        child: Center(child: CircularProgressIndicator()),
      );
    }
    if (schools == null || schools.isEmpty) {
      return Text(
        AppLocalizations.of(context).schoolsEmpty,
        key: const Key('manage-schools-empty'),
        style: _monoLabel(PlinkColors.muted),
      );
    }
    return Container(
      decoration: BoxDecoration(
        border: Border.all(
          color: PlinkColors.hairline,
          width: PlinkBorders.width,
        ),
        borderRadius: BorderRadius.circular(PlinkRadius.base),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          for (var i = 0; i < schools.length; i++) ...[
            if (i > 0) const _Hairline(),
            _SchoolRow(
              school: schools[i],
              busy: _busy.contains(schools[i].name),
              onChanged: (value) => _setActive(schools[i], value),
            ),
          ],
        ],
      ),
    );
  }
}

/// One school row — the company name, with a trailing active toggle and a quiet
/// Active/Inactive state label.
class _SchoolRow extends StatelessWidget {
  const _SchoolRow({
    required this.school,
    required this.busy,
    required this.onChanged,
  });

  final School school;
  final bool busy;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        PlinkSpacing.s4,
        PlinkSpacing.s3,
        PlinkSpacing.s3,
        PlinkSpacing.s3,
      ),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  school.name,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(
                    context,
                  ).textTheme.bodyLarge?.copyWith(color: PlinkColors.ink),
                ),
                Text(
                  school.isActive
                      ? AppLocalizations.of(context).statusActive
                      : AppLocalizations.of(context).statusInactive,
                  style: _monoSpec(
                    school.isActive ? PlinkColors.ink60 : PlinkColors.muted,
                    PlinkType.textSm,
                  ),
                ),
              ],
            ),
          ),
          if (busy)
            const Padding(
              padding: EdgeInsets.symmetric(horizontal: PlinkSpacing.s3),
              child: SizedBox(
                width: 16,
                height: 16,
                child: CircularProgressIndicator(strokeWidth: 2),
              ),
            )
          else
            Switch(
              key: Key('school-toggle-${school.name}'),
              value: school.isActive,
              onChanged: onChanged,
            ),
        ],
      ),
    );
  }
}

/// A full-width 1px instrument rule — the system separates with hairlines,
/// never shadows. Mirrors the manage-admins list.
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

/// A space-mono label style for the quiet section header and microcopy.
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

/// Tabular-figure mono for the quiet Active/Inactive state label.
TextStyle _monoSpec(Color color, double size) => TextStyle(
  fontFamily: PlinkType.monoFamily,
  package: PlinkType.fontPackage,
  fontFamilyFallback: PlinkType.monoFallback,
  fontSize: size,
  color: color,
  height: 1.4,
  fontFeatures: const <FontFeature>[FontFeature.tabularFigures()],
);
