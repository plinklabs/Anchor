using System.Diagnostics;
using System.Runtime.Versioning;
using FocusAgent.Core.Extension;
using Microsoft.Win32;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end proof for the command lines the agent hands the elevated
/// <c>reg.exe</c> (#405): the very argument strings
/// <see cref="EdgeExtensionPolicy.RegAddArguments"/> and
/// <see cref="EdgeExtensionPolicy.RegDeleteArguments"/> build, run through the real
/// system <c>reg.exe</c>, add exactly Anchor's value at the chosen slot and delete
/// exactly that value, leaving another product's entry alone.
///
/// The agent runs them elevated against HKLM through a UAC prompt, which no test may
/// trigger. So this spec runs them <em>unelevated</em> against a throwaway HKCU key
/// (the only part that differs is the key path the builders take). What stays
/// unverified here is the <c>runas</c> launch itself, the UAC prompt: that needs a
/// one-time manual check on a real laptop.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatedRegCommandTests : IDisposable
{
    private const string Other = "someotherextensionidaaaaaaaaaaaa;https://example.test/crx";

    private readonly string _root = $@"Software\AnchorTests\{Guid.NewGuid():N}";

    private string KeyPath => $@"{_root}\ExtensionInstallForcelist";

    private string RegKeyPath => $@"HKCU\{KeyPath}";

    [Fact]
    public async Task The_add_command_writes_our_entry_at_the_slot_and_the_delete_command_removes_only_it()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true))
            key.SetValue("1", Other, RegistryValueKind.String);

        var add = await RunRegAsync(EdgeExtensionPolicy.RegAddArguments("2", EdgeExtensionPolicy.ForcelistEntry, RegKeyPath));
        Assert.Equal(0, add);

        var afterAdd = ReadValues();
        Assert.Equal(2, afterAdd.Count);
        Assert.Equal(Other, afterAdd["1"]);
        Assert.Equal(EdgeExtensionPolicy.ForcelistEntry, afterAdd["2"]); // the exact string Edge consumes

        var delete = await RunRegAsync(EdgeExtensionPolicy.RegDeleteArguments("2", RegKeyPath));
        Assert.Equal(0, delete);

        var afterDelete = ReadValues();
        Assert.Equal(Other, Assert.Single(afterDelete).Value);
    }

    [Fact]
    public async Task The_add_command_creates_the_key_when_no_forcelist_exists_yet()
    {
        var add = await RunRegAsync(EdgeExtensionPolicy.RegAddArguments("1", EdgeExtensionPolicy.ForcelistEntry, RegKeyPath));

        Assert.Equal(0, add);
        Assert.Equal(EdgeExtensionPolicy.ForcelistEntry, Assert.Single(ReadValues()).Value);
    }

    private Dictionary<string, string> ReadValues()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        var values = new Dictionary<string, string>();
        if (key is null) return values;
        foreach (var name in key.GetValueNames())
            if (key.GetValue(name) is string s)
                values[name] = s;
        return values;
    }

    /// <summary>
    /// Run the system reg.exe with <paramref name="arguments"/> as one command-line
    /// string, the way ShellExecute passes it, and return its exit code.
    /// </summary>
    private static async Task<int> RunRegAsync(string arguments)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("reg.exe didn't start.");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(cts.Token);
        return process.ExitCode;
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false); } catch { }
    }
}
