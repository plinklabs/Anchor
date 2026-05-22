import 'package:flutter/foundation.dart';

class AuthTokenStore extends ChangeNotifier {
  String? _token;

  String? get token => _token;
  bool get isAuthenticated => _token != null;

  void setToken(String? token) {
    if (_token == token) return;
    _token = token;
    notifyListeners();
  }

  void clear() => setToken(null);
}
