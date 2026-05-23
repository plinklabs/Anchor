import 'package:flutter/foundation.dart';

import '../auth/msal_auth_service.dart';

class AuthTokenStore extends ChangeNotifier {
  String? _token;
  AccountInfo? _account;

  String? get token => _token;
  AccountInfo? get account => _account;
  bool get isAuthenticated => _account != null;

  void setSession({required String token, required AccountInfo account}) {
    _token = token;
    _account = account;
    notifyListeners();
  }

  void setToken(String? token) {
    if (_token == token) return;
    _token = token;
    notifyListeners();
  }

  void clear() {
    if (_token == null && _account == null) return;
    _token = null;
    _account = null;
    notifyListeners();
  }
}
