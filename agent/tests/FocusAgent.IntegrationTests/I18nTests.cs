using System.Diagnostics;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end proof for #323 (agent i18n, part of #320): the <em>real</em> built
/// agent, run in its dev-only <c>--verify-i18n &lt;lang&gt;</c> mode, resolves its
/// user-facing strings from the compiled <c>resources.pri</c> under a forced UI
/// language and correctly (a) renders Dutch for <c>nl-NL</c> and (b) falls back to
/// English for an unsupported language.
///
/// <para>
/// The mode exercises <em>both</em> localization paths against the shipped exe: the
/// window path (it parses a real <see cref="FocusAgent.App.Extension.GuidedInstallWindow"/>
/// and reads back the copy its constructor resolved — the composed surface, not a
/// raw lookup) and the direct code-lookup path (the <c>Loc</c> helper over an
/// explicit MRT resource context). A unit test can lock the catalogues in parity,
/// but only launching the real exe proves the .resw actually compiled into
/// resources.pri, that MRT resolves it, and that the en-US default is the fallback —
/// the wiring that a stubbed lookup can't.
/// </para>
///
/// <para>
/// The agent ships unpackaged, where the framework's own <c>x:Uid</c> resolution
/// can't be pointed at a chosen language (<c>PrimaryLanguageOverride</c> throws), so
/// the agent applies every string in code via <c>Loc</c> against an explicit
/// resource context — the single path this spec drives. No backend needed.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class I18nTests
{
    [Fact]
    public async Task English_is_the_source_language()
    {
        var strings = await RunVerifyI18nAsync("en-US");

        Assert.Equal("One quick step", strings["window.guided_headline"]);
        Assert.Equal("OPEN STORE", strings["window.guided_openstore"]);
        Assert.Equal("Open Anchor", strings["code.tray_open"]);
        Assert.Equal("Connected", strings["code.status_connected"]);
        Assert.Equal("Code not found.", strings["code.join_not_found"]);
    }

    [Fact]
    public async Task Dutch_renders_through_both_the_window_and_code_paths()
    {
        var strings = await RunVerifyI18nAsync("nl-NL");

        // Window path (real GuidedInstallWindow composition).
        Assert.Equal("Nog één stap", strings["window.guided_headline"]);
        Assert.Equal("STORE OPENEN", strings["window.guided_openstore"]);
        // Direct code-lookup path (Loc/MRT).
        Assert.Equal("Anchor openen", strings["code.tray_open"]);
        Assert.Equal("Verbonden", strings["code.status_connected"]);
        Assert.Equal("Code niet gevonden.", strings["code.join_not_found"]);
    }

    [Fact]
    public async Task The_paused_Edge_overlay_reads_in_English_and_Dutch()
    {
        // #427: the copy a student reads on the real overlay window when Edge is
        // paused because its extension is off — composed the way the controller
        // shows it, with a 60-second repair window.
        var en = await RunVerifyI18nAsync("en-US");
        Assert.Equal("Edge is paused for now", en["window.overlay_paused_headline"]);
        Assert.StartsWith("The Anchor extension in Edge is switched off", en["window.overlay_paused_message"]);
        Assert.Equal("Switch the extension back on", en["window.overlay_paused_action"]);
        Assert.Equal(
            "Edge opens for 60 seconds. Go to edge://extensions and switch Anchor on.",
            en["window.overlay_paused_hint"]);

        var nl = await RunVerifyI18nAsync("nl-NL");
        Assert.Equal("Edge staat even op pauze", nl["window.overlay_paused_headline"]);
        Assert.StartsWith("De Anchor-extensie in Edge staat uit", nl["window.overlay_paused_message"]);
        Assert.Equal("Zet de extensie weer aan", nl["window.overlay_paused_action"]);
        Assert.Equal(
            "Edge gaat 60 seconden open. Ga naar edge://extensions en zet Anchor aan.",
            nl["window.overlay_paused_hint"]);
    }

    [Fact]
    public async Task Unsupported_language_falls_back_to_English()
    {
        // fr-FR ships no catalogue, so MRT resolves the en-US default — never a
        // blank or a raw key.
        var strings = await RunVerifyI18nAsync("fr-FR");

        Assert.Equal("One quick step", strings["window.guided_headline"]);
        Assert.Equal("OPEN STORE", strings["window.guided_openstore"]);
        Assert.Equal("Open Anchor", strings["code.tray_open"]);
        Assert.Equal("Connected", strings["code.status_connected"]);
        Assert.Equal("Code not found.", strings["code.join_not_found"]);
    }

    /// <summary>
    /// Launch the real agent exe in <c>--verify-i18n &lt;lang&gt;</c> mode and read
    /// back the <c>key=value</c> strings it resolves for that language (the exe is a
    /// WinExe with no console, so the result comes via the result file, like
    /// --verify-ds-theme / --check-update). <paramref name="agentExe"/> defaults
    /// to the x64 Debug build; <c>PublishedPackageTests</c> passes the release
    /// publish instead (#403).
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, string>> RunVerifyI18nAsync(
        string language, string? agentExe = null)
    {
        agentExe ??= TestConfig.AgentExe;
        if (!File.Exists(agentExe))
            throw new FileNotFoundException(
                $"Agent exe not found at {agentExe}. Build it first: " +
                "dotnet build agent/src/FocusAgent.App/FocusAgent.App.csproj -p:Platform=x64 -c Debug",
                agentExe);

        var resultPath = Path.Combine(Path.GetTempPath(), $"anchor-i18n-{Guid.NewGuid():N}.txt");
        try
        {
            var psi = new ProcessStartInfo(agentExe) { UseShellExecute = false };
            psi.ArgumentList.Add("--verify-i18n");
            psi.ArgumentList.Add(language);
            psi.Environment["ANCHOR_I18N_RESULT_PATH"] = resultPath;

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start the agent process.");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort teardown */ }
                throw new TimeoutException($"Agent did not exit within 60s of --verify-i18n {language}.");
            }

            Assert.True(
                File.Exists(resultPath),
                $"Agent --verify-i18n {language} wrote no result file (exit {process.ExitCode}).");

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in await File.ReadAllLinesAsync(resultPath))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                map[line[..eq]] = line[(eq + 1)..];
            }

            Assert.False(
                map.ContainsKey("window.error"),
                $"Agent --verify-i18n {language} failed to build the probe window: " +
                (map.TryGetValue("window.error", out var err) ? err : "<unknown>"));
            Assert.False(
                map.ContainsKey("window.overlay_error"),
                $"Agent --verify-i18n {language} failed to compose the paused-Edge overlay: " +
                (map.TryGetValue("window.overlay_error", out var overlayErr) ? overlayErr : "<unknown>"));

            return map;
        }
        finally
        {
            try { File.Delete(resultPath); } catch { /* best-effort cleanup */ }
        }
    }
}
