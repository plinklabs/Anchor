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
    public async Task The_site_access_pause_reads_in_English_and_Dutch()
    {
        // #524: the copy a student reads on the real overlay window when Edge is
        // paused because the extension may not work on all sites, with a
        // 60-second repair window.
        var en = await RunVerifyI18nAsync("en-US");
        Assert.Equal("Anchor is not allowed to work on all sites", en["window.overlay_siteaccess_headline"]);
        Assert.StartsWith("Anchor's site access in Edge was changed", en["window.overlay_siteaccess_message"]);
        Assert.Equal("Open the setting", en["window.overlay_siteaccess_action"]);
        Assert.Equal(
            "Edge opens for 60 seconds on Anchor's page. Set Site access to \"On all sites\".",
            en["window.overlay_siteaccess_hint"]);

        var nl = await RunVerifyI18nAsync("nl-NL");
        Assert.Equal("Anchor mag niet op alle sites werken", nl["window.overlay_siteaccess_headline"]);
        Assert.StartsWith("De sitetoegang van Anchor in Edge is gewijzigd", nl["window.overlay_siteaccess_message"]);
        Assert.Equal("Open de instelling", nl["window.overlay_siteaccess_action"]);
        Assert.Equal(
            "Edge gaat 60 seconden open op de pagina van Anchor. Zet Sitetoegang op \"Op alle sites\".",
            nl["window.overlay_siteaccess_hint"]);
    }

    [Fact]
    public async Task The_school_profile_pauses_read_in_English_and_Dutch()
    {
        // #525: the copy a student reads on the real overlay window when no Edge
        // profile is signed in with the school account (naming it, with a 60-second
        // repair window), and when one Edge window is outside the school profile.
        // The message's line break reads " | " here.
        var en = await RunVerifyI18nAsync("en-US");
        Assert.Equal("Sign in to Edge with your school account", en["window.overlay_schoolaccount_headline"]);
        Assert.Equal(
            "You need to sign in to Edge with your school account: ada.lovelace@student.school.example | " +
            "Until then Edge can't be used in this session. You can carry on with one of these apps in the meantime.",
            en["window.overlay_schoolaccount_message"]);
        Assert.Equal("Open Edge to sign in", en["window.overlay_schoolaccount_action"]);
        Assert.Equal(
            "Edge opens for 60 seconds. Click your profile picture at the top of Edge and sign in with that account.",
            en["window.overlay_schoolaccount_hint"]);
        Assert.Equal("Work in your school profile", en["window.overlay_schoolprofile_headline"]);
        Assert.StartsWith("During the lesson you work in your school profile.", en["window.overlay_schoolprofile_message"]);
        Assert.Equal("Go to my school profile", en["window.overlay_schoolprofile_action"]);
        Assert.Equal("This window closes and Edge opens in your school profile.", en["window.overlay_schoolprofile_hint"]);

        var nl = await RunVerifyI18nAsync("nl-NL");
        Assert.Equal("Meld je in Edge aan met je schoolaccount", nl["window.overlay_schoolaccount_headline"]);
        Assert.Equal(
            "Je moet je in Edge aanmelden met je schoolaccount: ada.lovelace@student.school.example | " +
            "Tot dan kun je Edge in deze sessie niet gebruiken. Ga intussen verder met een van deze apps.",
            nl["window.overlay_schoolaccount_message"]);
        Assert.Equal("Open Edge om je aan te melden", nl["window.overlay_schoolaccount_action"]);
        Assert.Equal(
            "Edge gaat 60 seconden open. Klik bovenaan in Edge op je profielfoto en meld je aan met dat account.",
            nl["window.overlay_schoolaccount_hint"]);
        Assert.Equal("Werk in je schoolprofiel", nl["window.overlay_schoolprofile_headline"]);
        Assert.StartsWith("Tijdens de les werk je in je schoolprofiel.", nl["window.overlay_schoolprofile_message"]);
        Assert.Equal("Naar mijn schoolprofiel", nl["window.overlay_schoolprofile_action"]);
        Assert.Equal("Dit venster sluit en Edge opent in je schoolprofiel.", nl["window.overlay_schoolprofile_hint"]);
    }

    [Fact]
    public async Task The_pause_for_a_school_profile_without_the_extension_reads_in_English_and_Dutch()
    {
        // #536: the copy a student reads on the real overlay window when Edge is
        // signed in with the school account but Anchor was never added to that
        // profile, with a 60-second repair window. Not the sign-in copy, and no
        // account named: they are signed in already.
        var en = await RunVerifyI18nAsync("en-US");
        Assert.Equal("Add Anchor to your school profile", en["window.overlay_schoolextension_headline"]);
        Assert.Equal(
            "You're signed in to Edge with your school account, but Anchor still has to be added to your school " +
            "profile. Until then Edge can't be used in this session. You can carry on with one of these apps in the meantime.",
            en["window.overlay_schoolextension_message"]);
        Assert.Equal("Add Anchor", en["window.overlay_schoolextension_action"]);
        Assert.Equal(
            "Edge opens for 60 seconds at Anchor in the store, in your school profile. Click Get, then Add extension.",
            en["window.overlay_schoolextension_hint"]);

        var nl = await RunVerifyI18nAsync("nl-NL");
        Assert.Equal("Voeg Anchor toe aan je schoolprofiel", nl["window.overlay_schoolextension_headline"]);
        Assert.Equal(
            "Je bent in Edge aangemeld met je schoolaccount, maar Anchor moet nog aan je schoolprofiel worden " +
            "toegevoegd. Tot dan kun je Edge in deze sessie niet gebruiken. Ga intussen verder met een van deze apps.",
            nl["window.overlay_schoolextension_message"]);
        Assert.Equal("Anchor toevoegen", nl["window.overlay_schoolextension_action"]);
        Assert.Equal(
            "Edge gaat 60 seconden open bij Anchor in de store, in je schoolprofiel. Klik op Ophalen en daarna op Extensie toevoegen.",
            nl["window.overlay_schoolextension_hint"]);
    }

    [Fact]
    public async Task The_pause_for_an_extension_that_never_connected_reads_in_English_and_Dutch()
    {
        // #556: the copy a student reads on the real overlay window when the
        // extension is installed but never connected, with a 60-second repair
        // window. Not the "switched off" copy: the student switched nothing off.
        var en = await RunVerifyI18nAsync("en-US");
        Assert.Equal("Edge is paused for now", en["window.overlay_notconnected_headline"]);
        Assert.Equal(
            "The Anchor extension in Edge isn't connected to Anchor, so Edge can't be used in this session until it is. " +
            "Your teacher knows. You can carry on with one of these apps in the meantime.",
            en["window.overlay_notconnected_message"]);
        Assert.Equal("Try Edge again", en["window.overlay_notconnected_action"]);
        Assert.Equal(
            "Edge opens for 60 seconds. If Anchor connects in that time, Edge stays open.",
            en["window.overlay_notconnected_hint"]);

        var nl = await RunVerifyI18nAsync("nl-NL");
        Assert.Equal("Edge staat even op pauze", nl["window.overlay_notconnected_headline"]);
        Assert.Equal(
            "De Anchor-extensie in Edge is niet verbonden met Anchor, dus je kunt Edge in deze sessie pas weer gebruiken " +
            "als ze dat is. Je leerkracht weet het. Ga intussen verder met een van deze apps.",
            nl["window.overlay_notconnected_message"]);
        Assert.Equal("Probeer Edge opnieuw", nl["window.overlay_notconnected_action"]);
        Assert.Equal(
            "Edge gaat 60 seconden open. Maakt Anchor in die tijd verbinding, dan blijft Edge open.",
            nl["window.overlay_notconnected_hint"]);
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
