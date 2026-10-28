import 'package:flutter/material.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../l10n/app_localizations.dart';
import 'live_alert_services.dart';
import 'live_alerts.dart';

/// Opens the Notifications dialog (#454): sound on or off, which kinds of
/// live-session event alert, and a Test button. Reached from the bell in the
/// app bar, so it can be set up before a lesson, and from the live page's
/// header. The choices are kept in this browser.
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
class LiveAlertSettingsDialog extends StatelessWidget {
  const LiveAlertSettingsDialog({super.key, required this.services});

  final LiveAlertServices services;

  static String _categoryLabel(AppLocalizations l10n, LiveAlertCategory c) {
    return switch (c) {
      LiveAlertCategory.accessRequest => l10n.alertsAccessRequest,
      LiveAlertCategory.studentLeft => l10n.alertsStudentLeft,
      LiveAlertCategory.agentLost => l10n.alertsAgentLost,
      LiveAlertCategory.tamper => l10n.alertsTamper,
    };
  }

  @override
  Widget build(BuildContext context) {
    final AppLocalizations l10n = AppLocalizations.of(context);
    final settings = services.settings;
    final TextTheme text = Theme.of(context).textTheme;
    final TextStyle? hint = text.bodySmall?.copyWith(color: PlinkColors.ink60);
    return AlertDialog(
      title: Text(l10n.alertsTitle),
      content: SizedBox(
        width: 460,
        child: SingleChildScrollView(
          child: ListenableBuilder(
            listenable: settings,
            builder: (context, _) => Column(
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
            ),
          ),
        ),
      ),
      actions: <Widget>[
        // Plays whatever the switches say: it's how the teacher checks the
        // speakers before a lesson. The click also lets the browser play
        // sound from now on.
        OutlinedButton.icon(
          key: const Key('alerts-test'),
          onPressed: () => services.sound
            ..unlock()
            ..play(),
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
