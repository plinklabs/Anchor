using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Asserting e2e for #525: during a session only the Edge profile signed in with
/// the student's school account may be used. A window of another profile, a Guest
/// window and an InPrivate window are each paused within a few seconds while the
/// school profile's window keeps working; the overlay's button closes such a
/// window; the teacher hears of one only when it is left open; and once the
/// session ends every window works again.
///
/// The real agent, backend and witness pipe, with <see cref="SchoolProfileRun"/>'s
/// stand-ins for Edge, which keep the run off the developer's own browser: a
/// <c>User Data</c> folder of the spec's own whose <c>Local State</c> says which
/// account each profile is signed in with (shaped as Edge 157 writes it); a fake
/// extension on a private pipe; and the spec's own windows as Edge's, each given
/// the AppUserModelID Edge gives that profile's windows (InPrivate: its profile's,
/// with <c>[InPrivate]</c> in the title; measured on Edge 157). The agent reads
/// those from the real windows.
///
/// The spec's windows are no allowed app, so they are opened without taking the
/// foreground: what this asserts comes from the agent's own scans and
/// <c>ShowWindow</c> calls, not from foreground events, which are the flaky part
/// on a headless desktop. The overlays are <see cref="SchoolProfileOverlayTests"/>'.
///
/// #537 adds the windows of sites installed as apps: each carries its package's
/// ID in every profile, so the run also stands in the packages (a folder of the
/// spec's own, each with an <c>AppxManifest.xml</c> shaped as Edge 157 writes it)
/// and lists the profiles each app is installed in in <c>Local State</c>.
/// </summary>
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class SchoolProfileTests
{
    private readonly BackendFixture _backend;
    public SchoolProfileTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task WindowsOutsideTheSchoolProfile_ArePaused_TheSchoolProfileKeepsWorking_AndAllWorkAgainAfterTheSession()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await SchoolProfileRun.StartAsync(_backend.Url, api, signedIn: true);
        await run.WaitForAsync(
            s => s.SchoolProfile is { Status: "Found", Profile: "Default" }, TimeSpan.FromSeconds(10),
            "the agent to find the school profile (Default)");

        // 1. The student has their school profile open, and opens a personal
        //    profile's window, a Guest window and an InPrivate window.
        var school = await run.OpenWindowAsync("Anchor e2e - School - Microsoft Edge", "MSEdge");
        var personal = await run.OpenWindowAsync("Anchor e2e - Personal - Microsoft Edge", "MSEdge.UserData.Profile1");
        var guest = await run.OpenWindowAsync("Anchor e2e - [Guest] - Microsoft Edge", "MSEdge.UserData.GuestProfile");
        var inPrivate = await run.OpenWindowAsync("Anchor e2e - [InPrivate] - Microsoft Edge", "MSEdge");

        // Each window outside the school profile is paused within a few seconds...
        foreach (var (window, what) in new[] { (personal, "personal profile"), (guest, "Guest"), (inPrivate, "InPrivate") })
            Assert.True(
                await BrowserPauseRun.WaitForIconicAsync(window.Handle, expected: true, TimeSpan.FromSeconds(6)),
                $"The {what} window was not paused (minimized) within a few seconds of opening.");
        var found = await run.WaitForAsync(
            s => (s.SchoolProfile?.OutsideWindows ?? []).Length == 3, TimeSpan.FromSeconds(5),
            "the agent to list the three windows outside the school profile");
        Assert.Equal(
            new[] { personal.Handle, guest.Handle, inPrivate.Handle }.Select(h => (long)h).Order(),
            found!.SchoolProfile!.OutsideWindows!.Order());
        // ...the school profile's keeps working, and Edge as a whole isn't paused.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(WindowCapture.IsIconic(school.Handle), "The school profile's window was paused.");
        Assert.Equal("Present", (await run.Agent.TryGetStatusAsync())?.ExtensionGuard?.State);

        // 2. A paused window the student brings back goes down again.
        WindowCapture.RestoreWithoutActivating(guest.Handle);
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(guest.Handle, expected: true, TimeSpan.FromSeconds(5)),
            "The Guest window stayed up after the student brought it back.");

        // 3. Left open past the report time: the teacher is told once, as a window
        //    outside the school profile, and of the InPrivate one by its own report
        //    (#148, #535), once; never as "switched off".
        Assert.True(
            await TamperReports.WaitForAsync(
                api, run.SessionId, "outside_school_profile", 1, SchoolProfileRun.Report + TimeSpan.FromSeconds(10)),
            "Windows outside the school profile stayed open past the report time, but the teacher was not told (once).");
        Assert.True(
            await TamperReports.WaitForAsync(
                api, run.SessionId, "inprivate_opened", 1, SchoolProfileRun.Report + TimeSpan.FromSeconds(10)),
            "The InPrivate window stayed open past the report time, but the teacher was not told (once).");
        Assert.Equal(0, await TamperReports.CountAsync(api, run.SessionId, "extension_disabled"));
        Assert.Equal(0, await TamperReports.CountAsync(api, run.SessionId, "school_profile_missing"));

        // 4. The overlay's button for the personal window closes it, as its close
        //    button does, and leaves the school profile's window be.
        Assert.True(await run.Agent.GoToSchoolProfileAsync(personal.Handle), "The agent refused to go to the school profile.");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (WindowCapture.IsWindow(personal.Handle) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.False(WindowCapture.IsWindow(personal.Handle), "The button did not close the personal profile's window.");
        Assert.True(WindowCapture.IsWindow(school.Handle), "The button closed the school profile's window.");

        // 5. The session ends: every paused window can be used again.
        await run.EndSessionAsync();
        await run.WaitForAsync(
            s => s.JoinedSessionId is null && s.SchoolProfile?.Status == "Unknown", TimeSpan.FromSeconds(10),
            "the session to end on the agent");
        foreach (var window in new[] { guest, inPrivate })
            WindowCapture.RestoreWithoutActivating(window.Handle);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(WindowCapture.IsIconic(guest.Handle), "The Guest window was paused after the session ended.");
        Assert.False(WindowCapture.IsIconic(inPrivate.Handle), "The InPrivate window was paused after the session ended.");
    }

    [Fact]
    public async Task AppWindowsOfAnotherProfile_ArePaused_TheSchoolProfilesOwnAppsKeepWorking_AndAllWorkAgainAfterTheSession()
    {
        // #537: sites installed as apps. Each app window carries its package's ID
        // in every profile (measured on Edge 157); the package names the app, and
        // Local State the profiles it is installed in.
        var api = new BackendClient(_backend.Url);
        await using var run = await SchoolProfileRun.StartAsync(_backend.Url, api, signedIn: true);
        await run.WaitForAsync(
            s => s.SchoolProfile is { Status: "Found", Profile: "Default" }, TimeSpan.FromSeconds(10),
            "the agent to find the school profile (Default)");

        // 1. Teams is installed as an app in the school profile, a game site in the
        //    personal one, and Outlook in both (the personal one first). The student
        //    has their school profile open and every app's window.
        var teams = run.InstallApp("localhost-AB457204_ebq0fnkrwaj2c", "abjckpmkhelphnmlbbjeehohmjbopiio", "Default");
        var games = run.InstallApp("localhost-15D39BB8_ts9w021fep8jm", "poiahfjabbbejmkahpdcpdgnnfmbakcf", "Profile 1");
        var outlook = run.InstallApp("localhost-5C0FFEE1_q1w2e3r4t5y6u", "faolnafnngnfdaknnbpnkhgohbobgegn", "Profile 1", "Default");
        // The school profile's windows are never handed the foreground by the others
        // going down: the spec's process plays Edge but isn't allowed as Edge is.
        var school = await run.OpenWindowAsync("Anchor e2e - School - Microsoft Edge", "MSEdge", neverActivated: true);
        var schoolTeams = await run.OpenWindowAsync("Anchor e2e Teams", teams, neverActivated: true);
        var personalGames = await run.OpenWindowAsync("Anchor e2e Games", games);
        // An app in two profiles: Edge ends each window's title with its profile's name.
        var schoolOutlook = await run.OpenWindowAsync("Anchor e2e Outlook - School", outlook, neverActivated: true);
        var personalOutlook = await run.OpenWindowAsync("Anchor e2e Outlook - Personal", outlook);

        // The personal profile's app windows are paused within a few seconds...
        foreach (var (window, what) in new[] { (personalGames, "personal game app"), (personalOutlook, "personal Outlook app") })
            Assert.True(
                await BrowserPauseRun.WaitForIconicAsync(window.Handle, expected: true, TimeSpan.FromSeconds(6)),
                $"The {what}'s window was not paused (minimized) within a few seconds of opening.");
        var found = await run.WaitForAsync(
            s => (s.SchoolProfile?.OutsideWindows ?? []).Length == 2, TimeSpan.FromSeconds(5),
            "the agent to list the two app windows outside the school profile");
        Assert.Equal(
            new[] { personalGames.Handle, personalOutlook.Handle }.Select(h => (long)h).Order(),
            found!.SchoolProfile!.OutsideWindows!.Order());
        // ...the school profile's own apps and window keep working.
        await Task.Delay(TimeSpan.FromSeconds(3));
        foreach (var (window, what) in new[] { (schoolTeams, "school Teams app"), (schoolOutlook, "school Outlook app"), (school, "school profile") })
            Assert.False(WindowCapture.IsIconic(window.Handle), $"The {what}'s window was paused.");
        Assert.Equal("Present", (await run.Agent.TryGetStatusAsync())?.ExtensionGuard?.State);

        // 2. Left open past the report time: the teacher is told once, as of windows
        //    outside the school profile.
        Assert.True(
            await TamperReports.WaitForAsync(
                api, run.SessionId, "outside_school_profile", 1, SchoolProfileRun.Report + TimeSpan.FromSeconds(10)),
            "App windows outside the school profile stayed open past the report time, but the teacher was not told (once).");

        // 3. The session ends: the paused app windows can be used again.
        await run.EndSessionAsync();
        await run.WaitForAsync(
            s => s.JoinedSessionId is null && s.SchoolProfile?.Status == "Unknown", TimeSpan.FromSeconds(10),
            "the session to end on the agent");
        foreach (var window in new[] { personalGames, personalOutlook })
            WindowCapture.RestoreWithoutActivating(window.Handle);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(WindowCapture.IsIconic(personalGames.Handle), "The personal game app's window was paused after the session ended.");
        Assert.False(WindowCapture.IsIconic(personalOutlook.Handle), "The personal Outlook app's window was paused after the session ended.");
    }

    [Fact]
    public async Task AnInPrivateWindowClosedInTime_TellsTheTeacherNothing_AndOneLeftOpenIsReportedOnce()
    {
        // #535: an InPrivate window is paused at once (#525), and the teacher hears
        // of it only when the student leaves it open past the report time, as of a
        // window of another profile. Long enough for the checks in between.
        var report = TimeSpan.FromSeconds(15);
        var api = new BackendClient(_backend.Url);
        await using var run = await SchoolProfileRun.StartAsync(_backend.Url, api, signedIn: true, report: report);
        await run.WaitForAsync(
            s => s.SchoolProfile is { Status: "Found", Profile: "Default" }, TimeSpan.FromSeconds(10),
            "the agent to find the school profile (Default)");
        var school = await run.OpenWindowAsync("Anchor e2e - School - Microsoft Edge", "MSEdge");

        // 1. The student opens an InPrivate window. The extension, allowed in
        //    InPrivate, says so the moment it opens. The window is paused, and the
        //    teacher isn't told.
        var first = await run.OpenWindowAsync("Anchor e2e - [InPrivate] - Microsoft Edge", "MSEdge");
        await run.Extension.SendAsync(new { type = "tamper", sessionId = run.SessionId, kind = "inprivate_opened" });
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(first.Handle, expected: true, TimeSpan.FromSeconds(6)),
            "The InPrivate window was not paused (minimized) within a few seconds of opening.");
        await run.WaitForAsync(s => s.InPrivateOpen == 1, TimeSpan.FromSeconds(5), "the agent to see the InPrivate window");
        Assert.Equal(0, await TamperReports.CountAsync(api, run.SessionId, "inprivate_opened"));

        // 2. The overlay's button closes it within the report time: the teacher
        //    hears nothing of it, not even after.
        Assert.True(await run.Agent.GoToSchoolProfileAsync(first.Handle), "The agent refused to go to the school profile.");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (WindowCapture.IsWindow(first.Handle) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.False(WindowCapture.IsWindow(first.Handle), "The button did not close the InPrivate window.");
        await run.WaitForAsync(s => s.InPrivateOpen == 0, TimeSpan.FromSeconds(5), "the agent to see the InPrivate window gone");
        await Task.Delay(report + TimeSpan.FromSeconds(3));
        Assert.Equal(0, await TamperReports.CountAsync(api, run.SessionId, "inprivate_opened"));

        // 3. Another one, left open: the teacher is told once, as InPrivate, never as
        //    a window outside the school profile, and the extension's word for it
        //    adds nothing.
        var second = await run.OpenWindowAsync("Anchor e2e - [InPrivate] - Microsoft Edge", "MSEdge");
        await run.Extension.SendAsync(new { type = "tamper", sessionId = run.SessionId, kind = "inprivate_opened" });
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(second.Handle, expected: true, TimeSpan.FromSeconds(6)),
            "The second InPrivate window was not paused (minimized) within a few seconds of opening.");
        Assert.True(
            await TamperReports.WaitForAsync(api, run.SessionId, "inprivate_opened", 1, report + TimeSpan.FromSeconds(10)),
            "The InPrivate window stayed open past the report time, but the teacher was not told (once).");
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(1, await TamperReports.CountAsync(api, run.SessionId, "inprivate_opened"));
        Assert.Equal(0, await TamperReports.CountAsync(api, run.SessionId, "outside_school_profile"));
        Assert.Equal(1, (await run.Agent.TryGetStatusAsync())?.InPrivateDetections);
        Assert.True(WindowCapture.IsWindow(school.Handle), "The school profile's window was closed.");
    }

    [Fact]
    public async Task WithoutASchoolProfile_EdgeIsPaused_UntilTheStudentSignsIn_AndAnUnresolvedOneReachesTheTeacher()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await SchoolProfileRun.StartAsync(_backend.Url, api, signedIn: true);
        await run.WaitForAsync(
            s => s.SchoolProfile?.Status == "Found", TimeSpan.FromSeconds(10), "the agent to find the school profile");
        var school = await run.OpenWindowAsync("Anchor e2e - School - Microsoft Edge", "MSEdge");
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.False(WindowCapture.IsIconic(school.Handle), "The school profile's window was paused.");

        // 1. The student signs out of Edge: no profile is signed in with the school
        //    account any more. Edge as a whole is paused at once, its window too.
        run.WriteLocalState(signedIn: false);
        await run.WaitForAsync(
            s => s.ExtensionGuard is { State: "Paused", PauseReason: "NoSchoolProfile" }
                 && s.SchoolProfile?.Status == "Missing",
            TimeSpan.FromSeconds(6), "Edge to be paused for the missing school profile");
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(school.Handle, expected: true, TimeSpan.FromSeconds(5)),
            "Edge was paused for the missing school profile but its window was not minimized.");

        // 2. Not resolved in time: the teacher is told once, as a missing school
        //    profile, never as "switched off or removed".
        Assert.True(
            await TamperReports.WaitForAsync(
                api, run.SessionId, "school_profile_missing", 1, SchoolProfileRun.Report + TimeSpan.FromSeconds(10)),
            "No school profile past the report time, but the teacher was not told (once).");
        Assert.Equal(0, await TamperReports.CountAsync(api, run.SessionId, "extension_disabled"));

        // 3. The overlay's button opens Edge so the student can sign in...
        Assert.True(await run.Agent.TurnExtensionBackOnAsync(), "The agent refused to open Edge to sign in.");
        await run.WaitForAsync(
            s => s.ExtensionGuard is { State: "Repairing", PauseReason: "NoSchoolProfile" }, TimeSpan.FromSeconds(3),
            "Edge to open for the student to sign in");

        // ...and once they have, the pause is over for good.
        run.WriteLocalState(signedIn: true);
        await run.WaitForAsync(
            s => s.ExtensionGuard?.State == "Present" && s.SchoolProfile?.Status == "Found", TimeSpan.FromSeconds(6),
            "the pause to lift once the student signed in");
        await Task.Delay(SchoolProfileRun.Repair);
        Assert.Equal("Present", (await run.Agent.TryGetStatusAsync())?.ExtensionGuard?.State);
        Assert.Equal(1, await TamperReports.CountAsync(api, run.SessionId, "school_profile_missing"));
    }
}

