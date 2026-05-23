using FocusAgent.Core.Auth;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;

namespace FocusAgent.App.Auth;

public sealed class WamTokenProvider : IAuthTokenProvider, IAsyncDisposable
{
    private const string CacheFileName = "msal_cache.dat";

    private readonly AuthSettings _settings;
    private readonly ILogger<WamTokenProvider> _log;
    private readonly Func<IntPtr> _windowHandleProvider;
    private readonly SemaphoreSlim _sync = new(1, 1);

    private IPublicClientApplication? _app;
    private MsalCacheHelper? _cacheHelper;
    private AuthResult? _last;

    public WamTokenProvider(
        IOptions<AuthSettings> settings,
        Func<IntPtr> windowHandleProvider,
        ILogger<WamTokenProvider> log)
    {
        _settings = settings.Value;
        _windowHandleProvider = windowHandleProvider;
        _log = log;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var result = await AcquireTokenAsync(ct).ConfigureAwait(false);
        return result.AccessToken;
    }

    public async Task<AuthResult> AcquireTokenAsync(CancellationToken ct = default)
    {
        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var app = await GetOrCreateAppAsync(ct).ConfigureAwait(false);
            var scopes = new[] { _settings.Scope };

            var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
            var cachedAccount = accounts.FirstOrDefault();
            var account = cachedAccount ?? PublicClientApplication.OperatingSystemAccount;

            AuthenticationResult result;
            try
            {
                result = await app.AcquireTokenSilent(scopes, account)
                    .ExecuteAsync(ct)
                    .ConfigureAwait(false);
                _log.LogDebug("Acquired token silently for {Username}", result.Account?.Username);
            }
            catch (MsalUiRequiredException ex)
            {
                _log.LogInformation(ex, "Silent acquisition required UI; falling back to interactive WAM prompt");
                var interactive = app.AcquireTokenInteractive(scopes)
                    .WithParentActivityOrWindow(_windowHandleProvider());

                // Only pin the prompt to a specific account when we have a real cached MSAL account.
                // Otherwise let WAM show its picker so the user can sign in with a tenant account
                // that differs from the Windows-signed-in OS account (e.g. dev laptops on a personal MSA).
                if (cachedAccount is not null)
                {
                    interactive = interactive.WithAccount(cachedAccount);
                }
                else if (!string.IsNullOrWhiteSpace(_settings.LoginHint))
                {
                    interactive = interactive.WithLoginHint(_settings.LoginHint);
                }

                result = await interactive.ExecuteAsync(ct).ConfigureAwait(false);
                _log.LogInformation("Acquired token interactively for {Username}", result.Account?.Username);
            }

            var auth = new AuthResult(
                AccessToken: result.AccessToken,
                Username: result.Account?.Username ?? string.Empty,
                DisplayName: ExtractDisplayName(result),
                ExpiresOn: result.ExpiresOn);
            _last = auth;
            return auth;
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<IPublicClientApplication> GetOrCreateAppAsync(CancellationToken ct)
    {
        if (_app is not null)
            return _app;

        ValidateSettings();

        var builder = PublicClientApplicationBuilder
            .Create(_settings.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _settings.TenantId)
            .WithDefaultRedirectUri()
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows));

        _app = builder.Build();

        var cacheDir = MsalCacheDirectory();
        Directory.CreateDirectory(cacheDir);
        // Windows-only agent — cache is DPAPI-encrypted via MsalCacheHelper defaults.
        var storage = new StorageCreationPropertiesBuilder(CacheFileName, cacheDir).Build();

        _cacheHelper = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
        _cacheHelper.RegisterCache(_app.UserTokenCache);
        ct.ThrowIfCancellationRequested();
        return _app;
    }

    private void ValidateSettings()
    {
        var missing = new List<string>(3);
        if (string.IsNullOrWhiteSpace(_settings.TenantId)) missing.Add("Auth:TenantId");
        if (string.IsNullOrWhiteSpace(_settings.ClientId)) missing.Add("Auth:ClientId");
        if (string.IsNullOrWhiteSpace(_settings.Scope)) missing.Add("Auth:Scope");
        if (missing.Count == 0) return;

        throw new InvalidOperationException(
            $"Auth configuration is incomplete: {string.Join(", ", missing)} not set. " +
            "Populate these in appsettings.json or a local appsettings.Development.json " +
            "(see agent/README.md → Configuration).");
    }

    private static string ExtractDisplayName(AuthenticationResult result)
    {
        if (result.ClaimsPrincipal is not null)
        {
            var name = result.ClaimsPrincipal.FindFirst("name")?.Value;
            if (!string.IsNullOrWhiteSpace(name))
                return name!;
        }
        return result.Account?.Username ?? "";
    }

    public static string MsalCacheDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Anchor", "FocusAgent");

    public ValueTask DisposeAsync()
    {
        _sync.Dispose();
        return ValueTask.CompletedTask;
    }
}
