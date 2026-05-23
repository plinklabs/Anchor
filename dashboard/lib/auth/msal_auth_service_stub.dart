import 'msal_auth_service.dart';
import 'msal_config.dart';

class MsalAuthServiceImpl implements MsalAuthService {
  MsalAuthServiceImpl(MsalConfig config);

  Never _unsupported() => throw UnsupportedError(
    'MsalAuthService is only available on the web build of the dashboard.',
  );

  @override
  Future<void> initialize() async => _unsupported();

  @override
  Future<AccountInfo?> signIn() async => _unsupported();

  @override
  Future<void> signOut() async => _unsupported();

  @override
  Future<String> acquireToken() async => _unsupported();

  @override
  AccountInfo? currentAccount() => null;
}