/// <summary>
/// Asserting e2e for #536: the Edge profile signed in with the school account
/// doesn't have Anchor's extension (a laptop with the guided install, where the
/// student signed in to a profile the extension was never added to). Edge is
/// paused for that, not for signing in; the overlay's button opens the store
/// listing in that profile; adding the extension there lifts the pause; and the
/// teacher hears of it as such, or not at all when the student adds it in time.
/// The same stand-ins as <see cref="SchoolProfileTests"/>: the e2e's Edge is never
/// started, so what the button opened is read from /status.
/// </summary>
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class SchoolProfileWithoutExtensionTests
{
    private readonly BackendFixture _backend;
    public SchoolProfileWithoutExtensionTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task ASchoolProfileWithoutTheExtension_PausesEdgeToAddAnchor_TheButtonOpensTheStoreInIt_AndAnUnresolvedOneReachesTheTeacherAsSuch()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await SchoolProfileRun.StartAsync(
            _backend.Url, api, signedIn: true, schoolProfileHasExtension: false);

        // 1. The student is signed in to Edge with the school account, in a profile
        //    Anchor was never added to: Edge is paused for that, not for signing in.
        await run.WaitForAsync(
            s => s.ExtensionGuard is { State: "Paused", PauseReason: "SchoolProfileWithoutExtension" }
                 && s.SchoolProfile is { Status: "Missing", WithoutExtension: "Default" },
            TimeSpan.FromSeconds(10), "Edge to be paused for the school profile without the extension");

        // 2. Not resolved in time: the teacher is told once that Anchor isn't in the
        //    school profile; never that the student isn't signed in, nor "switched off".
        Assert.True(
            await TamperReports.WaitForAsync(
                api, run.SessionId, "school_profile_without_extension", 1,
                SchoolProfileRun.Report + TimeSpan.FromSeconds(10)),
            "The school profile stayed without the extension past the report time, but the teacher was not told (once).");
        Assert.Equal(0, await TamperReports.CountAsync(api, run.SessionId, "school_profile_missing"));
        Assert.Equal(0, await TamperReports.CountAsync(api, run.SessionId, "extension_disabled"));

        // 3. The overlay's button opens Edge for the repair window, at Anchor's
        //    store listing, in the profile signed in with the school account.
        Assert.True(await run.Agent.TurnExtensionBackOnAsync(), "The agent refused to open the store listing.");
        var opened = await run.WaitForAsync(
            s => s.ExtensionGuard is { State: "Repairing", PauseReason: "SchoolProfileWithoutExtension" }
                 && s.SchoolProfile?.LastOpened is not null,
            TimeSpan.FromSeconds(3), "Edge to open at the store listing in the school profile");
        Assert.Equal(
            new ProfileOpenedSnapshot("Default", FocusAgent.Core.Extension.EdgeExtensionPolicy.StoreListingUrl),
            opened!.SchoolProfile!.LastOpened);

        // 4. The student adds Anchor there: the pause is over for good.
        run.AddExtension("Default");
        await run.WaitForAsync(
            s => s.ExtensionGuard?.State == "Present" && s.SchoolProfile is { Status: "Found", Profile: "Default" },
            TimeSpan.FromSeconds(6), "the pause to lift once Anchor was added to the school profile");
        await Task.Delay(SchoolProfileRun.Repair);
        Assert.Equal("Present", (await run.Agent.TryGetStatusAsync())?.ExtensionGuard?.State);
        Assert.Equal(1, await TamperReports.CountAsync(api, run.SessionId, "school_profile_without_extension"));
    }

    [Fact]
    public async Task SigningInToAProfileWithoutTheExtension_ChangesWhatThePauseSays_AndAddingAnchorInTimeTellsTheTeacherNothing()
    {
        // A report time long enough for both steps to be taken in time.
        var report = TimeSpan.FromSeconds(20);
        var api = new BackendClient(_backend.Url);
        await using var run = await SchoolProfileRun.StartAsync(
            _backend.Url, api, signedIn: false, schoolProfileHasExtension: false, report: report);
        await run.WaitForAsync(
            s => s.ExtensionGuard is { State: "Paused", PauseReason: "NoSchoolProfile" }
                 && s.SchoolProfile is { Status: "Missing", WithoutExtension: null },
            TimeSpan.FromSeconds(10), "Edge to be paused for no profile signed in with the school account");
        var school = await run.OpenWindowAsync("Anchor e2e - School - Microsoft Edge", "MSEdge");
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.False(WindowCapture.IsIconic(school.Handle), "The Edge window was taken down before anything changed.");

        // 1. The student signs in to Edge with the school account, in a profile
        //    without Anchor: the pause now says to add Anchor, and takes Edge down
        //    again to say so.
        run.WriteLocalState(signedIn: true);
        await run.WaitForAsync(
            s => s.ExtensionGuard is { State: "Paused", PauseReason: "SchoolProfileWithoutExtension" }
                 && s.SchoolProfile?.WithoutExtension == "Default",
            TimeSpan.FromSeconds(6), "the pause to say Anchor still has to be added to the school profile");
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(school.Handle, expected: true, TimeSpan.FromSeconds(5)),
            "Edge was paused anew for the school profile without the extension, but its window was not minimized.");

        // 2. They add Anchor to it in time: the pause is over, and the teacher hears
        //    of neither step.
        run.AddExtension("Default");
        await run.WaitForAsync(
            s => s.ExtensionGuard?.State == "Present" && s.SchoolProfile is { Status: "Found", Profile: "Default" },
            TimeSpan.FromSeconds(6), "the pause to lift once Anchor was added to the school profile");
        await Task.Delay(report + TimeSpan.FromSeconds(3));
        foreach (var kind in new[] { "school_profile_without_extension", "school_profile_missing", "extension_disabled" })
            Assert.Equal(0, await TamperReports.CountAsync(api, run.SessionId, kind));
    }
}

