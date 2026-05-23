class MsalConfig {
  const MsalConfig({
    required this.tenantId,
    required this.clientId,
    required this.apiScope,
  });

  final String tenantId;
  final String clientId;
  final String apiScope;

  static const _defaultClientId = 'c9ba7c0e-763d-4a1b-9d95-894f54fb16da';
  static const _defaultTenantId = '8ee90830-e251-45a0-bf95-abdf72738b07';

  factory MsalConfig.fromEnvironment() {
    const tenantId = String.fromEnvironment(
      'ENTRA_TENANT_ID',
      defaultValue: _defaultTenantId,
    );
    const clientId = String.fromEnvironment(
      'ENTRA_CLIENT_ID',
      defaultValue: _defaultClientId,
    );
    // When the SPA and API share an app registration, Entra requires the
    // resource to be the GUID client id (no api:// prefix) — otherwise it
    // rejects with AADSTS90009 ("Application is requesting a token for
    // itself"). The backend accepts both audience forms.
    const apiScope = String.fromEnvironment(
      'API_SCOPE',
      defaultValue: '$_defaultClientId/.default',
    );
    return const MsalConfig(
      tenantId: tenantId,
      clientId: clientId,
      apiScope: apiScope,
    );
  }
}
