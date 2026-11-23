import 'dart:js_interop';
import 'dart:typed_data';

import 'package:web/web.dart' as web;

import 'log_file_io.dart';

/// Web implementation of [LogFileIo] over `package:web`: a Blob behind an
/// object URL, clicked through a transient anchor. Only ever instantiated on
/// the web build (see the conditional import in `log_file_io.dart`).
LogFileIo createLogFileIo() => _WebLogFileIo();

class _WebLogFileIo implements LogFileIo {
  @override
  void save(String fileName, Uint8List bytes, String mimeType) {
    // A Blob rather than a data URL: a zip of a class's logs runs to megabytes.
    final blob = web.Blob(
      <JSAny>[bytes.toJS].toJS,
      web.BlobPropertyBag(type: mimeType),
    );
    final url = web.URL.createObjectURL(blob);
    final anchor = web.HTMLAnchorElement()
      ..href = url
      ..download = fileName;
    // Some browsers only honour a programmatic click when the anchor is in the
    // document, so attach-click-detach rather than clicking a detached node.
    web.document.body!.appendChild(anchor);
    anchor.click();
    anchor.remove();
    // The download has its own copy once the click is handled.
    Future<void>.delayed(
      const Duration(seconds: 1),
      () => web.URL.revokeObjectURL(url),
    );
  }
}
