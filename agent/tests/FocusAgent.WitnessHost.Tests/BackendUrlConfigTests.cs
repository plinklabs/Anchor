using System.Text.Json;
using FocusAgent.WitnessHost;

namespace FocusAgent.WitnessHost.Tests;

/// <summary>
/// The backend-URL hand-off the host gives the extension (#204): a single
/// published extension learns which backend to target from the on-box agent at
/// runtime. These lock the resolution precedence (env → file → dev fallback) and
/// the native-message shape the extension's witness.ts parses.
/// </summary>
public class BackendUrlConfigTests
{
    private static string? NoEnv(string _) => null;
    private static string? NoFile() => null;

    [Fact]
    public void Env_var_wins_over_everything()
    {
        var url = BackendUrlConfig.Resolve(
            getEnv: name => name == BackendUrlConfig.EnvVarName ? "https://env.example" : null,
            readConfigFile: () => "{\"backendUrl\":\"https://file.example\"}");

        Assert.Equal("https://env.example", url);
    }

    [Fact]
    public void Falls_back_to_the_config_file_when_no_env_var()
    {
        var url = BackendUrlConfig.Resolve(
            getEnv: NoEnv,
            readConfigFile: () => "{\"backendUrl\":\"https://file.example\"}");

        Assert.Equal("https://file.example", url);
    }

    [Fact]
    public void Falls_back_to_the_dev_default_when_nothing_is_configured()
    {
        var url = BackendUrlConfig.Resolve(NoEnv, NoFile);

        Assert.Equal(BackendUrlConfig.DevFallbackUrl, url);
        // The dev fallback is the local backend port, NOT a production default —
        // a real deployment must supply env/file (#204).
        Assert.Equal("http://localhost:5276", BackendUrlConfig.DevFallbackUrl);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void Blank_env_value_is_ignored(string envValue)
    {
        var url = BackendUrlConfig.Resolve(
            getEnv: _ => envValue,
            readConfigFile: () => "{\"backendUrl\":\"https://file.example\"}");

        Assert.Equal("https://file.example", url);
    }

    [Fact]
    public void Trailing_slash_is_trimmed()
    {
        var url = BackendUrlConfig.Resolve(
            getEnv: _ => "https://env.example/",
            readConfigFile: NoFile);

        Assert.Equal("https://env.example", url);
    }

    [Fact]
    public void Malformed_config_file_falls_through_to_the_dev_default()
    {
        var url = BackendUrlConfig.Resolve(
            getEnv: NoEnv,
            readConfigFile: () => "{ not json");

        Assert.Equal(BackendUrlConfig.DevFallbackUrl, url);
    }

    [Fact]
    public void BuildMessage_emits_the_shape_witness_ts_parses()
    {
        var json = BackendUrlConfig.BuildMessage("https://anchor.example");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("backend_url", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("https://anchor.example", doc.RootElement.GetProperty("url").GetString());
    }
}
