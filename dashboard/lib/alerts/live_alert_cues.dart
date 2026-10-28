import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/services.dart';

import '../app_title.dart';
import '../l10n/app_localizations.dart';
import '../realtime/session_hub_client.dart';
import 'alert_popups.dart';
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

/// The tag of the pop-up for [category] on the live page of session [scope]
/// (#455): one pop-up per kind of alert, which a newer one replaces.
String alertPopupTag(String scope, LiveAlertCategory category) =>
    'anchor:$scope:${category.name}';

/// The tag of the Test button's sample pop-up.
const String alertPopupTestTag = 'anchor:test';

/// What the pop-up for [alert] says (#455). The title says what happened,
/// with [count] of them in brackets past one, and never names anyone: the
/// screen may be projected. With [names] on, the body names the student, and
/// for an access request the site, of [alert]; off, it is empty.
///
/// The name comes from the event as the hub contract gives it; an agent that
/// stopped reporting only carries the student's id, so [nameOf] looks it up
/// (the roster).
({String title, String body}) alertPopupText(
  AppLocalizations l10n,
  LiveAlert alert, {
  required int count,
  required bool names,
  String? Function(String userId)? nameOf,
}) {
  final int n = math.max(1, count);
  final String title = switch (alert.category) {
    LiveAlertCategory.accessRequest => l10n.alertsPopupAccessRequest(n),
    LiveAlertCategory.studentLeft => l10n.alertsPopupStudentLeft(n),
    LiveAlertCategory.agentLost => l10n.alertsPopupAgentLost(n),
    LiveAlertCategory.tamper => l10n.alertsPopupTamper(n),
  };
  if (!names) return (title: title, body: '');
  final SessionEvent event = alert.event;
  final String? name = _studentName(event, nameOf);
  if (name == null) return (title: title, body: '');
  final String body = switch (alert.category) {
    LiveAlertCategory.accessRequest => switch (event.payload['host']) {
      final String host when host.isNotEmpty => l10n.alertsPopupAsks(
        name,
        host,
      ),
      _ => '',
    },
    LiveAlertCategory.studentLeft =>
      event.payload['state'] == 'Declined'
          ? l10n.alertsPopupDeclined(name)
          : l10n.alertsPopupLeft(name),
    LiveAlertCategory.agentLost => l10n.alertsPopupAgentLostOf(name),
    LiveAlertCategory.tamper => l10n.alertsPopupTamperOf(name),
  };
  return (title: title, body: body);
}

/// The student [event] is about, by name: from the field its kind carries
/// in the hub contract, or else [nameOf] its `userId`.
String? _studentName(
  SessionEvent event,
  String? Function(String userId)? nameOf,
) {
  final Object? name = switch (event.kind) {
    'UnblockRequested' || 'TamperDetected' => event.payload['userDisplayName'],
    'ParticipantStateChanged' => event.payload['displayName'],
    _ => null,
  };
  if (name is String && name.isNotEmpty) return name;
  final Object? id = event.payload['userId'];
  final String? looked = id == null ? null : nameOf?.call('$id');
  return looked == null || looked.isEmpty ? null : looked;
}

/// Shows a system pop-up for live-session alerts while the teacher can't see
/// the page (#455), when they turned pop-ups on and the browser allows them.
/// A consumer of [LiveAlerts], like [AlertSoundCue].
///
/// One pop-up per burst of each kind: a new one on
/// [LiveAlert.startsCategoryBurst], tagged by kind, so it replaces the last
/// one of that kind rather than stacking. Its count is what the tab title
/// counts for that kind ([LiveAlerts.unseenOf]).
///
/// Clicking one brings the dashboard to the front, closes it and tells
/// [onOpen] its kind. Coming back to the page takes the rest away: they're
/// stale once the teacher sees the page, as the title's count is.
class AlertPopupCue {
  AlertPopupCue({
    required LiveAlerts alerts,
    required AlertPopups popups,
    required this.l10n,
    required String scope,
    String? Function(String userId)? nameOf,
    void Function(LiveAlertCategory category)? onOpen,
  }) : _alerts = alerts,
       _popups = popups,
       _scope = scope,
       _nameOf = nameOf,
       _onOpen = onOpen {
    _subscription = alerts.alerts.listen(_onAlert);
    alerts.addListener(_onCountChanged);
  }

  final LiveAlerts _alerts;
  final AlertPopups _popups;
  final String _scope;
  final String? Function(String userId)? _nameOf;
  final void Function(LiveAlertCategory category)? _onOpen;
  late final StreamSubscription<LiveAlert> _subscription;

  /// The page's strings, which the pop-ups speak in.
  AppLocalizations l10n;

  /// The tags of the pop-ups this page showed and hasn't closed.
  final Set<String> _shown = {};

  void _onAlert(LiveAlert alert) {
    if (!alert.startsCategoryBurst ||
        !_alerts.pageHidden ||
        !_alerts.settings.popups) {
      return;
    }
    final LiveAlertCategory category = alert.category;
    final ({String title, String body}) text = alertPopupText(
      l10n,
      alert,
      count: _alerts.unseenOf(category),
      names: _alerts.settings.popupNames,
      nameOf: _nameOf,
    );
    final String tag = alertPopupTag(_scope, category);
    _popups.show(
      AlertPopup(
        tag: tag,
        title: text.title,
        body: text.body,
        onClick: () => _open(category, tag),
      ),
    );
    _shown.add(tag);
  }

  void _open(LiveAlertCategory category, String tag) {
    _popups.bringPageToFront();
    _close(tag);
    _onOpen?.call(category);
  }

  /// The page shows again: the teacher sees what the pop-ups said.
  void _onCountChanged() {
    if (!_alerts.pageHidden) _closeAll();
  }

  void _close(String tag) {
    if (_shown.remove(tag)) _popups.close(tag);
  }

  void _closeAll() {
    for (final String tag in [..._shown]) {
      _close(tag);
    }
  }

  /// Stops showing pop-ups, and takes this page's away: clicking one would
  /// lead nowhere once the live page is gone.
  void dispose() {
    _alerts.removeListener(_onCountChanged);
    unawaited(_subscription.cancel());
    _closeAll();
  }
}
