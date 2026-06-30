import 'package:flutter/material.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../l10n/app_localizations.dart';

/// The admin-area sub-pages, each reached from the left vertical sub-nav. New
/// admin features (manage admins, schools) slot in here as further values — the
/// sub-nav is the extension point so they never clutter the primary app-bar.
enum AdminSection { bundles, admins, schools }

/// Chrome for the admin area (#299): a left vertical sub-navigation beside the
/// routed sub-page [child]. Sits *inside* the shared [AppShell] (which still
/// supplies the app-bar + "04 · ADMIN" eyebrow), so this widget only owns the
/// second-level nav rail and the content pane.
///
/// Purely presentational — it takes the active [section] and an [onNavigate]
/// callback so it can be widget-tested without a router. The router gates the
/// whole area on `isAdmin` and supplies the wiring.
class AdminShell extends StatelessWidget {
  const AdminShell({
    super.key,
    required this.section,
    required this.onNavigate,
    required this.child,
  });

  /// The active admin sub-page — drives the rail's active marker.
  final AdminSection section;

  /// Navigate to a sub-page location (e.g. `/admin/bundles`).
  final void Function(String location) onNavigate;

  /// The routed admin sub-page. Keeps its own `Scaffold`/body.
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return Row(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: <Widget>[
        SizedBox(
          width: 220,
          child: _SubNav(section: section, onNavigate: onNavigate),
        ),
        // A vertical hairline between rail and content — the system separates
        // with rules, never shadows.
        const SizedBox(
          width: PlinkBorders.width,
          child: ColoredBox(color: PlinkColors.hairline),
        ),
        Expanded(child: child),
      ],
    );
  }
}

/// The admin sub-pages, in rail order. Adding a new admin feature is a single
/// entry here plus its route.
const List<({AdminSection section, String location})> _items = [
  (section: AdminSection.bundles, location: '/admin/bundles'),
  (section: AdminSection.admins, location: '/admin/admins'),
  (section: AdminSection.schools, location: '/admin/schools'),
];

/// The localized display label for an admin sub-nav [section].
String _adminNavLabel(BuildContext c, AdminSection s) {
  final AppLocalizations l10n = AppLocalizations.of(c);
  return switch (s) {
    AdminSection.bundles => l10n.adminNavBundles,
    AdminSection.admins => l10n.adminNavAdmins,
    AdminSection.schools => l10n.adminNavSchools,
  };
}

class _SubNav extends StatelessWidget {
  const _SubNav({required this.section, required this.onNavigate});

  final AdminSection section;
  final void Function(String location) onNavigate;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: <Widget>[
        const SizedBox(height: PlinkSpacing.s2),
        for (final item in _items)
          _SubNavItem(
            section: item.section,
            label: _adminNavLabel(context, item.section),
            active: item.section == section,
            onTap: () => onNavigate(item.location),
          ),
      ],
    );
  }
}

class _SubNavItem extends StatelessWidget {
  const _SubNavItem({
    required this.section,
    required this.label,
    required this.active,
    required this.onTap,
  });

  final AdminSection section;
  final String label;
  final bool active;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final Color color = active ? PlinkColors.ink : PlinkColors.ink60;
    return InkWell(
      key: Key('admin-nav-${section.name}'),
      onTap: onTap,
      child: ColoredBox(
        // The selected row wears the same quiet paper-tint fill the bundle
        // catalogue uses — never a heavy highlight.
        color: active ? PlinkColors.paper2 : PlinkColors.paper,
        child: Row(
          children: <Widget>[
            // The magenta active tick — mirrors the app-bar nav indicator and
            // the catalogue row marker.
            SizedBox(
              width: 3,
              height: 44,
              child: active
                  ? const ColoredBox(color: PlinkColors.magenta)
                  : null,
            ),
            Expanded(
              child: ConstrainedBox(
                constraints: const BoxConstraints(minHeight: 44),
                child: Padding(
                  padding: const EdgeInsets.fromLTRB(
                    PlinkSpacing.s4 - 3,
                    PlinkSpacing.s3,
                    PlinkSpacing.s3,
                    PlinkSpacing.s3,
                  ),
                  child: Align(
                    alignment: Alignment.centerLeft,
                    child: Text(
                      label.toUpperCase(),
                      style: _navTextStyle(color, active: active),
                    ),
                  ),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// The mono nav-label treatment — mirrors the app-bar's destinations so the
/// second level reads as the same instrument, one rung down.
TextStyle _navTextStyle(Color color, {required bool active}) => TextStyle(
  fontFamily: PlinkType.monoFamily,
  package: PlinkType.fontPackage,
  fontFamilyFallback: PlinkType.monoFallback,
  fontSize: PlinkType.label,
  fontWeight: active ? FontWeight.w700 : FontWeight.w400,
  letterSpacing: PlinkType.tracking(
    PlinkType.labelTrackingTight,
    PlinkType.label,
  ),
  color: color,
);
