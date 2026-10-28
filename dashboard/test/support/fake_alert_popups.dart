import 'package:anchor_dashboard/alerts/alert_popups.dart';
import 'package:flutter/foundation.dart';

/// The browser's system pop-ups as a test drives them (#455), standing in
/// for its Notification API.
///
/// [browser] is what the browser holds for the dashboard: `default` (not
/// asked yet), `granted` or `denied`, and null for a browser without
/// pop-ups. [answer] is what the teacher answers when it asks. The teacher
/// can change [browser] in the site settings at any time; the dashboard only
/// sees it when it reads it again.
class FakeAlertPopups implements AlertPopups {
  FakeAlertPopups({this.browser = 'default', this.answer = 'granted'})
    : _permission = ValueNotifier(_of(browser));

  String? browser;
  String answer;

  /// How often the dashboard asked the browser for permission.
  int asked = 0;

  /// How often a click on a pop-up brought the dashboard to the front.
  int broughtToFront = 0;

  /// What bringing the dashboard to the front does in the browser: a test
  /// shows the page, as the browser reports it then.
  VoidCallback? onFront;

  /// Every pop-up the browser showed, in order.
  final List<AlertPopup> shown = [];

  /// The pop-ups on screen now, by tag: a new one replaces one with its tag.
  final Map<String, AlertPopup> onScreen = {};

  final ValueNotifier<PopupPermission> _permission;

  static PopupPermission _of(String? browser) => switch (browser) {
    null => PopupPermission.unsupported,
    'granted' => PopupPermission.allowed,
    'denied' => PopupPermission.blocked,
    _ => PopupPermission.notAsked,
  };

  @override
  ValueListenable<PopupPermission> get permission => _permission;

  @override
  void refresh() => _permission.value = _of(browser);

  @override
  Future<PopupPermission> request() async {
    asked++;
    // Only a browser that hasn't asked yet asks; one that has keeps its
    // answer.
    if (browser == 'default') browser = answer;
    refresh();
    return _permission.value;
  }

  @override
  void show(AlertPopup popup) {
    // The browser only shows one when the teacher allowed them.
    if (browser != 'granted') return;
    shown.add(popup);
    onScreen[popup.tag] = popup;
  }

  @override
  void close(String tag) => onScreen.remove(tag);

  @override
  void bringPageToFront() {
    broughtToFront++;
    onFront?.call();
  }

  /// The teacher clicks the pop-up with [tag].
  void click(String tag) => onScreen[tag]!.onClick?.call();

  @override
  void dispose() {}
}