/// <summary>
/// #525, what the student sees: a window outside the school profile that comes
/// forward gets the school-profile overlay ("Work in your school profile"), with
/// no profile signed in with the school account at all Edge gets the overlay that
/// names the account, and (#536) with one that lacks the extension Edge gets the
/// overlay that says to add Anchor to it; either way the overlay has the keyboard. Their copy in
/// both languages is <c>I18nTests</c>' and their rendering <c>OverlayVisualTests</c>';
/// this is the live rule bringing them up. Depends on the real foreground, so it
/// runs in the non-blocking Visual lane, like <see cref="ExtensionSiteAccessOverlayTests"/>.
/// </summary>
[Trait("Category", "Visual")]
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class SchoolProfileOverlayTests
{
    private readonly BackendFixture _backend;
    public SchoolProfileOverlayTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task AWindowOfAnotherProfileComingForward_GetsTheSchoolProfileOverlay_WithTheKeyboard()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await SchoolProfileRun.StartAsync(_backend.Url, api, signedIn: true);
        await run.WaitForAsync(
            s => s.SchoolProfile?.Status == "Found", TimeSpan.FromSeconds(10), "the agent to find the school profile");

        // Opened in the background, so it carries its profile's ID before it comes
        // forward; then the student clicks it. It goes down the moment it has the
        // foreground, so one attempt (#441).
        var personal = await run.OpenWindowAsync("Anchor e2e - Personal - Microsoft Edge", "MSEdge.UserData.Profile1");
        Assert.True(WindowCapture.ActivateOnce(personal.Handle), "The personal window could not be brought forward.");

        await AssertOverlayAsync(run, personal.Handle, "SchoolProfilePaused");

        // Its button closes the window.
        Assert.True(await run.Agent.GoToSchoolProfileAsync(IntPtr.Zero), "The agent refused to go to the school profile.");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (WindowCapture.IsWindow(personal.Handle) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.False(WindowCapture.IsWindow(personal.Handle), "The overlay's button did not close the window it was shown for.");
    }

    [Fact]
    public async Task AnAppWindowOfAnotherProfileComingForward_GetsTheSchoolProfileOverlay_WithTheKeyboard()
    {
        // #537: a site installed as an app in the personal profile.
        var api = new BackendClient(_backend.Url);
        await using var run = await SchoolProfileRun.StartAsync(_backend.Url, api, signedIn: true);
        await run.WaitForAsync(
            s => s.SchoolProfile?.Status == "Found", TimeSpan.FromSeconds(10), "the agent to find the school profile");
        var games = run.InstallApp("localhost-15D39BB8_ts9w021fep8jm", "poiahfjabbbejmkahpdcpdgnnfmbakcf", "Profile 1");

        var app = await run.OpenWindowAsync("Anchor e2e Games", games);
        Assert.True(WindowCapture.ActivateOnce(app.Handle), "The app window could not be brought forward.");

        await AssertOverlayAsync(run, app.Handle, "SchoolProfilePaused");

        // Its button closes the app window.
        Assert.True(await run.Agent.GoToSchoolProfileAsync(IntPtr.Zero), "The agent refused to go to the school profile.");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (WindowCapture.IsWindow(app.Handle) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.False(WindowCapture.IsWindow(app.Handle), "The overlay's button did not close the app window it was shown for.");
    }

    [Fact]
    public async Task WithoutASchoolProfile_EdgeComingForward_GetsTheOverlayThatNamesTheAccount_WithTheKeyboard()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await SchoolProfileRun.StartAsync(_backend.Url, api, signedIn: false);
        await run.WaitForAsync(
            s => s.ExtensionGuard is { State: "Paused", PauseReason: "NoSchoolProfile" }, TimeSpan.FromSeconds(10),
            "Edge to be paused for the missing school profile");

        var window = await run.OpenWindowAsync("Anchor e2e - Personal - Microsoft Edge", "MSEdge");
        Assert.True(WindowCapture.ActivateOnce(window.Handle), "The Edge window could not be brought forward.");

        await AssertOverlayAsync(run, window.Handle, "SchoolAccountPaused");
    }

    [Fact]
    public async Task ASchoolProfileWithoutTheExtension_EdgeComingForward_GetsTheOverlayToAddAnchor_WithTheKeyboard()
    {
        // #536: signed in with the school account, in a profile without Anchor.
        var api = new BackendClient(_backend.Url);
        await using var run = await SchoolProfileRun.StartAsync(
            _backend.Url, api, signedIn: true, schoolProfileHasExtension: false);
        await run.WaitForAsync(
            s => s.ExtensionGuard is { State: "Paused", PauseReason: "SchoolProfileWithoutExtension" },
            TimeSpan.FromSeconds(10), "Edge to be paused for the school profile without the extension");

        var window = await run.OpenWindowAsync("Anchor e2e - School - Microsoft Edge", "MSEdge");
        Assert.True(WindowCapture.ActivateOnce(window.Handle), "The Edge window could not be brought forward.");

        await AssertOverlayAsync(run, window.Handle, "SchoolExtensionPaused");
    }

    private static async Task AssertOverlayAsync(SchoolProfileRun run, IntPtr window, string expected)
    {
        var pid = run.Agent.Pid;
        var overlay = IntPtr.Zero;
        StatusSnapshot? status = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            overlay = WindowCapture.FindFullScreenWindow(pid);
            status = await run.Agent.TryGetStatusAsync();
            if (WindowCapture.IsIconic(window) && overlay != IntPtr.Zero
                && WindowCapture.ForegroundWindow() == overlay && status?.Overlay == expected)
                break;
            await Task.Delay(100);
        }

        Assert.True(WindowCapture.IsIconic(window), "The paused Edge window was not taken down.");
        Assert.True(
            status?.Overlay == expected,
            $"The overlay did not show {expected} (showing: {status?.Overlay ?? "<nothing>"}). " +
            "An allowed app that takes the foreground hides it: run again without touching the desktop.");
        Assert.True(
            overlay != IntPtr.Zero && WindowCapture.ForegroundWindow() == overlay,
            $"The overlay does not have the keyboard (foreground: " +
            $"{WindowCapture.GetProcessName(WindowCapture.ForegroundWindow())}).");
    }
}

