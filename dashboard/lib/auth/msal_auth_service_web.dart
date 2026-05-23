import 'dart:js_interop';
import 'dart:js_interop_unsafe';

import 'msal_auth_service.dart';
import 'msal_config.dart';

@JS()
external JSObject get window;

extension type _AnchorAuthJs._(JSObject _) implements JSObject {
  external JSPromise<JSAny?> init(_InitConfig config);
  external JSPromise<JSAny?> signIn();
  external JSPromise<JSAny?> signOut();
  external JSPromise<JSString> acquireToken();
  external JSAny? getAccount();
}

extension type _InitConfig._(JSObject _) implements JSObject {
  external factory _InitConfig({
    required String clientId,
    required String tenantId,
    required String apiScope,
  });
}

AccountInfo? _accountFromJs(JSAny? raw) {
  if (raw == null) return null;
  final obj = raw as JSObject;
  final homeAccountId = (obj['homeAccountId'] as JSString?)?.toDart ?? '';
  final username = (obj['username'] as JSString?)?.toDart ?? '';
  final name = (obj['name'] as JSString?)?.toDart ?? username;
  String? department;
  final claims = obj['idTokenClaims'];
  if (claims != null && claims.isA<JSObject>()) {
    final claimsObj = claims as JSObject;
    department = (claimsObj['department'] as JSString?)?.toDart;
  }
  return AccountInfo(
    homeAccountId: homeAccountId,
    username: username,
    displayName: name,
    department: department,
  );
}

class MsalAuthServiceImpl implements MsalAuthService {
  MsalAuthServiceImpl(this.config);

  final MsalConfig config;
  bool _initialized = false;

  _AnchorAuthJs get _js {
    final raw = window['anchorAuth'];
    if (raw == null) {
      throw StateError(
        'anchorAuth JS shim missing — web/anchor_auth.js failed to load',
      );
    }
    return raw as _AnchorAuthJs;
  }

  @override
  Future<void> initialize() async {
    if (_initialized) return;
    await _js
        .init(_InitConfig(
          clientId: config.clientId,
          tenantId: config.tenantId,
          apiScope: config.apiScope,
        ))
        .toDart;
    _initialized = true;
  }

  @override
  Future<AccountInfo?> signIn() async {
    final result = await _js.signIn().toDart;
    return _accountFromJs(result);
  }

  @override
  Future<void> signOut() async {
    await _js.signOut().toDart;
  }

  @override
  Future<String> acquireToken() async {
    final result = await _js.acquireToken().toDart;
    return result.toDart;
  }

  @override
  AccountInfo? currentAccount() => _accountFromJs(_js.getAccount());
}
