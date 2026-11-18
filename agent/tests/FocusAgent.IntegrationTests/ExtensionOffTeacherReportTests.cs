using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Asserting e2e for #518: what reaches the teacher's session when the extension's
/// witness link drops. "…Anchor extension was switched off or removed"
/// (<c>TamperDetected{extension_disabled}</c>) used to go out on every drop: an
/// option changed on the extension's page (Edge restarts it; back within a
/// second), an update, a reload, Edge being closed. It must go out only when the
/// extension is really gone while the browser is still open, after the grace
/// period, and once.
///
/// Drives the real agent against the real backend over the real witness pipe, on
/// <see cref="BrowserPauseRun"/>'s stand-ins (a fake extension on a private pipe,
/// Notepad as the browser), and reads what the teacher gets from the session's
/// events, which is what the dashboard's feed shows.
/// </summary>
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ExtensionOffTeacherReportTests
{
    private readonly BackendFixture _backend;
    public ExtensionOffTeacherReportTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task OnlyAnExtensionReallyGoneWithTheBrowserOpen_ReachesTheTeacher_AndOnce()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await BrowserPauseRun.StartAsync(_backend.Url, api);

        // 1. An option changed on the extension's page, three times, as in the
        //    report of #518: Edge restarts the extension and it is back within a
        //    second. The teacher must see nothing.
        foreach (var away in new[] { 1050, 10, 360 })
        {
            await run.Extension.DisconnectAsync();
            await Task.Delay(away);
            await run.Extension.ConnectAsync();
            await run.WaitForGuardAsync(
                g => g.WitnessConnected && g.State == "Present", TimeSpan.FromSeconds(5), "the extension to be back");
        }
        await Task.Delay(BrowserPauseRun.Grace + TimeSpan.FromSeconds(2));
        Assert.Equal(0, await ExtensionOffReports.CountAsync(api, run.SessionId));

        // 2. Switched off with the browser open: nothing on the drop itself, and
        //    one report once the grace period ran out and the browser is paused.
        await run.Extension.DisconnectAsync();
        await run.WaitForGuardAsync(g => !g.WitnessConnected, TimeSpan.FromSeconds(5), "the witness to drop");
        Assert.Equal(0, await ExtensionOffReports.CountAsync(api, run.SessionId));
        await run.WaitForGuardAsync(
            g => g.State == "Paused", BrowserPauseRun.Grace + TimeSpan.FromSeconds(5), "the browser to be paused");
        Assert.True(
            await ExtensionOffReports.WaitForAsync(api, run.SessionId, 1, TimeSpan.FromSeconds(10)),
            "The extension stayed off past the grace period with the browser open, but the teacher was not told.");

        // 3. The student opens the browser to repair it and doesn't: paused again,
        //    and the teacher is not told a second time.
        Assert.True(await run.Agent.TurnExtensionBackOnAsync(), "The agent refused to open the paused browser for repair.");
        await run.WaitForGuardAsync(g => g.State == "Repairing", TimeSpan.FromSeconds(3), "the repair window to open");
        WindowCapture.RestoreWindow(run.BrowserWindow);
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(run.BrowserWindow, expected: false, TimeSpan.FromSeconds(3)),
            "The browser window could not be restored during the repair window.");
        await run.WaitForGuardAsync(
            g => g.State == "Paused", BrowserPauseRun.Repair + TimeSpan.FromSeconds(5), "the pause to return after repair");
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await ExtensionOffReports.CountAsync(api, run.SessionId));

        // 4. Back on. Then the student closes the browser altogether: the link
        //    drops with no browser window left. Nothing is paused and the teacher
        //    sees nothing.
        await run.Extension.ConnectAsync();
        await run.WaitForGuardAsync(
            g => g.WitnessConnected && g.State == "Present", TimeSpan.FromSeconds(5), "the pause to lift");
        BrowserPauseRun.CloseBrowser();
        await run.Extension.DisconnectAsync();
        await run.WaitForGuardAsync(
            g => g.State == "Dormant", BrowserPauseRun.Grace + TimeSpan.FromSeconds(5),
            "the grace period to run out with the browser closed");
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await ExtensionOffReports.CountAsync(api, run.SessionId));
    }
}

