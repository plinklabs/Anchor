import 'dart:async';
import 'dart:js_interop';

import 'package:web/web.dart' as web;

import 'bundle_file_io.dart';

/// Web implementation of [BundleFileIo] over `package:web`: a data-URL anchor
/// for download, and a transient `<input type=file>` for pick. Only ever
/// instantiated on the web build (see the conditional import in
/// `bundle_file_io.dart`).
BundleFileIo createBundleFileIo() => _WebBundleFileIo();

class _WebBundleFileIo implements BundleFileIo {
  @override
  void downloadJson(String filename, String contents) {
    // A data URL avoids the Blob/object-URL lifecycle; bundle JSON is small
    // enough that the URL length is never a concern.
    final href =
        'data:application/json;charset=utf-8,${Uri.encodeComponent(contents)}';
    final anchor = web.HTMLAnchorElement()
      ..href = href
      ..download = filename;
    // Some browsers only honour a programmatic click when the anchor is in the
    // document, so attach-click-detach rather than clicking a detached node.
    web.document.body!.appendChild(anchor);
    anchor.click();
    anchor.remove();
  }

  @override
  Future<String?> pickJsonFile() {
    final completer = Completer<String?>();
    final input = web.HTMLInputElement()
      ..type = 'file'
      ..accept = '.json,application/json';

    input.addEventListener(
      'change',
      (web.Event _) {
        final files = input.files;
        if (files == null || files.length == 0) {
          if (!completer.isCompleted) completer.complete(null);
          return;
        }
        final reader = web.FileReader();
        reader.addEventListener(
          'load',
          (web.Event _) {
            if (completer.isCompleted) return;
            final result = reader.result;
            completer.complete((result as JSString?)?.toDart);
          }.toJS,
        );
        reader.addEventListener(
          'error',
          (web.Event _) {
            if (!completer.isCompleted) completer.complete(null);
          }.toJS,
        );
        reader.readAsText(files.item(0)!);
      }.toJS,
    );

    // Note: a cancelled picker fires no event, so the future may never
    // complete — that is by design (the import flow simply doesn't proceed) and
    // is documented on [BundleFileIo.pickJsonFile].
    input.click();
    return completer.future;
  }
}
