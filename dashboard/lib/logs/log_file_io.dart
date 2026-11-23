import 'dart:typed_data';

import 'log_file_io_stub.dart'
    if (dart.library.js_interop) 'log_file_io_web.dart'
    as platform;

/// The browser download seam for Admin → Logs (#566): saves a downloaded log
/// or the access-history CSV as a file, kept behind an interface so the page
/// (and its tests) never touch `package:web`. The concrete implementation is
/// web-only; VM unit / widget tests and the integration tests inject a fake.
abstract class LogFileIo {
  /// Triggers a browser download of [bytes] under [fileName].
  void save(String fileName, Uint8List bytes, String mimeType);
}

/// The platform implementation, resolved by conditional import: the real
/// `package:web` seam on the web build, a throwing stub elsewhere (the
/// dashboard only ships to the web).
LogFileIo createLogFileIo() => platform.createLogFileIo();
