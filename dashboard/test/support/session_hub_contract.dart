import 'dart:convert';
import 'dart:io';

/// contracts/session-hub.json (#390): the session hub as the dashboard relies
/// on it. Backend CI checks that the backend still serves what it says; the
/// tests in test/contract/ check that the dashboard handles it.
class SessionHubContract {
  SessionHubContract._({
    required this.hubPath,
    required this.events,
    required this.methods,
  });

  /// Reads the file from the repo root: `flutter test` runs in dashboard/.
  factory SessionHubContract.load() {
    final json =
        jsonDecode(File('../contracts/session-hub.json').readAsStringSync())
            as Map<String, dynamic>;
    final events = json['events'] as Map<String, dynamic>;
    final methods = json['methods'] as Map<String, dynamic>;
    return SessionHubContract._(
      hubPath: json['hub'] as String,
      events: [
        for (final MapEntry(:key, :value) in events.entries)
          HubEvent._(key, value as Map<String, dynamic>),
      ],
      methods: [
        for (final MapEntry(:key, :value) in methods.entries)
          HubMessage._(key, value as Map<String, dynamic>),
      ],
    );
  }

  /// Where the backend maps the hub.
  final String hubPath;

  /// The events the dashboard listens for.
  final List<HubEvent> events;

  /// The hub methods the dashboard calls.
  final List<HubMessage> methods;
}

/// One message on the hub. It carries one argument: an object with [fields],
/// or a bare value that [argument] names.
class HubMessage {
  HubMessage._(this.name, Map<String, dynamic> json)
    : argument = json['argument'] as String?,
      fields = [...(json['fields'] as List? ?? const []).cast<String>()];

  final String name;
  final String? argument;
  final List<String> fields;

  /// The names its data goes by: the object's fields, or the bare value's
  /// name.
  Set<String> get names => argument != null ? {argument!} : fields.toSet();

  /// Its argument on the wire for a message about [sessionId]: the session id
  /// under `sessionId`, a placeholder under any other name.
  Object? wireArgument(String sessionId) {
    final argument = this.argument;
    return argument != null
        ? _valueOf(argument, sessionId)
        : {for (final f in fields) f: _valueOf(f, sessionId)};
  }

  /// What [SessionHubClient] passes on for it: the object as it came, or the
  /// bare value under its name.
  Map<String, dynamic> payload(String sessionId) {
    final argument = this.argument;
    return argument != null
        ? {argument: _valueOf(argument, sessionId)}
        : wireArgument(sessionId) as Map<String, dynamic>;
  }

  static String _valueOf(String name, String sessionId) =>
      name == 'sessionId' ? sessionId : 'contract-$name';
}

/// An event the hub sends, and who it goes to.
class HubEvent extends HubMessage {
  HubEvent._(super.name, super.json)
    : audience = {...(json['audience'] as List).cast<String>()},
      super._();

  /// Any of `session` (the session group: only the owning teacher's
  /// connections, once they joined it), `teacher` (the session teacher's user
  /// group: all their connections, for each of their sessions) and
  /// `participants` (the participants' user groups).
  final Set<String> audience;

  /// Whether it reaches the teacher's live page through their user group,
  /// which every connection of theirs is in, whatever session its page shows.
  /// So it can name another session.
  bool get reachesTheTeachersUserGroup => audience.contains('teacher');

  /// Whether it reaches the teacher's live page through the session group,
  /// which the page joins.
  bool get reachesTheSessionGroup => audience.contains('session');
}
