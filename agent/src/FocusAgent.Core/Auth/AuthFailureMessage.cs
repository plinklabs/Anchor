using System.Globalization;

namespace FocusAgent.Core.Auth;

/// <summary>
/// Turns an MSAL error code into the human-readable line the sign-in screen
/// shows. Pure string logic (no MSAL dependency) so it lives in Core and is
/// unit-testable — the WinUI <c>ConnectionManager</c> just feeds it
/// <c>MsalException.ErrorCode</c>.
///
/// <para>
/// The one case worth special-handling is a WAM broker <em>configuration</em>
/// failure (#271). MSAL surfaces WAM provider errors as
/// <c>WAM_provider_error_&lt;decimal HRESULT&gt;</c>; the <c>0xCAA2000x</c>
/// family means the broker rejected the app registration itself — typically a
/// missing broker redirect URI (<c>ms-appx-web://Microsoft.AAD.BrokerPlugin/&lt;id&gt;</c>)
/// or "allow public client flows" not enabled. The raw
/// <c>WAM_provider_error_3399614473</c> tells the user nothing actionable, so we
/// translate it into a description that names the actual cause.
/// </para>
/// </summary>
public static class AuthFailureMessage
{
    private const string WamProviderErrorPrefix = "WAM_provider_error_";

    public static string Describe(string? msalErrorCode)
    {
        if (string.IsNullOrWhiteSpace(msalErrorCode))
            return "Sign-in failed. Click Sign in to try again.";

        if (IsBrokerConfigError(msalErrorCode))
        {
            return "Sign-in failed: this build's Microsoft sign-in is misconfigured " +
                   "for the Windows account broker — its app registration is missing " +
                   "the broker redirect URI or \"allow public client flows\". " +
                   "Reinstall from an official release or contact support. " +
                   $"({msalErrorCode})";
        }

        return $"Sign-in failed ({msalErrorCode}). Click Sign in to try again.";
    }

    /// <summary>
    /// True when the code is a WAM provider error in the <c>0xCAA2000x</c> band,
    /// i.e. the broker rejected the app-registration configuration (#271).
    /// </summary>
    public static bool IsBrokerConfigError(string? msalErrorCode)
    {
        if (string.IsNullOrWhiteSpace(msalErrorCode)) return false;
        if (!msalErrorCode.StartsWith(WamProviderErrorPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var hresultText = msalErrorCode[WamProviderErrorPrefix.Length..];
        if (!uint.TryParse(hresultText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hresult))
            return false;

        // 0xCAA2000x — mask off the low nibble (the specific sub-code, e.g.
        // ...0009 observed in #271) and compare against the family base.
        return (hresult & 0xFFFFFFF0u) == 0xCAA20000u;
    }
}
