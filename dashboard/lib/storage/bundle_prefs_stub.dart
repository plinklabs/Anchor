import 'bundle_prefs.dart';

class BundlePrefsImpl implements BundlePrefs {
  BundlePrefsImpl();

  @override
  List<String>? readSelection(String accountKey) => null;

  @override
  void writeSelection(String accountKey, List<String> bundleIds) {}
}
