import 'package:flutter/foundation.dart';

/// Whether the browser lets the dashboard show pop-ups (#455): its
/// notification permission for the dashboard's site.
enum PopupPermission {
  /// The browser hasn't asked the teacher yet (`default`). The dashboard
  /// asks when they turn pop-ups on, never before.
  notAsked,

  /// The teacher allowed them (`granted`).
  allowed,

  /// The teacher blocked them (`denied`). The browser won't let the page ask
  /// again: only the teacher can undo it, in the browser's site settings.
  blocked,

  /// The browser has no system notifications for pages.
  unsupported,
}

/// One system pop-up (a notification in the corner of the screen).
@immutable
class AlertPopup {
  const AlertPopup({
    required this.tag,
    required this.title,
    this.body = '',
    this.onClick,
  });

  /// A pop-up with the same tag replaces this one, rather than stacking.
  final String tag;
  final String title;
  final String body;

  /// The teacher clicked it.
  final VoidCallback? onClick;
}

/// The browser's system pop-ups for live-session alerts (#455): what the
/// teacher allowed, asking them, and showing one.
///
/// Pop-ups are silent: the page plays its own sound (#454), so the teacher
/// doesn't hear two, and it still plays when Windows holds the pop-up back
/// ("Do not disturb" during a slide show).
abstract class AlertPopups {
  /// The browser's permission, as last read. The teacher can change it in
  /// the browser's site settings at any time, so [refresh] reads it again.
  ValueListenable<PopupPermission> get permission;

  /// Reads the browser's permission again.
  void refresh();

  /// Asks the browser for permission, and returns the answer. Only ever from
  /// the teacher's own click: a browser treats a request without one less
  /// kindly (Firefox ignores it), and it is the moment teachers block it.
  Future<PopupPermission> request();

  /// Shows [popup] now, in place of a shown one with the same tag, when the
  /// browser allows pop-ups; nothing otherwise.
  void show(AlertPopup popup);

  /// Takes the pop-up with [tag] away, if it is still there.
  void close(String tag);

  /// Brings the dashboard's tab and window to the front: the teacher
  /// clicked one of its pop-ups.
  void bringPageToFront();

  void dispose();
}
