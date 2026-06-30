import 'bundle_file_io_stub.dart'
    if (dart.library.js_interop) 'bundle_file_io_web.dart'
    as platform;

/// The browser file-IO seam for bundle import/export (#304): a download and a
/// file-pick, kept behind an interface so the page logic (and its tests) never
/// touch `package:web`. The concrete implementation is web-only; VM unit /
/// widget tests inject a fake.
abstract class BundleFileIo {
  /// Triggers a browser download of [contents] under [filename].
  void downloadJson(String filename, String contents);

  /// Opens the OS file picker and resolves to the chosen file's text, or null
  /// if the picker yields nothing. May never complete if the user cancels —
  /// callers must not gate persistent UI state on it.
  Future<String?> pickJsonFile();
}

/// The platform implementation, resolved by conditional import: the real
/// `package:web` seam on the web build, a throwing stub elsewhere (the
/// dashboard only ships to the web).
BundleFileIo createBundleFileIo() => platform.createBundleFileIo();
