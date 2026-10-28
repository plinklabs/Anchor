import 'package:flutter/material.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../l10n/app_localizations.dart';
import 'alert_popups.dart';
import 'live_alert_cues.dart';
import 'live_alert_services.dart';
import 'live_alerts.dart';

/// Opens the Notifications dialog (#454): sound on or off, system pop-ups
/// (#455), which kinds of live-session event alert, and a Test button.
/// Reached from the bell in the app bar, so it can be set up before a lesson,
/// and from the live page's header. The choices are kept in this browser.
Future<void> showLiveAlertSettings(
  BuildContext context,
  LiveAlertServices services,
) {
  return showDialog<void>(
    context: context,
    builder: (dialogContext) => LiveAlertSettingsDialog(services: services),
  );
}

/// The Notifications dialog's content; see [showLiveAlertSettings].
///
/// Pop-ups need the browser's permission, which only the teacher's click on
/// the Pop-ups switch asks for. The teacher can change it in the browser's
/// site settings at any time, so the dialog reads it again when it opens and
/// whenever they come back to the tab.
class LiveAlertSettingsDialog extends StatefulWidget {
  const LiveAlertSettingsDialog({super.key, required this.services});

  final LiveAlertServices services;

  @override
  State<LiveAlertSettingsDialog> createState() =>
      _LiveAlertSettingsDialogState();
}

class _LiveAlertSettingsDialogState extends State<LiveAlertSettingsDialog> {
  late final AppLifecycleListener _lifecycle;

  /// The browser is asking the teacher for permission.
  bool _asking = false;

  LiveAlertServices get _services => widget.services;

  @override
  void initState() {
    super.initState();
    _services.popups.refresh();
    // Back from the browser's site settings (focus) or from another tab.
    _lifecycle = AppLifecycleListener(onResume: _services.popups.refresh);
  }

  @override
  void dispose() {
    _lifecycle.dispose();
    super.dispose();
  }

  static String _categoryLabel(AppLocalizations l10n, LiveAlertCategory c) {
    return switch (c) {
      LiveAlertCategory.accessRequest => l10n.alertsAccessRequest,
      LiveAlertCategory.studentLeft => l10n.alertsStudentLeft,
      LiveAlertCategory.agentLost => l10n.alertsAgentLost,
      LiveAlertCategory.tamper => l10n.alertsTamper,
    };
  }

  static String _permissionLine(AppLocalizations l10n, PopupPermission p) {
    return switch (p) {
      PopupPermission.notAsked => l10n.alertsPopupsNotAsked,
      PopupPermission.allowed => l10n.alertsPopupsAllowed,
      PopupPermission.blocked => l10n.alertsPopupsBlocked,
      PopupPermission.unsupported => l10n.alertsPopupsUnsupported,
    };
  }

  /// Whether the Pop-ups switch shows on: the teacher turned them on, and
  /// the browser answered (or is asking). A browser that forgot its answer
  /// (it lets a permission lapse on a site that isn't used for a while)
  /// shows it off, so turning it on asks again.
  bool _popupsOn(PopupPermission permission) {
    if (!_services.settings.popups) return false;
    return switch (permission) {
      PopupPermission.allowed || PopupPermission.blocked => true,
      PopupPermission.notAsked => _asking,
      PopupPermission.unsupported => false,
    };
  }

  /// The teacher flipped the Pop-ups switch. Turning it on is the only
  /// moment the dashboard asks the browser for permission, from this click.
  Future<void> _setPopups(bool on) async {
    final settings = _services.settings;
    final popups = _services.popups..refresh();
    settings.popups = on;
    if (!on || popups.permission.value != PopupPermission.notAsked) return;
    setState(() => _asking = true);
    final PopupPermission answer = await popups.request();
    if (mounted) setState(() => _asking = false);
    // They closed the prompt without answering: still off, and turning them
    // on asks again.
    if (answer == PopupPermission.notAsked) settings.popups = false;
  }

