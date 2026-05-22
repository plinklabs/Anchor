import 'package:http/http.dart' as http;

import 'auth_token_store.dart';

class _BearerTokenClient extends http.BaseClient {
  _BearerTokenClient(this._tokens, this._inner);

  final AuthTokenStore _tokens;
  final http.Client _inner;

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) {
    final token = _tokens.token;
    if (token != null && token.isNotEmpty) {
      request.headers['Authorization'] = 'Bearer $token';
    }
    return _inner.send(request);
  }

  @override
  void close() {
    _inner.close();
    super.close();
  }
}

class ApiClient {
  ApiClient({required Uri baseUrl, required AuthTokenStore tokens})
    : _baseUrl = baseUrl,
      _http = _BearerTokenClient(tokens, http.Client());

  final Uri _baseUrl;
  final http.Client _http;

  Future<http.Response> get(String path) =>
      _http.get(_baseUrl.resolve(path));

  Future<http.Response> post(String path, {Object? body, Map<String, String>? headers}) =>
      _http.post(_baseUrl.resolve(path), body: body, headers: headers);

  void close() => _http.close();
}
