using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Asserting e2e for #556: the extension is installed in the Edge profile the
/// student has open, but its link to the agent never comes up. In the classroom
/// test of 2026-11-20 that left the session unfiltered and silent: the extension
/// gets its session only over the link, and the agent's guard stayed Off because
/// the link had never been up since the agent started. Now the agent sees on disk
/// that the extension is installed in an open profile, waits the grace period,
/// wakes it by opening Edge in that profile at a page the session allows, waits
/// once more, then pauses Edge and tells the teacher it isn't connected (not that
/// it was switched off). A link that comes up at last lifts the pause.
///
/// The real agent, backend and witness pipe, with the stand-ins of
/// <see cref="ExtensionOffInOneProfileTests"/>: a <c>User Data</c> folder of the
/// spec's own with the extension installed in the Default profile, the spec's own
/// window as that profile's browser window (its AppUserModelID set as Edge sets
/// it), and a fake extension on a private pipe that stays away until the end.
/// The browser is a stand-in, so the agent never starts Edge: what it would have
/// opened is read from /status.
/// </summary>
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ExtensionNeverConnectedTests
{
    private const string StoreExtensionId = "dnkimhodjfogjibnbbfdjdapgmmiojio";
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(6);

    private readonly BackendFixture _backend;
    public ExtensionNeverConnectedTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task An_installed_extension_that_never_connects_is_woken_then_edge_is_paused_and_the_teacher_told()
    {
        var api = new BackendClient(_backend.Url);
        var stale = await api.GetRejoinableSessionIdsAsync();
        Assert.True(
            stale.Count == 0,
            $"The student is still in running session(s) {string.Join(", ", stale)} that an earlier spec left " +
            "behind; this agent would rejoin them on connect. End every session a spec starts.");

        var root = Path.Combine(Path.GetTempPath(), "anchor-e2e-edge-" + Guid.NewGuid().ToString("N"));
        var userData = Path.Combine(root, "User Data");
        WriteInstalledExtension(userData, "Default");

        var pipeName = "anchor-witness-e2e-" + Guid.NewGuid().ToString("N");
        using var self = Process.GetCurrentProcess();
        await using var agent = AgentProcess.Launch(
            _backend.Url,
            TestConfig.StudentOid,
            autoJoin: true,
            extraEnvironment: new Dictionary<string, string>
            {
                ["Witness__PipeName"] = pipeName,
                // The spec's own window plays Edge's.
                ["Witness__BrowserProcessName"] = self.ProcessName,
                ["Witness__BrowserUserDataFolder"] = userData,
                ["Session__ExtensionGraceSeconds"] = ((int)Grace.TotalSeconds).ToString(),
            });
        await using var extension = new FakeExtensionWitness(pipeName);
        OffListWindow? window = null;
        Guid? sessionId = null;
        try
        {
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

            var classId = await api.FindClassIdAsync();
            var session = await api.StartSessionAsync(classId);
            sessionId = session.Id;

            // 1. The join: guarded, not Off (Off was the silent case).
            var joined = await agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id && s.StartupSweep is not null && s.ExtensionGuard is not null,
                TimeSpan.FromSeconds(10));
            Assert.True(
                joined is { ExtensionGuard.State: "Waiting" },
                $"The guard didn't wait for the installed extension at the join (guard: {joined?.ExtensionGuard}).");

            // The Default profile's window comes up on screen, with the extension
            // installed and on there, and no link: the extension never connects.
            // After the join's sweep, which would minimize a window already open
            // (the stand-in isn't an allowed app), and without taking the
            // foreground, which would have it blocked.
            window = await OffListWindow.OpenAsync("Anchor e2e Edge (Default)", TimeSpan.FromSeconds(5), activate: false);
            WindowAppId.Set(window.Handle, "MSEdge");

            // 2. The grace period runs out: the agent wakes the extension, opening
            //    the profile it is installed in at an http(s) page. Nothing paused yet.
            var woken = await agent.WaitForAsync(
                s => s.SchoolProfile?.LastOpened is { Profile: "Default" }, Grace + TimeSpan.FromSeconds(10));
            var opened = woken?.SchoolProfile?.LastOpened;
            Assert.True(
                opened is { Profile: "Default" },
                $"The agent did not open the profile with the extension to wake it (last opened: {opened}).");
            Assert.StartsWith("https://", opened!.Url);
            Assert.NotEqual("Paused", woken!.ExtensionGuard?.State);
            Assert.Equal(0, await TamperReports.CountAsync(api, session.Id, "extension_not_connected"));

            // 3. Still no link after another grace period: Edge is paused for it, and
            //    the teacher is told it isn't connected, once, not "switched off".
            await WaitForGuardAsync(
                agent, g => g is { State: "Paused", PauseReason: "NotConnected" }, Grace + TimeSpan.FromSeconds(10),
                "the browser to be paused for an extension that isn't connected");
            Assert.True(
                await TamperReports.WaitForAsync(api, session.Id, "extension_not_connected", 1, TimeSpan.FromSeconds(10)),
                "Edge was paused for an extension that never connected, but the teacher was not told.");
            Assert.Equal(0, await TamperReports.CountAsync(api, session.Id, "extension_disabled"));

            // 4. The link comes up at last: the pause lifts, and nothing more is told.
            await extension.ConnectAsync();
            await WaitForGuardAsync(
                agent, g => g is { WitnessConnected: true, State: "Present" }, TimeSpan.FromSeconds(10), "the pause to lift");
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Equal(1, await TamperReports.CountAsync(api, session.Id, "extension_not_connected"));
        }
        finally
        {
            if (sessionId is { } id)
            {
                try { await api.EndSessionAsync(id); } catch { /* best-effort */ }
            }
            window?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// A profile's <c>Secure Preferences</c> with the extension installed and on,
    /// as Edge writes it.
    /// </summary>
    private static void WriteInstalledExtension(string userData, string profile)
    {
        var folder = Path.Combine(userData, profile);
        Directory.CreateDirectory(folder);
        var preferences = new JsonObject
        {
            ["extensions"] = new JsonObject
            {
                ["settings"] = new JsonObject
                {
                    [StoreExtensionId] = new JsonObject
                    {
                        ["location"] = 1,
                        ["path"] = $"{StoreExtensionId}\\0.6.0_0",
                        ["disable_reasons"] = new JsonArray(),
                    },
                },
            },
        };
        File.WriteAllText(Path.Combine(folder, "Secure Preferences"), preferences.ToJsonString());
    }

    private static async Task WaitForGuardAsync(
        AgentProcess agent, Func<ExtensionGuardSnapshot, bool> predicate, TimeSpan timeout, string what)
    {
        var last = await agent.WaitForAsync(s => s.ExtensionGuard is { } g && predicate(g), timeout);
        Assert.True(
            last?.ExtensionGuard is { } guard && predicate(guard),
            $"Timed out after {timeout.TotalSeconds:N0}s waiting for {what} (guard: {last?.ExtensionGuard}).");
    }
}
