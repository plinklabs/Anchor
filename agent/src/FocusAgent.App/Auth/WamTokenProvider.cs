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
                result = await AcquireInteractiveAsync(app, scopes, cachedAccount, ct).ConfigureAwait(false);
                _log.LogInformation("Acquired token interactively for {Username}", result.Account?.Username);
            }

            var auth = new AuthResult(
                AccessToken: result.AccessToken,
                Username: result.Account?.Username ?? string.Empty,
                DisplayName: ExtractDisplayName(result),
                ExpiresOn: result.ExpiresOn,
                // #525: the Entra object ID an Edge profile signed in with the same
                // account carries.
                AccountId: result.UniqueId ?? result.Account?.HomeAccountId?.ObjectId);
            _last = auth;
            return auth;
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<AuthenticationResult> AcquireInteractiveAsync(
        IPublicClientApplication app,
        string[] scopes,
        IAccount? cachedAccount,
        CancellationToken ct)
    {
        // First attempt: pin to the cached/hinted account so a returning user
        // doesn't have to pick from the WAM list.
        try
        {
            return await BuildInteractive(app, scopes, pinAccount: true, cachedAccount)
                .ExecuteAsync(ct)
                .ConfigureAwait(false);
        }
        catch (MsalException ex) when (IsBrokerStateError(ex) && cachedAccount is not null)
        {
            // WAM rejected the pinned account (stale broker state, revoked
            // refresh token, account deleted from the broker, …). Wipe the
            // pin and let WAM show its account picker so the user can re-sign
            // in fresh — that's the actual recovery path.
            _log.LogWarning(ex,
                "WAM rejected the pinned account; retrying interactive without account pin so the picker can show.");
            return await BuildInteractive(app, scopes, pinAccount: false, cachedAccount: null)
                .ExecuteAsync(ct)
                .ConfigureAwait(false);
        }
    }

    private AcquireTokenInteractiveParameterBuilder BuildInteractive(
        IPublicClientApplication app,
        string[] scopes,
        bool pinAccount,
        IAccount? cachedAccount)
    {
        var interactive = app.AcquireTokenInteractive(scopes)
            .WithParentActivityOrWindow(_windowHandleProvider());

        if (pinAccount && cachedAccount is not null)
        {
            interactive = interactive.WithAccount(cachedAccount);
        }
        else if (pinAccount && !string.IsNullOrWhiteSpace(_settings.LoginHint))
        {
            interactive = interactive.WithLoginHint(_settings.LoginHint);
        }

        return interactive;
    }

    /// <summary>
    /// True when an MSAL/WAM exception indicates the broker itself failed for
    /// the pinned account (stale broker state, revoked refresh token, account
    /// removed from the broker, …), as opposed to the user cancelling the
    /// prompt. For these we re-prompt without pinning so the user can pick a
    /// fresh account.
    /// </summary>
    private static bool IsBrokerStateError(MsalException ex)
    {
        // ErrorCode "failed_to_acquire_token_silently_from_broker" leaks out
        // of *interactive* calls when the underlying WAM broker has stale
        // state for the pinned account — observed in #41.
        if (ex.ErrorCode == "failed_to_acquire_token_silently_from_broker") return true;
        // Generic broker / WAM errors. MsalClientException.ErrorCode for these
        // varies by broker version; the readable signal is the message prefix.
        if (ex.Message.Contains("WAM Error", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
            // Cap to Warning to avoid drowning the file logger in MSAL telemetry
            // (per-call there are dozens of Info lines: telemetry dumps, cache
            // partition counts, key-by-key reads, …). Warning+ catches the
            // signal we actually need — broker failures emit Warning/Error
            // with the surrounding context.
            .WithLogging(ForwardMsalLog, Microsoft.Identity.Client.LogLevel.Warning, enablePiiLogging: false, enableDefaultPlatformLogging: true);

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

    private void ForwardMsalLog(Microsoft.Identity.Client.LogLevel level, string message, bool containsPii)
    {
        // MSAL drops PII unless explicitly enabled (it isn't), so containsPii=true
        // lines here have already been redacted to "(pii)" by MSAL itself. We
        // forward everything at the matching log severity so broker failures
        // emit a readable trail next to MsalException.Message="Error Message: (pii)".
        var severity = level switch
        {
            Microsoft.Identity.Client.LogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
            Microsoft.Identity.Client.LogLevel.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
            Microsoft.Identity.Client.LogLevel.Info => Microsoft.Extensions.Logging.LogLevel.Information,
            _ => Microsoft.Extensions.Logging.LogLevel.Debug,
        };
        _log.Log(severity, "MSAL: {Message}", message);
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
