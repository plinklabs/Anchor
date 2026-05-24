import 'bundle_prefs_stub.dart'
    if (dart.library.js_interop) 'bundle_prefs_web.dart';

/// Per-teacher persistence of the last-used bundle selection on the start-session
/// flow (#69). Scope is the local browser, keyed by [accountKey] (the MSAL
/// homeAccountId) so a shared workstation keeps each teacher's preferences
/// separate. Returns null on non-web builds and on first use.
abstract class BundlePrefs {
  factory BundlePrefs() = BundlePrefsImpl;

  List<String>? readSelection(String accountKey);
  void writeSelection(String accountKey, List<String> bundleIds);
}
