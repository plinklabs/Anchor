import 'package:flutter/foundation.dart';

/// The short sound the live page plays when a session needs the teacher
/// (#454). The page plays it itself, so it needs no notification permission
/// and Windows' "Do not disturb" (on during a PowerPoint slide show) doesn't
/// hold it back. It plays from a background tab too.
///
/// Browsers allow a page sound only once the teacher has interacted with it.
/// Opening the live page by clicking through the dashboard counts; reloading
/// it doesn't, until they click somewhere. Until then [blocked] is true, and
/// the page says so rather than failing silently.
abstract class AlertSound {
  /// Whether the browser holds the page's sound back until the teacher
  /// interacts with the page.
  ValueListenable<bool> get blocked;

  /// The teacher clicked or pressed a key on the page: the browser now lets
  /// it play sound, so get ready to (and clear [blocked]).
  void unlock();

  /// Plays the sound once, now, if the browser lets the page play sound.
  /// Never later: a sound the browser held back isn't played on the
  /// teacher's next click.
  void play();

  void dispose();
}
