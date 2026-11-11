import 'dart:async';

import 'package:flutter/foundation.dart';

import '../realtime/session_hub_client.dart';
import 'live_alert_settings.dart';

/// The kinds of live-session event that alert the teacher (#454), each with
/// its own switch in the Notifications dialog. A student joining (or their
/// agent coming back) never alerts.
enum LiveAlertCategory {
  /// A student asks for access to a site: `UnblockRequested`.
  accessRequest,

  /// A student left or declined the session: `ParticipantStateChanged` with
  /// the state `Left` or `Declined`.
  studentLeft,

  /// A student's agent stopped reporting: `HeartbeatLost`.
  agentLost,

  /// A tamper attempt: `TamperDetected`.
  tamper,
}

/// One event from the live feed that the teacher should be told about.
///
/// Every new alert reaches the [LiveAlerts.alerts] stream. Whether it also
/// interrupts the teacher (a sound, and a pop-up in #455) depends on
/// [startsBurst] / [startsCategoryBurst], so a burst of them is one
/// interruption.
@immutable
class LiveAlert {
  const LiveAlert({
    required this.category,
    required this.event,
    required this.startsBurst,
    required this.startsCategoryBurst,
  });

  final LiveAlertCategory category;

  /// The hub event that raised it: who and which site, for a consumer that
  /// names them (the pop-ups of #455). Read only the fields
  /// contracts/session-hub.json gives [SessionEvent.kind].
  final SessionEvent event;

  /// The first alert after a quiet [LiveAlerts.burstWindow], of any
  /// category: the one the sound plays for. Leading edge, so it plays at once
  /// and never waits on a timer, which a background tab throttles.
  final bool startsBurst;

  /// The first alert of [category] after a quiet [LiveAlerts.burstWindow] for
  /// that category. A consumer that keeps one notice per category (a pop-up
  /// tagged by category, #455) shows a new one on these.
  final bool startsCategoryBurst;
}

/// The category [event] alerts in, or null when it never alerts: a join, an
/// agent coming back, the session starting or ending, or a participant state
/// other than leaving or declining.
///
/// Reads only `state` of a `ParticipantStateChanged`, which the hub contract
/// gives it.
LiveAlertCategory? liveAlertCategoryOf(SessionEvent event) {
  switch (event.kind) {
    case 'UnblockRequested':
      return LiveAlertCategory.accessRequest;
    case 'ParticipantStateChanged':
      final Object? state = event.payload['state'];
      return state == 'Left' || state == 'Declined'
          ? LiveAlertCategory.studentLeft
          : null;
    case 'HeartbeatLost':
      return LiveAlertCategory.agentLost;
    case 'TamperDetected':
      return LiveAlertCategory.tamper;
  }
  return null;
}

/// The one place that decides whether an event from a live session is a new
/// alert the teacher should be told about (#454), and keeps count of the
/// alerts they haven't seen. Its consumers ([AlertSoundCue], [AlertTabTitle],
/// and the pop-ups of #455) only act on what it decides.
///
/// An event is a new alert when:
/// - it falls in a [LiveAlertCategory] the teacher has on in [settings];
/// - it isn't news about a student the page already alerted about: once a
///   student left, declined or their agent stopped reporting, another of
///   those, or a tamper report, for that student is the same episode (a
///   `Leave session` that's followed by `HeartbeatLost`, #448) until they
///   join again or their agent comes back;
/// - it isn't the same student asking for the same site again.
///
/// Alerts come in bursts (twenty students asking for one site): the first one
/// after a quiet [burstWindow] starts a burst, and the sound plays only for
/// that one.
///
/// While the page is hidden ([pageHidden]: another tab, or a window over it),
/// each new alert adds to [unseenCount]; showing the page clears it.
class LiveAlerts extends ChangeNotifier {
  LiveAlerts({
    required this.settings,
    DateTime Function()? clock,
    this.burstWindow = defaultBurstWindow,
  }) : _clock = clock ?? DateTime.now;

  /// How long after the first alert of a burst further alerts are part of
  /// it, and stay quiet.
  static const Duration defaultBurstWindow = Duration(seconds: 10);

