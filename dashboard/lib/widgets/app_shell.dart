import 'package:flutter/material.dart';
import 'package:plink_design_system/plink_design_system.dart';

import 'anchor_mark.dart';

/// The dashboard surfaces that share the chrome. The first four are the
/// standing nav destinations; [session]/[pastSession] are detail pages reached
/// from them (no nav slot of their own). [admin] is the admin-only area whose
/// own sub-pages (Bundles, …) live behind a left vertical sub-nav.
enum AppSection { home, classes, history, admin, session, pastSession }

/// A top-level nav destination in the app-bar.
class _Destination {
  const _Destination(this.section, this.label, this.location, this.number);

  final AppSection section;
  final String label;
  final String location;
  final String number;
}

const List<_Destination> _destinations = <_Destination>[
  _Destination(AppSection.home, 'Home', '/', '01'),
  _Destination(AppSection.classes, 'Classes', '/classes', '02'),
  _Destination(AppSection.history, 'History', '/history', '03'),
  _Destination(AppSection.admin, 'Admin', '/admin', '04'),
];

/// Horizontal page gutter — keeps the lockup, nav, and page eyebrow on one
/// flush-left margin (the editorial column).
const double _gutter = PlinkSpacing.s6; // 32

/// Shared chrome for every dashboard page (AD1, #166): the indigo identity
/// rule, the app-bar (anchor lockup · horizontal nav · account + sign-out),
/// a hairline, and a flush-left mono eyebrow with the section number — wrapped
/// around the routed page [child].
///
/// Purely presentational: it takes plain data and callbacks so it can be
/// widget-tested without a router or network. The router supplies [isAdmin]
/// (from `/me`), [accountName] (from the token store), and the [onSignOut] /
/// [onNavigate] wiring.
class AppShell extends StatelessWidget {
  const AppShell({
    super.key,
    required this.section,
    required this.onNavigate,
    required this.child,
    this.isAdmin = false,
    this.accountName,
    this.onSignOut,
  });

  final AppSection section;

  /// Navigate to a destination location (e.g. `/classes`).
  final void Function(String location) onNavigate;

  /// The routed page. Keeps its own `Scaffold`/body; the shell stacks chrome
  /// above it.
  final Widget child;

  /// Whether the signed-in teacher is an admin — gates the Admin nav slot.
  final bool isAdmin;

  /// Display name shown on the right of the bar; hidden when null.
  final String? accountName;

  /// Sign-out handler; the button is hidden when null.
  final VoidCallback? onSignOut;

  @override
  Widget build(BuildContext context) {
    final ({String number, String label}) header = _headerFor(section);
    final String eyebrow = header.number.isEmpty
        ? header.label
        : '${header.number} · ${header.label}';

    return Scaffold(
      backgroundColor: PlinkColors.paper,
      body: SafeArea(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: <Widget>[
            // The one per-product identity element — indigo, decorative.
            const ExcludeSemantics(child: PlinkIdentityRule()),
            _TopBar(
              section: section,
              isAdmin: isAdmin,
              accountName: accountName,
              onNavigate: onNavigate,
              onSignOut: onSignOut,
            ),
            // Hairline, never a shadow.
            const SizedBox(
              height: PlinkBorders.width,
              child: ColoredBox(color: PlinkColors.hairline),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(
                _gutter,
                PlinkSpacing.s5,
                _gutter,
                PlinkSpacing.s4,
              ),
              child: Align(
                alignment: Alignment.centerLeft,
                child: Eyebrow(eyebrow),
              ),
            ),
            Expanded(child: child),
          ],
        ),
      ),
    );
  }

  static ({String number, String label}) _headerFor(AppSection section) {
    return switch (section) {
      AppSection.home => (number: '01', label: 'Home'),
      AppSection.classes => (number: '02', label: 'Classes'),
      AppSection.history => (number: '03', label: 'Past sessions'),
      AppSection.admin => (number: '04', label: 'Admin'),
      AppSection.session => (number: '', label: 'Live session'),
      AppSection.pastSession => (number: '', label: 'Past session'),
    };
  }
}

class _TopBar extends StatelessWidget {
  const _TopBar({
    required this.section,
    required this.isAdmin,
    required this.accountName,
    required this.onNavigate,
    required this.onSignOut,
  });

  final AppSection section;
  final bool isAdmin;
  final String? accountName;
  final void Function(String location) onNavigate;
  final VoidCallback? onSignOut;

  @override
  Widget build(BuildContext context) {
    final List<_Destination> items = _destinations
        .where((_Destination d) => d.section != AppSection.admin || isAdmin)
        .toList(growable: false);

    return Padding(
      padding: const EdgeInsets.symmetric(
        horizontal: _gutter,
        vertical: PlinkSpacing.s2,
      ),
      child: Row(
        children: <Widget>[
          // Lockup doubles as the "home" affordance.
          InkWell(
            key: const Key('lockup-home'),
            onTap: () => onNavigate('/'),
            child: const Padding(
              padding: EdgeInsets.symmetric(vertical: PlinkSpacing.s2),
              child: AnchorLockup(height: 26),
            ),
          ),
          const SizedBox(width: PlinkSpacing.s7), // 48
          // Nav scrolls rather than overflowing if the window is ever too
          // narrow to lay every destination out (a11y: reflow, never clip).
          Expanded(
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: Row(
                children: <Widget>[
                  for (final _Destination d in items)
                    _NavItem(
                      dest: d,
                      active: d.section == section,
                      onTap: () => onNavigate(d.location),
                    ),
                ],
              ),
            ),
          ),
          const SizedBox(width: PlinkSpacing.s4),
          if (accountName != null)
            Padding(
              padding: const EdgeInsets.only(right: PlinkSpacing.s3),
              child: Text(
                accountName!,
                style: const TextStyle(
                  fontFamily: PlinkType.bodyFamily,
                  package: PlinkType.fontPackage,
                  fontFamilyFallback: PlinkType.bodyFallback,
                  fontSize: PlinkType.textSm,
                  color: PlinkColors.ink60,
                ),
              ),
            ),
          if (onSignOut != null)
            TextButton(
              key: const Key('sign-out'),
              onPressed: onSignOut,
              style: TextButton.styleFrom(
                minimumSize: const Size(0, 44),
                foregroundColor: PlinkColors.ink,
              ),
              child: Text(
                'SIGN OUT',
                style: _navTextStyle(PlinkColors.ink, active: false),
              ),
            ),
        ],
      ),
    );
  }
}

class _NavItem extends StatelessWidget {
  const _NavItem({
    required this.dest,
    required this.active,
    required this.onTap,
  });

  final _Destination dest;
  final bool active;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final Color color = active ? PlinkColors.ink : PlinkColors.ink60;
    return InkWell(
      key: Key('nav-${dest.section.name}'),
      onTap: onTap,
      child: ConstrainedBox(
        constraints: const BoxConstraints(minHeight: 44),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: PlinkSpacing.s3),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            mainAxisAlignment: MainAxisAlignment.center,
            children: <Widget>[
              Text(
                dest.label.toUpperCase(),
                style: _navTextStyle(color, active: active),
              ),
              const SizedBox(height: PlinkSpacing.s1 + 2), // 6
              // Active marker — the magenta spark doubles as the indicator.
              SizedBox(
                height: 2,
                width: 20,
                child: active
                    ? const ColoredBox(color: PlinkColors.magenta)
                    : null,
              ),
            ],
          ),
        ),
      ),
    );
  }
}

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