/// <summary>
/// Asserting e2e for #518 with several Edge profiles: every profile runs its own
/// copy of the extension on its own link (#517). Closing one profile's last
/// window drops its link, and so does switching the extension off in a profile
/// that stays open; the links can't tell the two apart. The agent reads it from
/// Edge instead: which profiles have a window open (each browser window carries
/// its profile's AppUserModelID) and what the profile's preferences say about the
/// extension. A closed profile must tell the teacher nothing; an open profile
/// with the extension switched off must be reported once, and pause the browser,
/// while the other profile's extension is still connected.
///
/// The real agent, backend and witness pipe, with stand-ins for Edge that keep
/// the run off the developer's own browser: a <c>User Data</c> folder of the
/// spec's own with a <c>Secure Preferences</c> per profile, shaped as Edge 157
/// writes them; one fake extension per profile on a private pipe; and the spec's
/// own windows as the browser's, each given the AppUserModelID Edge gives that
/// profile's windows. The agent reads those IDs from the real windows.
/// </summary>
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ExtensionOffInOneProfileTests
{
    private const string StoreExtensionId = "dnkimhodjfogjibnbbfdjdapgmmiojio";
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

    private readonly BackendFixture _backend;
    public ExtensionOffInOneProfileTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task AClosedProfileTellsTheTeacherNothing_AndTheExtensionSwitchedOffInAnOpenOneIsReportedOnce()
    {
        var api = new BackendClient(_backend.Url);
        var stale = await api.GetRejoinableSessionIdsAsync();
        Assert.True(
            stale.Count == 0,
            $"The student is still in running session(s) {string.Join(", ", stale)} that an earlier spec left " +
            "behind; this agent would rejoin them on connect. End every session a spec starts.");

        var root = Path.Combine(Path.GetTempPath(), "anchor-e2e-edge-" + Guid.NewGuid().ToString("N"));
        var userData = Path.Combine(root, "User Data");
        foreach (var profile in new[] { "Default", "Profile 1", "Profile 2" })
            WritePreferences(userData, profile, switchedOff: false);

        var pipeName = "anchor-witness-e2e-" + Guid.NewGuid().ToString("N");
        using var self = Process.GetCurrentProcess();
        await using var agent = AgentProcess.Launch(
            _backend.Url,
            TestConfig.StudentOid,
            autoJoin: true,
            extraEnvironment: new Dictionary<string, string>
            {
                ["Witness__PipeName"] = pipeName,
                // The spec's own windows play Edge's.
                ["Witness__BrowserProcessName"] = self.ProcessName,
                ["Witness__BrowserUserDataFolder"] = userData,
                ["Session__ExtensionGraceSeconds"] = ((int)Grace.TotalSeconds).ToString(),
                ["Session__ExtensionProfileScanSeconds"] = "1",
            });
        await using var defaultProfile = new FakeExtensionWitness(pipeName);
        await using var profile1 = new FakeExtensionWitness(pipeName);
        await using var profile2 = new FakeExtensionWitness(pipeName);
        var windows = new List<OffListWindow>();
        Guid? sessionId = null;
        try
        {
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

            // Three profiles open, each with a window and its extension running.
            foreach (var (profile, appId) in new[]
                     {
                         ("Default", "MSEdge"),
                         ("Profile 1", "MSEdge.UserData.Profile1"),
                         ("Profile 2", "MSEdge.UserData.Profile2"),
                     })
            {
                var window = await OffListWindow.OpenAsync(
                    $"Anchor e2e Edge ({profile})", TimeSpan.FromSeconds(10), activate: false);
                windows.Add(window);
                WindowAppId.Set(window.Handle, appId);
            }
            await defaultProfile.ConnectAsync();
            await profile1.ConnectAsync();
            await profile2.ConnectAsync();
            await WaitForGuardAsync(agent, g => g.WitnessConnections == 3, TimeSpan.FromSeconds(10), "three profiles to connect");

            var classId = await api.FindClassIdAsync();
            var session = await api.StartSessionAsync(classId);
            sessionId = session.Id;
            await WaitForGuardAsync(
                agent, g => g.State == "Present", TimeSpan.FromSeconds(10), "the session to be joined with the extension present");

            // 1. The student closes Profile 2's last window: Edge ends that profile
            //    and its link drops. Its preferences still have the extension on.
            windows[2].Dispose();
            await profile2.DisconnectAsync();
            await WaitForGuardAsync(agent, g => g.WitnessConnections == 2, TimeSpan.FromSeconds(5), "Profile 2's link to drop");
            // Longer than the grace period, the scan and the time the agent allows
            // for Edge to write a change down.
            await Task.Delay(TimeSpan.FromSeconds(16));
            var afterClose = (await agent.TryGetStatusAsync())?.ExtensionGuard;
            Assert.True(
                afterClose is { State: "Present" } && (afterClose.ProfilesWithExtensionOff ?? []).Length == 0,
                $"Closing a profile took the guard out of Present: {afterClose}");
            Assert.Equal(0, await ExtensionOffReports.CountAsync(api, session.Id));

            // 2. The student switches the extension off in Profile 1, whose window
            //    stays open: its link drops just the same, the Default profile's
            //    stays up, and Edge writes the change to the profile's preferences.
            await profile1.DisconnectAsync();
            WritePreferences(userData, "Profile 1", switchedOff: true);
            await WaitForGuardAsync(
                agent, g => g.State == "Paused", TimeSpan.FromSeconds(15),
                "the browser to be paused for the profile with the extension off");
            var paused = (await agent.TryGetStatusAsync())?.ExtensionGuard;
            Assert.Equal(new[] { "Profile 1" }, paused?.ProfilesWithExtensionOff);
            Assert.True(paused?.WitnessConnected, "The Default profile's extension should still be connected.");
            Assert.True(
                await ExtensionOffReports.WaitForAsync(api, session.Id, 1, TimeSpan.FromSeconds(10)),
                "The extension was switched off in a profile that is still open, but the teacher was not told.");

            // It stays off: still paused, and the teacher was told once.
            await Task.Delay(TimeSpan.FromSeconds(4));
            Assert.Equal("Paused", (await agent.TryGetStatusAsync())?.ExtensionGuard?.State);
            Assert.Equal(1, await ExtensionOffReports.CountAsync(api, session.Id));

            // 3. Switched back on there: its link is back and the pause lifts.
            await profile1.ConnectAsync();
            WritePreferences(userData, "Profile 1", switchedOff: false);
            await WaitForGuardAsync(agent, g => g.State == "Present", TimeSpan.FromSeconds(10), "the pause to lift");
            Assert.Equal(1, await ExtensionOffReports.CountAsync(api, session.Id));
        }
        finally
        {
            if (sessionId is { } id)
            {
                try { await api.EndSessionAsync(id); } catch { /* best-effort */ }
            }
            foreach (var window in windows) window.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// A profile's <c>Secure Preferences</c>, with the extension as Edge writes an
    /// installed one: on, or switched off by the user (<c>disable_reasons: [1]</c>).
    /// </summary>
    private static void WritePreferences(string userData, string profile, bool switchedOff)
    {
        var folder = Path.Combine(userData, profile);
        Directory.CreateDirectory(folder);
        var entry = new JsonObject
        {
            ["location"] = 1,
            ["path"] = $"{StoreExtensionId}\\0.4.4_0",
            ["disable_reasons"] = switchedOff ? new JsonArray(1) : new JsonArray(),
        };
        var preferences = new JsonObject
        {
            ["extensions"] = new JsonObject { ["settings"] = new JsonObject { [StoreExtensionId] = entry } },
        };
        // In one move, as Edge replaces the file.
        var temp = Path.Combine(folder, "Secure Preferences.tmp");
        File.WriteAllText(temp, preferences.ToJsonString());
        File.Move(temp, Path.Combine(folder, "Secure Preferences"), overwrite: true);
    }

    private static async Task WaitForGuardAsync(
        AgentProcess agent, Func<ExtensionGuardSnapshot, bool> predicate, TimeSpan timeout, string what)
    {
        var last = await agent.WaitForAsync(s => s.ExtensionGuard is { } g && predicate(g), timeout);
        Assert.True(
            last?.ExtensionGuard is { } guard && predicate(guard),
            $"Timed out after {timeout.TotalSeconds:N0}s waiting for {what} (guard: {last?.ExtensionGuard}, " +
            $"profiles off: {string.Join(", ", last?.ExtensionGuard?.ProfilesWithExtensionOff ?? [])}).");
    }
}

/// <summary>
/// The "…Anchor extension was switched off or removed" reports of a session, as
/// the teacher's dashboard gets them: <c>TamperDetected</c> events of kind
/// <c>extension_disabled</c> (#518).
/// </summary>
internal static class ExtensionOffReports
{
    public static async Task<int> CountAsync(BackendClient api, Guid sessionId) =>
        (await api.GetSessionEventsAsync(sessionId))
        .Count(e => e.Kind == "TamperDetected" && e.PayloadJson.Contains("extension_disabled"));

    /// <summary>Poll until the teacher has exactly <paramref name="expected"/> of them, or time out.</summary>
    public static async Task<bool> WaitForAsync(BackendClient api, Guid sessionId, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await CountAsync(api, sessionId) == expected) return true;
            await Task.Delay(300);
        }
        return await CountAsync(api, sessionId) == expected;
    }
}

/// <summary>
/// Sets a window's <c>System.AppUserModel.ID</c> through its property store, as
/// Edge does for each browser window with its profile's ID (#518).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowAppId
{
    /// <summary>The window's explicit AppUserModelID, or null when it has none (#526: which Edge profile it is).</summary>
    public static string? Get(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0) return null;
        try
        {
            var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
            if (store.GetValue(ref key, out var value) != 0) return null;
            try
            {
                return value.Type == 31 /* VT_LPWSTR */ && value.Pointer != IntPtr.Zero
                    ? Marshal.PtrToStringUni(value.Pointer)
                    : null;
            }
            finally
            {
                _ = PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    public static void Set(IntPtr hwnd, string appId)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd, ref iid, out var store));
        try
        {
            var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
            var value = new PropVariant { Type = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(appId) };
            try
            {
                Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
                Marshal.ThrowExceptionForHR(store.Commit());
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.Pointer);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
