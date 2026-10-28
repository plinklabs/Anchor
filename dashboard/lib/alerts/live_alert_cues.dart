import 'dart:async';

import 'package:flutter/services.dart';

import '../app_title.dart';
import 'alert_sound.dart';
import 'live_alerts.dart';

/// The browser tab's title with [unseen] alerts in front: "(3) Anchor", or
/// just "Anchor" when there are none (#454).
String alertTabTitle(int unseen) =>
    unseen == 0 ? appTitle : '($unseen) $appTitle';

/// Plays the alert sound on the first alert of each burst (#454), while the
/// teacher has sound on. A consumer of [LiveAlerts]: it decides nothing about
/// which events alert.
class AlertSoundCue {
  AlertSoundCue({required LiveAlerts alerts, required AlertSound sound}) {
    _subscription = alerts.alerts.listen((alert) {
      if (alert.startsBurst && alerts.settings.sound) sound.play();
    });
  }

  late final StreamSubscription<LiveAlert> _subscription;

  void dispose() => unawaited(_subscription.cancel());
}

/// Counts the alerts the teacher hasn't seen in the browser tab's title,
/// "(3) Anchor", while the page is hidden (#454). A consumer of [LiveAlerts].
///
/// It sets the title the moment the count changes, not in a build: a
/// background tab draws no frames, so a `Title` widget would only catch up
/// once the teacher is back.
class AlertTabTitle {
  AlertTabTitle({required LiveAlerts alerts, required Color color})
    : _alerts = alerts,
      _color = color {
    _alerts.addListener(_update);
  }

  final LiveAlerts _alerts;

  /// The app's colour, which the title carries along (the browser's
  /// theme-color on the web build), as `MaterialApp`'s own title does.
  final Color _color;
  int _shown = 0;

  void _update() {
    final int unseen = _alerts.unseenCount;
    if (unseen == _shown) return;
    _shown = unseen;
    _show(alertTabTitle(unseen));
  }

  void _show(String title) {
    unawaited(
      SystemChrome.setApplicationSwitcherDescription(
        ApplicationSwitcherDescription(
          label: title,
          primaryColor: _color.toARGB32(),
        ),
      ),
    );
  }

  /// Stops counting, and puts the plain title back if it showed a count.
  void dispose() {
    _alerts.removeListener(_update);
    if (_shown != 0) _show(appTitle);
  }
}