/// <summary>
/// One run of the school-profile rule (#525): the real agent with a fake
/// extension connected, a <c>User Data</c> folder of the spec's own in which the
/// Default profile is the school profile (signed in with the impersonated
/// student's account, by its object ID) and Profile 1 a personal one, both with
/// the extension on, and a joined session. The spec's own windows play Edge's.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SchoolProfileRun : IAsyncDisposable
{
    public const string SchoolAddress = "student@school.test";
    /// <summary>How long a window outside the school profile, or no school profile, may last before the teacher is told.</summary>
    public static readonly TimeSpan Report = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Repair = TimeSpan.FromSeconds(8);
    private const string StoreExtensionId = "dnkimhodjfogjibnbbfdjdapgmmiojio";

    private readonly BackendClient _api;
    private readonly string _root;
    private readonly List<OffListWindow> _windows = new();
    // Per installed site's app (#537): the profiles it is installed in, for Local State.
    private readonly Dictionary<string, string[]> _appProfiles = new();
    private bool _signedIn;
    private Guid? _sessionId;
    private AgentProcess? _agent;
    private FakeExtensionWitness? _extension;

    private SchoolProfileRun(BackendClient api, string root)
    {
        _api = api;
        _root = root;
    }

    public AgentProcess Agent => _agent ?? throw new InvalidOperationException("Agent not launched.");
    /// <summary>The extension's end of the witness pipe, to say what the extension says.</summary>
    public FakeExtensionWitness Extension => _extension ?? throw new InvalidOperationException("Extension not connected.");
    public Guid SessionId => _sessionId ?? throw new InvalidOperationException("No session started.");
    private string UserData => Path.Combine(_root, "User Data");
    /// <summary>The packages of the sites installed as apps (#537), one folder per package family, as Windows keeps them.</summary>
    private string Packages => Path.Combine(_root, "Packages");

    /// <param name="schoolProfileHasExtension">
    /// False: the Default profile never had the extension (#536), as a profile the
    /// student signed in to on a laptop with the guided install; Profile 1 still has
    /// it, and its extension is the one connected.
    /// </param>
    /// <param name="report">How long the teacher's reports wait; <see cref="Report"/> by default.</param>
    public static async Task<SchoolProfileRun> StartAsync(
        string backendUrl, BackendClient api, bool signedIn, bool schoolProfileHasExtension = true,
        TimeSpan? report = null)
    {
        var stale = await api.GetRejoinableSessionIdsAsync();
        Assert.True(
            stale.Count == 0,
            $"The student is still in running session(s) {string.Join(", ", stale)} that an earlier spec left " +
            "behind; this agent would rejoin them on connect. End every session a spec starts.");

        var run = new SchoolProfileRun(api, Path.Combine(Path.GetTempPath(), "anchor-e2e-edge-" + Guid.NewGuid().ToString("N")));
        try
        {
            run.WritePreferences("Default", withExtension: schoolProfileHasExtension);
            run.WritePreferences("Profile 1");
            run.WriteLocalState(signedIn);

            var pipeName = "anchor-witness-e2e-" + Guid.NewGuid().ToString("N");
            using var self = Process.GetCurrentProcess();
            run._agent = AgentProcess.Launch(
                backendUrl,
                TestConfig.StudentOid,
                autoJoin: true,
                extraEnvironment: new Dictionary<string, string>
                {
                    ["Witness__PipeName"] = pipeName,
                    // The spec's own windows play Edge's.
                    ["Witness__BrowserProcessName"] = self.ProcessName,
                    ["Witness__BrowserUserDataFolder"] = run.UserData,
                    // And its own packages those of the sites installed as apps (#537).
                    ["Witness__BrowserAppPackagesFolder"] = run.Packages,
                    // The address WAM would give with the account; the object ID is
                    // the impersonated student's.
                    ["Dev__ImpersonateUsername"] = SchoolAddress,
                    ["Session__SchoolProfileScanSeconds"] = "1",
                    ["Session__SchoolProfileReportSeconds"] = ((int)(report ?? Report).TotalSeconds).ToString(),
                    // The InPrivate witness (#148) looks as often, and reports an
                    // InPrivate window after the same report time (#535).
                    ["Session__InPrivateScanIntervalSeconds"] = "1",
                    ["Session__ExtensionRepairSeconds"] = ((int)Repair.TotalSeconds).ToString(),
                });
            await run.Agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

            // The extension is installed and running before the lesson starts.
            run._extension = new FakeExtensionWitness(pipeName);
            await run._extension.ConnectAsync();
            await run.WaitForAsync(s => s.ExtensionGuard?.WitnessConnected == true, TimeSpan.FromSeconds(10), "the witness to connect");

            var classId = await api.FindClassIdAsync();
            var session = await api.StartSessionAsync(classId);
            run._sessionId = session.Id;
            await run.WaitForAsync(
                s => s.JoinedSessionId == session.Id && s.ExtensionGuard?.State is "Present" or "Paused",
                TimeSpan.FromSeconds(10), "the session to be joined with the extension present");
            return run;
        }
        catch
        {
            await run.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Opens one of "Edge's" windows: the spec's own, without taking the
    /// foreground unless <paramref name="activate"/>, carrying
    /// <paramref name="appId"/> as Edge gives each profile's windows.
    /// </summary>
    /// <param name="neverActivated">
    /// Windows doesn't hand it the foreground when a window above it is minimized
    /// (<c>WS_EX_NOACTIVATE</c>). For a window that must stay up while others are
    /// paused around it: Edge is an allowed app in a session, but the spec's process
    /// that plays it isn't, and a stand-in that ends up with the foreground that
    /// way is taken down as an off-list app, which Edge never is.
    /// </param>
    public async Task<OffListWindow> OpenWindowAsync(
        string title, string appId, bool activate = false, bool neverActivated = false)
    {
        var window = await OffListWindow.OpenAsync(title, TimeSpan.FromSeconds(10), activate: activate);
        _windows.Add(window);
        if (neverActivated)
            SetWindowLongPtrW(window.Handle, GwlExStyle, GetWindowLongPtrW(window.Handle, GwlExStyle) | WsExNoActivate);
        WindowAppId.Set(window.Handle, appId);
        return window;
    }

    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x08000000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(IntPtr hwnd, int index, nint value);

    /// <summary>
    /// Edge's <c>Local State</c>: the Default profile signed in with the school
    /// account (or with nobody), Profile 1 with a personal account. As Edge
    /// replaces it: in one move.
    /// </summary>
    public void WriteLocalState(bool signedIn)
    {
        _signedIn = signedIn;
        var cache = new JsonObject
        {
            ["Default"] = new JsonObject
            {
                ["name"] = "School",
                ["user_name"] = signedIn ? SchoolAddress : "",
                ["edge_account_oid"] = signedIn ? TestConfig.StudentOid : "",
                ["edge_account_type"] = signedIn ? 2 : 0,
            },
            ["Profile 1"] = new JsonObject
            {
                ["name"] = "Personal",
                ["user_name"] = "someone@outlook.test",
                ["edge_account_oid"] = "",
                ["edge_account_type"] = 1,
            },
        };
        var localState = new JsonObject { ["profile"] = new JsonObject { ["info_cache"] = cache } };
        // The profiles each site installed as an app is installed in (#537).
        if (_appProfiles.Count > 0)
        {
            var shims = new JsonObject();
            foreach (var (appId, profiles) in _appProfiles)
                shims[appId] = new JsonObject
                {
                    ["installed_profiles"] = new JsonArray(profiles.Select(p => (JsonNode?)p).ToArray()),
                    ["last_active_profiles"] = new JsonArray((JsonNode?)profiles[^1]),
                };
            localState["app_shims"] = shims;
        }
        Directory.CreateDirectory(UserData);
        var temp = Path.Combine(UserData, "Local State.tmp");
        File.WriteAllText(temp, localState.ToJsonString());
        File.Move(temp, Path.Combine(UserData, "Local State"), overwrite: true);
    }

    /// <summary>
    /// The student installs a site as an app in <paramref name="profile"/> (and
    /// then in <paramref name="alsoIn"/>), as Edge 157 does it (#537): a package of
    /// its own (<paramref name="family"/>) whose manifest names the app, this
    /// <c>User Data</c> folder and the first profile, and Local State listing every
    /// profile it is in. Returns the AppUserModelID its windows carry in all of them.
    /// </summary>
    public string InstallApp(string family, string appId, string profile, params string[] alsoIn)
    {
        var folder = Path.Combine(Packages, family);
        Directory.CreateDirectory(folder);
        var parameters =
            $"--app-id={appId} --ip-edge-aumid=Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!MSEDGE --user-data-dir=&quot;{UserData}&quot;";
        File.WriteAllText(Path.Combine(folder, "AppxManifest.xml"), $"""
            <?xml version="1.0"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3" xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" IgnorableNamespaces="uap uap3 rescap">
             <Identity Name="{family[..family.IndexOf('_')]}" Publisher="CN=anchor-e2e.test, OID.2.25.311729368913984317654407730594956997722=1" Version="1.0.0.0" ProcessorArchitecture="neutral"/>
             <Properties><DisplayName>Anchor e2e app</DisplayName><PublisherDisplayName>anchor-e2e.test</PublisherDisplayName><Logo>Images\StoreLogo.png</Logo></Properties>
             <Dependencies>
              <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.19041.0" MaxVersionTested="10.0.19041.0"/>
              <uap10:HostRuntimeDependency Name="Microsoft.MicrosoftEdge.Stable" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" MinVersion="1.0.0.0"/>
             </Dependencies>
             <Resources><Resource Language="EN-US"/></Resources>
             <Applications>
              <Application Id="App" uap10:HostId="PWA" uap10:Parameters="{parameters}">
               <uap:VisualElements DisplayName="Anchor e2e app" Description="Anchor e2e app" BackgroundColor="transparent" Square150x150Logo="Images\Square150x150Logo.png" Square44x44Logo="Images\Square44x44Logo.png"/>
               <Extensions>
                <uap3:Extension Category="windows.appExtension">
                 <uap3:AppExtension Name="com.ms.webapp.internals.4" Id="{family[..family.IndexOf('_')]}" PublicFolder="Public" DisplayName="Edge WebApp Internals" Description="parameters?{parameters};profile-directory?{profile};start-url?https://anchor-e2e.test/"/>
                </uap3:Extension>
               </Extensions>
              </Application>
             </Applications>
             <Capabilities><rescap:Capability Name="runFullTrust"/></Capabilities>
            </Package>
            """);
        _appProfiles[appId] = [profile, .. alsoIn];
        WriteLocalState(_signedIn);
        return family + "!App";
    }

    /// <summary>
    /// The student adds Anchor to <paramref name="profile"/> from the store (#536):
    /// Edge writes it to the profile's preferences.
    /// </summary>
    public void AddExtension(string profile = "Default") => WritePreferences(profile);

    private void WritePreferences(string profile, bool withExtension = true)
    {
        var folder = Path.Combine(UserData, profile);
        Directory.CreateDirectory(folder);
        var settings = new JsonObject();
        if (withExtension)
            settings[StoreExtensionId] = new JsonObject
            {
                ["location"] = 1,
                ["path"] = $"{StoreExtensionId}\\0.4.4_0",
                ["disable_reasons"] = new JsonArray(),
            };
        var preferences = new JsonObject
        {
            ["extensions"] = new JsonObject { ["settings"] = settings },
        };
        // As Edge replaces it: in one move.
        var temp = Path.Combine(folder, "Secure Preferences.tmp");
        File.WriteAllText(temp, preferences.ToJsonString());
        File.Move(temp, Path.Combine(folder, "Secure Preferences"), overwrite: true);
    }

    /// <summary>Poll /status until it matches, or fail naming what was awaited.</summary>
    public async Task<StatusSnapshot?> WaitForAsync(Func<StatusSnapshot, bool> predicate, TimeSpan timeout, string what)
    {
        var last = await Agent.WaitForAsync(predicate, timeout);
        Assert.True(
            last is not null && predicate(last),
            $"Timed out after {timeout.TotalSeconds:N0}s waiting for {what} " +
            $"(guard: {last?.ExtensionGuard?.State ?? "<none>"}/{last?.ExtensionGuard?.PauseReason ?? "<none>"}, " +
            $"school profile: {last?.SchoolProfile?.Status ?? "<none>"} ({last?.SchoolProfile?.Profile ?? "none"}, " +
            $"without the extension: {last?.SchoolProfile?.WithoutExtension ?? "none"}), " +
            $"outside: {(last?.SchoolProfile?.OutsideWindows ?? []).Length}).");
        return last;
    }

    public async Task EndSessionAsync()
    {
        if (_sessionId is { } id)
            await _api.EndSessionAsync(id);
    }

    public async ValueTask DisposeAsync()
    {
        if (_sessionId is { } id)
        {
            try { await _api.EndSessionAsync(id); } catch { /* best-effort; may have ended already */ }
        }
        if (_extension is not null)
            await _extension.DisposeAsync();
        if (_agent is not null)
            await _agent.DisposeAsync();
        foreach (var window in _windows)
            window.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}

/// <summary>The tamper reports of a session of one kind, as the teacher's dashboard gets them.</summary>
internal static class TamperReports
{
    public static async Task<int> CountAsync(BackendClient api, Guid sessionId, string kind) =>
        (await api.GetSessionEventsAsync(sessionId))
        .Count(e => e.Kind == "TamperDetected" && e.PayloadJson.Contains($"\"{kind}\""));

    /// <summary>Poll until the teacher has exactly <paramref name="expected"/> of them, or time out.</summary>
    public static async Task<bool> WaitForAsync(BackendClient api, Guid sessionId, string kind, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await CountAsync(api, sessionId, kind) == expected) return true;
            await Task.Delay(300);
        }
        return await CountAsync(api, sessionId, kind) == expected;
    }
}
