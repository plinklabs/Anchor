import 'dart:async';
import 'dart:js_interop';

import 'package:flutter/foundation.dart';
import 'package:web/web.dart' as web;

import 'alert_sound.dart';
import 'live_alert_settings.dart';

/// Web implementations of the live-alert seams (#454). Only ever built on the
/// web build (see the conditional import in `alert_platform.dart`).
AlertSound createAlertSound() => WebAlertSound();

AlertSettingsStore createAlertSettingsStore() => _LocalStorageStore();

class _LocalStorageStore implements AlertSettingsStore {
  @override
  String? read() =>
      web.window.localStorage.getItem(LiveAlertSettings.storageKey);

  @override
  void write(String value) =>
      web.window.localStorage.setItem(LiveAlertSettings.storageKey, value);
}

/// The alert sound over Web Audio: a soft two-note chime the page
/// synthesizes, so there's no file to load. Audio runs on its own clock, which
/// a background tab doesn't throttle.
///
/// Autoplay: an `AudioContext` made before the teacher interacted with the
/// page starts suspended, and Chrome warns about it. So the context is made
/// only once the page has had their interaction (`navigator.userActivation`),
/// and until then [blocked] is true. While it is, any click or key press on
/// the page, the app bar included, unlocks it; the live page also unlocks it
/// on a click of its own.
class WebAlertSound implements AlertSound {
  WebAlertSound() {
    _blocked.value = !_mayStart();
    _blocked.addListener(_followInteraction);
    _followInteraction();
  }

  final _blocked = ValueNotifier<bool>(true);
  web.AudioContext? _context;

  static const List<String> _interactions = [
    'pointerdown',
    'pointerup',
    'keydown',
  ];
  late final JSFunction _onInteraction = ((web.Event _) => unlock()).toJS;
  bool _following = false;

  /// Listens for the teacher's interaction while sound is blocked, in the
  /// capture phase so nothing on the page can swallow it.
  void _followInteraction() {
    final bool follow = _blocked.value;
    if (follow == _following) return;
    _following = follow;
    for (final String type in _interactions) {
      if (follow) {
        web.window.addEventListener(type, _onInteraction, true.toJS);
      } else {
        web.window.removeEventListener(type, _onInteraction, true.toJS);
      }
    }
  }

  /// A resume that the browser lets through comes back at once. One that
  /// takes longer was held until a later click, and the alert is stale by
  /// then.
  static const Duration _staleAfter = Duration(seconds: 2);

  @override
  ValueListenable<bool> get blocked => _blocked;

  /// Whether the browser lets the page start sound: it has had the teacher's
  /// interaction. Without `navigator.userActivation` (an old browser), try.
  static bool _mayStart() {
    try {
      return web.window.navigator.userActivation.hasBeenActive;
    } catch (_) {
      return true;
    }
  }

  web.AudioContext? _ensureContext() {
    final web.AudioContext? existing = _context;
    if (existing != null) return existing;
    if (!_mayStart()) return null;
    try {
      final context = web.AudioContext();
      context.addEventListener(
        'statechange',
        ((web.Event _) => _refresh()).toJS,
      );
      _context = context;
    } catch (_) {
      // No Web Audio: nothing to play.
    }
    _refresh();
    return _context;
  }

  void _refresh() {
    _blocked.value = _context?.state != 'running';
  }

  @override
  void unlock() {
    final web.AudioContext? context = _ensureContext();
    if (context == null || context.state != 'suspended') return;
    unawaited(_resume(context));
  }

  Future<bool> _resume(web.AudioContext context) async {
    try {
      await context.resume().toDart;
    } catch (_) {
      // Refused: still blocked.
    }
    _refresh();
    return context.state == 'running';
  }

  @override
  void play() {
    final web.AudioContext? context = _ensureContext();
    if (context == null) return;
    if (context.state == 'running') {
      scheduleChime(context);
      return;
    }
    if (context.state != 'suspended') return;
    final DateTime askedAt = DateTime.now();
    unawaited(
      _resume(context).then((running) {
        if (running && DateTime.now().difference(askedAt) < _staleAfter) {
          scheduleChime(context);
        }
      }),
    );
  }

  /// Schedules the chime on [context] from its current time: two soft sine
  /// notes (A5, then E6), each fading out within half a second.
  @visibleForTesting
  static void scheduleChime(web.BaseAudioContext context) {
    final double start = context.currentTime.toDouble();
    for (final (double offset, double frequency) in const [
      (0.0, 880.0),
      (0.16, 1318.5),
    ]) {
      final double at = start + offset;
      final web.OscillatorNode oscillator = context.createOscillator()
        ..type = 'sine';
      oscillator.frequency.setValueAtTime(frequency, at);
      final web.GainNode gain = context.createGain();
      gain.gain
        ..setValueAtTime(0.0001, at)
        ..exponentialRampToValueAtTime(0.3, at + 0.015)
        ..exponentialRampToValueAtTime(0.0001, at + 0.45);
      oscillator.connect(gain);
      gain.connect(context.destination);
      oscillator
        ..start(at)
        ..stop(at + 0.5);
    }
  }

  @override
  void dispose() {
    _blocked.removeListener(_followInteraction);
    if (_following) {
      _following = false;
      for (final String type in _interactions) {
        web.window.removeEventListener(type, _onInteraction, true.toJS);
      }
    }
    final web.AudioContext? context = _context;
    _context = null;
    if (context != null) unawaited(context.close().toDart);
    _blocked.dispose();
  }
}