  /// Plays the sound whatever the switches say: it's how the teacher checks
  /// the speakers before a lesson. The click also lets the browser play
  /// sound from now on. While pop-ups are on, it shows a sample one too.
  void _test(AppLocalizations l10n) {
    _services.sound
      ..unlock()
      ..play();
    if (!_services.settings.popups) return;
    final popups = _services.popups;
    popups.show(
      AlertPopup(
        tag: alertPopupTestTag,
        title: l10n.alertsPopupTestTitle,
        body: l10n.alertsPopupTestBody,
        onClick: () => popups
          ..bringPageToFront()
          ..close(alertPopupTestTag),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final AppLocalizations l10n = AppLocalizations.of(context);
    final settings = _services.settings;
    final TextTheme text = Theme.of(context).textTheme;
    final TextStyle? hint = text.bodySmall?.copyWith(color: PlinkColors.ink60);
    return AlertDialog(
      title: Text(l10n.alertsTitle),
      content: SizedBox(
        width: 460,
        child: SingleChildScrollView(
          child: ListenableBuilder(
            listenable: Listenable.merge(<Listenable>[
              settings,
              _services.popups.permission,
            ]),
            builder: (context, _) {
              final PopupPermission permission =
                  _services.popups.permission.value;
              final bool popupsOn = _popupsOn(permission);
              return Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: <Widget>[
                  Text(l10n.alertsIntro, style: hint),
                  const SizedBox(height: PlinkSpacing.s3),
                  SwitchListTile(
                    key: const Key('alerts-sound'),
                    contentPadding: EdgeInsets.zero,
                    title: Text(l10n.alertsSound),
                    value: settings.sound,
                    onChanged: (on) => settings.sound = on,
                  ),
                  SwitchListTile(
                    key: const Key('alerts-popups'),
                    contentPadding: EdgeInsets.zero,
                    title: Text(l10n.alertsPopups),
                    subtitle: Text(
                      _permissionLine(l10n, permission),
                      key: const Key('alerts-popups-permission'),
                    ),
                    value: popupsOn,
                    onChanged: permission == PopupPermission.unsupported
                        ? null
                        : _setPopups,
                  ),
                  if (popupsOn && permission == PopupPermission.blocked)
                    _HowToAllow(style: hint),
                  if (popupsOn)
                    Padding(
                      padding: const EdgeInsets.only(bottom: PlinkSpacing.s2),
                      child: Text(l10n.alertsPopupsWindows, style: hint),
                    ),
                  SwitchListTile(
                    key: const Key('alerts-popup-names'),
                    contentPadding: EdgeInsets.zero,
                    title: Text(l10n.alertsPopupNames),
                    subtitle: Text(l10n.alertsPopupNamesHint),
                    value: settings.popupNames,
                    onChanged: popupsOn
                        ? (on) => settings.popupNames = on
                        : null,
                  ),
                  const SizedBox(height: PlinkSpacing.s3),
                  Text(
                    l10n.alertsCategoriesLabel,
                    style: const TextStyle(
                      fontFamily: PlinkType.monoFamily,
                      package: PlinkType.fontPackage,
                      fontFamilyFallback: PlinkType.monoFallback,
                      fontSize: PlinkType.label,
                      color: PlinkColors.ink60,
                    ),
                  ),
                  for (final LiveAlertCategory category
                      in LiveAlertCategory.values)
                    SwitchListTile(
                      key: Key('alerts-${category.name}'),
                      contentPadding: EdgeInsets.zero,
                      title: Text(_categoryLabel(l10n, category)),
                      value: settings.isOn(category),
                      onChanged: (on) => settings.setOn(category, on),
                    ),
                  const SizedBox(height: PlinkSpacing.s3),
                  Text(l10n.alertsKeptInBrowser, style: hint),
                ],
              );
            },
          ),
        ),
      ),
      actions: <Widget>[
        OutlinedButton.icon(
          key: const Key('alerts-test'),
          onPressed: () => _test(l10n),
          icon: const Icon(Icons.volume_up_outlined, size: 18),
          label: Text(l10n.alertsTest),
        ),
        TextButton(
          onPressed: () => Navigator.pop(context),
          child: Text(l10n.actionClose),
        ),
      ],
    );
  }
}

/// The steps to allow pop-ups again once the browser blocks them (#455):
/// only the teacher can, in the browser's site settings.
class _HowToAllow extends StatelessWidget {
  const _HowToAllow({required this.style});

  final TextStyle? style;

  @override
  Widget build(BuildContext context) {
    final AppLocalizations l10n = AppLocalizations.of(context);
    return Padding(
      key: const Key('alerts-popups-how-to-allow'),
      padding: const EdgeInsets.only(bottom: PlinkSpacing.s2),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Text(l10n.alertsPopupsHowToAllow, style: style),
          for (final String step in <String>[
            l10n.alertsPopupsStepIcon,
            l10n.alertsPopupsStepAllow,
            l10n.alertsPopupsStepReload,
          ])
            Padding(
              padding: const EdgeInsets.only(left: PlinkSpacing.s3),
              child: Text(step, style: style),
            ),
        ],
      ),
    );
  }
}
