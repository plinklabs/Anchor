using FocusAgent.Core.Auth;

namespace FocusAgent.Core.Tests;

/// <summary>
/// Coverage for #271: a release agent pointed at an app registration without the
/// WAM broker redirect URI / public-client flows fails sign-in with
/// <c>WAM_provider_error_3399614473</c> (HRESULT 0xCAA20009). The raw code is
/// useless to a user, so <see cref="AuthFailureMessage"/> must translate the
/// <c>0xCAA2000x</c> broker-config band into an actionable message while leaving
/// other failures as the plain retry line.
/// </summary>
public class AuthFailureMessageTests
{
    // 0xCAA20009 — the exact code observed in #271.
    private const string Wam0xCAA20009 = "WAM_provider_error_3399614473";

    [Fact]
    public void BrokerConfigError_FromIssue271_IsTranslatedToActionableMessage()
    {
        var message = AuthFailureMessage.Describe(Wam0xCAA20009);

        Assert.Contains("broker", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redirect URI", message, StringComparison.OrdinalIgnoreCase);
        // The raw code is kept in parentheses for support/diagnostics.
        Assert.Contains(Wam0xCAA20009, message);
        // It must NOT be the generic "just try again" line — that's the bug.
        Assert.DoesNotContain("Click Sign in to try again", message);
    }

    [Theory]
    // Whole 0xCAA2000x band (low nibble is the sub-code): 0x...0 through 0x...F.
    [InlineData("WAM_provider_error_3399614464")] // 0xCAA20000
    [InlineData("WAM_provider_error_3399614473")] // 0xCAA20009
    [InlineData("WAM_provider_error_3399614479")] // 0xCAA2000F
    public void IsBrokerConfigError_True_AcrossTheBand(string code)
    {
        Assert.True(AuthFailureMessage.IsBrokerConfigError(code));
    }

    [Theory]
    [InlineData("WAM_provider_error_3399614480")] // 0xCAA20010 — outside 0x...0x
    [InlineData("WAM_provider_error_3399614463")] // 0xCAA1FFFF — below the band
    [InlineData("invalid_grant")]                  // ordinary MSAL error code
    [InlineData("WAM_provider_error_notanumber")]  // malformed suffix
    [InlineData("")]
    [InlineData(null)]
    public void IsBrokerConfigError_False_ForEverythingElse(string? code)
    {
        Assert.False(AuthFailureMessage.IsBrokerConfigError(code));
    }

    [Fact]
    public void OrdinaryMsalError_KeepsTheCodeAndRetryHint()
    {
        var message = AuthFailureMessage.Describe("invalid_grant");

        Assert.Contains("invalid_grant", message);
        Assert.Contains("Click Sign in to try again", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoErrorCode_FallsBackToGenericRetryLine(string? code)
    {
        Assert.Equal("Sign-in failed. Click Sign in to try again.", AuthFailureMessage.Describe(code));
    }
}