  final LiveAlertSettings settings;
  final Duration burstWindow;
  final DateTime Function() _clock;

  final _alerts = StreamController<LiveAlert>.broadcast(sync: true);
  DateTime? _burstStartedAt;
  final Map<LiveAlertCategory, DateTime> _categoryBurstStartedAt = {};

  /// Students who left, declined or whose agent stopped reporting, since the
  /// page alerted about it (or would have, with the category on).
  final Set<String> _gone = {};

  /// `userId host` of every access request the page alerted about.
  final Set<String> _asked = {};

  final Map<LiveAlertCategory, int> _unseen = {};
  bool _pageHidden = false;

  /// Every new alert, as [handle] decides it. Delivered synchronously, in the
  /// call that handles the event: a background tab runs no frames and
  /// throttles timers, but this still runs as the event arrives.
  Stream<LiveAlert> get alerts => _alerts.stream;

  /// Whether the teacher can't see the page: its tab is in the background or
  /// a window covers it. Showing it again clears [unseenCount].
  bool get pageHidden => _pageHidden;
  set pageHidden(bool hidden) {
    if (hidden == _pageHidden) return;
    _pageHidden = hidden;
    if (!hidden && _unseen.isNotEmpty) {
      _unseen.clear();
      notifyListeners();
    }
  }

  /// The alerts that came in while the page was hidden.
  int get unseenCount => _unseen.values.fold(0, (sum, n) => sum + n);

  /// The alerts of [category] that came in while the page was hidden.
  int unseenOf(LiveAlertCategory category) => _unseen[category] ?? 0;

  /// Takes one event about this page's session from its live feed, and
  /// returns the new alert it raises, or null.
  LiveAlert? handle(SessionEvent event) {
    final LiveAlertCategory? category = liveAlertCategoryOf(event);
    final String? userId = _userOf(event);
    if (category == null) {
      // Back in the session: news about this student alerts again.
      if (userId != null &&
          (event.kind == 'AgentReconnected' ||
              (event.kind == 'ParticipantStateChanged' &&
                  event.payload['state'] == 'Joined'))) {
        _gone.remove(userId);
      }
      return null;
    }
    if (!_isNews(category, event, userId)) return null;
    if (!settings.isOn(category)) return null;

    final DateTime now = _clock();
    final alert = LiveAlert(
      category: category,
      event: event,
      startsBurst: _opens(_burstStartedAt, now),
      startsCategoryBurst: _opens(_categoryBurstStartedAt[category], now),
    );
    if (alert.startsBurst) _burstStartedAt = now;
    if (alert.startsCategoryBurst) _categoryBurstStartedAt[category] = now;
    if (_pageHidden) {
      _unseen[category] = unseenOf(category) + 1;
      notifyListeners();
    }
    _alerts.add(alert);
    return alert;
  }

  /// Whether [event] says something the page hasn't alerted about yet, and
  /// remembers it.
  bool _isNews(LiveAlertCategory category, SessionEvent event, String? who) {
    if (who == null) return true;
    switch (category) {
      case LiveAlertCategory.studentLeft:
      case LiveAlertCategory.agentLost:
        return _gone.add(who);
      case LiveAlertCategory.tamper:
        return !_gone.contains(who);
      case LiveAlertCategory.accessRequest:
        return _asked.add('$who ${event.payload['host']}');
    }
  }

  bool _opens(DateTime? burstStartedAt, DateTime now) {
    if (burstStartedAt == null) return true;
    final Duration since = now.difference(burstStartedAt);
    return since >= burstWindow || since.isNegative;
  }

  /// The student an event is about. Every event in a category, and the
  /// joins, carry `userId` in the hub contract.
  static String? _userOf(SessionEvent event) {
    switch (event.kind) {
      case 'UnblockRequested':
      case 'ParticipantStateChanged':
      case 'HeartbeatLost':
      case 'TamperDetected':
      case 'AgentReconnected':
        final Object? id = event.payload['userId'];
        return id == null ? null : '$id';
    }
    return null;
  }

  @override
  void dispose() {
    _alerts.close();
    super.dispose();
  }
}
