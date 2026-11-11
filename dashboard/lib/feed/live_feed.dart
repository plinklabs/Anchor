import 'package:flutter/foundation.dart';

import '../alerts/live_alerts.dart';
import '../api/sessions_api.dart';
import '../l10n/app_localizations.dart';
import '../realtime/session_hub_client.dart';

/// How much an event in the live session's activity feed asks of the teacher
/// (#491).
enum FeedPriority {
  /// What goes on in a running session: a student joining, the session
  /// starting or ending, an agent coming back, anything the feed doesn't
  /// know. Shown only once the teacher shows all activity.
  routine,

  /// Worth the teacher's attention: an access request, a student leaving or
  /// declining. Shown by default.
  attention,

  /// Worth their attention, with the stronger marker: a student's laptop
  /// isn't enforced (their agent stopped reporting or never connected), or
  /// someone tampered with it. Shown by default.
  warning,
}

/// The live states a `ParticipantStateChanged` can carry that mean nothing
/// is being enforced on the student's laptop, although no alert goes off for
/// them (#444). The teacher can act on these, so the feed shows them.
const Set<ParticipantLiveState> _unenforcedStates = {
  ParticipantLiveState.agentMissing,
  ParticipantLiveState.heartbeatStale,
};

/// The live state a `ParticipantStateChanged` carries, or null for any other
/// kind. Reads only `state`, which the hub contract gives it.
ParticipantLiveState? _stateOf(SessionEvent event) {
  if (event.kind != 'ParticipantStateChanged') return null;
  final Object? state = event.payload['state'];
  return ParticipantLiveState.parse(state is String ? state : null);
}

/// How much [event] asks of the teacher. It follows the live alerts'
/// categories ([liveAlertCategoryOf]), so the feed and the alerts agree on
/// what matters, and adds the states that leave a laptop unenforced. It never
/// follows the teacher's switches in the Notifications dialog: those decide
/// what may interrupt them, not what is worth seeing on the page.
FeedPriority feedPriorityOf(SessionEvent event) {
  final ParticipantLiveState? state = _stateOf(event);
  if (state != null && _unenforcedStates.contains(state)) {
    return FeedPriority.warning;
  }
  switch (liveAlertCategoryOf(event)) {
    case LiveAlertCategory.agentLost:
    case LiveAlertCategory.tamper:
      return FeedPriority.warning;
    case LiveAlertCategory.accessRequest:
    case LiveAlertCategory.studentLeft:
      return FeedPriority.attention;
    case null:
      return FeedPriority.routine;
  }
}

/// [event] as one plain sentence the teacher reads at a glance (#491), in
/// their language, naming the student it is about: never the event's kind, an
/// id or a timestamp. An event the feed doesn't know gets a sentence too.
///
/// The name comes from the event as the hub contract gives it; a lost or
/// returning agent carries only the student's id, so [nameOf] looks it up
/// (the roster). Reads only the fields the contract gives each kind.
String feedSentence(
  AppLocalizations l10n,
  SessionEvent event, {
  String? Function(String userId)? nameOf,
}) {
  final String? name = liveEventStudentName(event, nameOf);
  final String who = name == null ? 'unknown' : 'named';
  final String n = name ?? '';
  switch (event.kind) {
    case 'SessionStarted':
      return _sessionStarted(l10n, event.payload);
    case 'SessionEnded':
      return l10n.feedSessionEnded;
    case 'ParticipantStateChanged':
      return switch (_stateOf(event)!) {
        ParticipantLiveState.joined => l10n.feedJoined(who, n),
        ParticipantLiveState.left => l10n.feedLeft(who, n),
        ParticipantLiveState.declined => l10n.feedDeclined(who, n),
        ParticipantLiveState.neverJoined => l10n.feedNotJoined(who, n),
        ParticipantLiveState.agentMissing => l10n.feedAgentMissing(who, n),
        ParticipantLiveState.heartbeatStale => l10n.feedAgentStopped(who, n),
        ParticipantLiveState.unknown => l10n.feedStateChanged(who, n),
      };
    case 'HeartbeatLost':
      return l10n.feedAgentStopped(who, n);
    case 'AgentReconnected':
      return l10n.feedAgentBack(who, n);
    case 'UnblockRequested':
      final Object? host = event.payload['host'];
      return l10n.feedAsks(
        who,
        n,
        host is String && host.isNotEmpty ? host : l10n.feedSomeSite,
      );
    case 'TamperDetected':
      return switch (event.payload['kind']) {
        'extension_disabled' => l10n.feedTamperExtensionDisabled(who, n),
        'extension_silent' => l10n.feedTamperExtensionSilent(who, n),
        'inprivate_opened' => l10n.feedTamperInPrivate(who, n),
        'host_permission_revoked' => l10n.feedTamperSiteAccess(who, n),
        'agent_unavailable' => l10n.feedTamperAgentUnavailable(who, n),
        _ => l10n.feedTamperOther(who, n),
      };
  }
  return l10n.feedOther;
}

/// The session starting, with its join code and how much it allows, as far
/// as the payload says.
String _sessionStarted(AppLocalizations l10n, Map<String, dynamic> payload) {
  final Object? code = payload['joinCode'];
  if (code is! String || code.isEmpty) return l10n.feedSessionStarted;
  final Object? apps = payload['apps'];
  final Object? domains = payload['domains'];
  if (apps is! List || domains is! List) {
    return l10n.feedSessionStartedCode(code);
  }
  return l10n.feedSessionStartedAllowed(
    code,
    l10n.feedApps(apps.length),
    l10n.feedSites(domains.length),
  );
}

/// One row of the activity feed: an event as the teacher reads it, or a run
/// of the same sentence about the same student in a row (a student who
/// reconnects can send several), shown once.
@immutable
class FeedEntry {
  const FeedEntry({
    required this.event,
    required this.sentence,
    required this.priority,
    this.count = 1,
  });

  /// The newest event of the run: its time is the row's.
  final SessionEvent event;
  final String sentence;
  final FeedPriority priority;

  /// How many events in a row the entry stands for.
  final int count;
}

/// The rows the activity feed shows for [events], newest first, as they
/// came: with [showAll] off only those that need the teacher's attention
/// ([FeedPriority.attention] and up), with it on all of them. The same
/// sentence about the same student in a row, as shown, is one row with its
/// count.
///
/// The events can come from the live hub or, rebuilt in the hub's shapes,
/// from the session's history: the feed reads them the same way.
List<FeedEntry> feedEntries(
  AppLocalizations l10n,
  Iterable<SessionEvent> events, {
  required bool showAll,
  String? Function(String userId)? nameOf,
}) {
  final List<FeedEntry> entries = [];
  String? lastUser;
  for (final SessionEvent event in events) {
    final FeedPriority priority = feedPriorityOf(event);
    if (!showAll && priority == FeedPriority.routine) continue;
    final String sentence = feedSentence(l10n, event, nameOf: nameOf);
    final String? user = liveEventUserId(event);
    if (entries.isNotEmpty &&
        entries.last.sentence == sentence &&
        lastUser == user) {
      final FeedEntry newer = entries.removeLast();
      entries.add(
        FeedEntry(
          event: newer.event,
          sentence: sentence,
          priority: newer.priority,
          count: newer.count + 1,
        ),
      );
      continue;
    }
    entries.add(
      FeedEntry(event: event, sentence: sentence, priority: priority),
    );
    lastUser = user;
  }
  return entries;
}
