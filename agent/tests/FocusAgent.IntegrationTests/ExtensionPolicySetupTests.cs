using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using FocusAgent.Core.Extension;
using Microsoft.Win32;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end proof for #405: the <em>real</em> built agent, on its normal startup
/// path, checks the Edge forcelist under HKLM and HKCU, explains the one UAC step
/// in its own window, and then either adds Anchor's entry machine-wide at the next
/// free slot, or — on "not now" or a declined prompt — falls back to the guided
/// install and stops asking at every start.
///
/// No real UAC prompt can appear: this is a Debug build (Development) launched
/// with <c>--inject-token</c>, so the agent composes a writer that never elevates.
/// The spec opts into the test knobs instead (env vars, honoured only in such a
/// run): both "hives" live in a throwaway HKCU subtree, the student's answer to
/// the UAC prompt is simulated (on "yes" the simulated writer writes the throwaway
/// machine key the way the elevated <c>reg.exe</c> would write HKLM), the state
/// file is a temp file, the grace period is 2 s, and the witness pipe has a
/// per-launch name (#442): on a box where Edge runs the force-installed Anchor
/// extension, the real extension would otherwise check in on the default
/// <c>anchor-witness</c> pipe and the guided install would never open. For the
/// same reason the Edge profiles the agent looks for the installed extension in
/// (#488) are a throwaway <c>User Data</c> folder, and Edge itself is played by a
/// stand-in process with a name of its own (<c>Witness__BrowserProcessName</c>),
/// which a spec starts when Edge is to be running. Everything
/// else — the startup wiring, the registrar, the setup window and the
/// guided-install window on screen, the registry access, the state file — is the
/// shipped code. The setup window is answered through the status endpoint (the
/// headless stand-in for a click, like <c>POST /leave</c>) or closed with
/// <c>WM_CLOSE</c>, as a student closing it would.
///
/// No backend is needed: the agent points at a dead port and just keeps retrying.
/// The windows these runs open take focus, so the specs run in their own
/// non-parallel collection.
/// </summary>
[Collection(ExtensionPolicySetupCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ExtensionPolicySetupTests
{
    // --ui-language en-US pins these, whatever the machine's display language.
    private const string SetupTitle = "Anchor — Set up the extension";
    private const string GuidedTitle = "Anchor — Install the extension";
    private const string WinUiWindowClass = "WinUIDesktop";
    private const string Other = "someotherextensionidaaaaaaaaaaaa;https://example.test/crx";

    // #497: a test-ring ID of the spec's own (the shape Edge derives from a key),
    // whatever extension/test-ring.json names, and the entry Intune's test-ring
    // profile writes for it.
    private const string TestRingId = "abcdefghijklmnopabcdefghijklmnop";
    private const string TestRingEntry =
        TestRingId + ";https://github.com/plinklabs/Anchor/releases/download/extension-test-ring/updates.xml";

    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(45);

    [Fact]
    public async Task Allow_adds_our_entry_to_the_machine_forcelist_at_the_next_free_slot_and_leaves_other_entries_alone()
    {
        using var env = SetupEnvironment.Create(simulateElevation: "allow");
        env.Machine.Write("1", Other); // another product's force-install, already there

        await using var agent = env.Launch();
        await agent.WaitForWindowAsync(SetupTitle);

        Assert.True(await agent.Process.AnswerExtensionConsentAsync(allow: true), "No setup window to answer.");
        var status = await agent.Process.WaitForAsync(s => s.ExtensionPolicy?.Location == "Machine", WindowTimeout);
        Assert.Equal("Machine", status?.ExtensionPolicy?.Location);

        var machine = env.Machine.Read();
        Assert.Equal(2, machine.Count);
        Assert.Equal(Other, machine["1"]);
        Assert.Equal(EdgeExtensionPolicy.ForcelistEntry, machine["2"]);
        Assert.Empty(env.User.Read()); // never the per-user key

        var state = env.ReadState();
        Assert.Equal("2", state.MachineEntryValueName); // uninstall removes this slot, and only it
        Assert.False(state.PromptDeclined);
        Assert.False(status!.ExtensionPolicy!.ConsentOpen);
    }

    [Fact]
    public async Task Not_now_falls_back_to_the_guided_install_and_the_next_start_does_not_ask_again()
    {
        using var env = SetupEnvironment.Create(simulateElevation: "allow");

        await using (var first = env.Launch())
        {
            var setup = await first.WaitForWindowAsync(SetupTitle);

            // The student closes the explanation instead of allowing it.
            WindowCapture.PostClose(setup);
            await first.WaitForWindowAsync(GuidedTitle);

            // No elevation happened: with "allow" simulated, it would have written.
            Assert.Empty(env.Machine.Read());
            Assert.True(env.ReadState().PromptDeclined);
        }

        await using var second = env.Launch();
        // After the 2 s grace period the guided install opens (the extension never
        // checks in here) — without the setup window ever having been shown.
        await second.WaitForWindowAsync(GuidedTitle, mustNotSee: SetupTitle);
        var status = await second.Process.TryGetStatusAsync();
        Assert.False(status?.ExtensionPolicy?.ConsentOpen);
        Assert.Equal("None", status?.ExtensionPolicy?.Location);
        Assert.Empty(env.Machine.Read());
    }

    [Fact]
    public async Task A_declined_UAC_prompt_falls_back_to_the_guided_install_and_is_remembered()
    {
        using var env = SetupEnvironment.Create(simulateElevation: "decline");

        await using var agent = env.Launch();
        await agent.WaitForWindowAsync(SetupTitle);

        Assert.True(await agent.Process.AnswerExtensionConsentAsync(allow: true), "No setup window to answer.");
        await agent.WaitForWindowAsync(GuidedTitle);

        Assert.Empty(env.Machine.Read());
        var state = env.ReadState();
        Assert.True(state.PromptDeclined);
        Assert.Null(state.MachineEntryValueName);
    }

    [Fact]
    public async Task The_tray_offers_the_setup_again_after_a_not_now_and_allow_then_writes_the_entry()
    {
        using var env = SetupEnvironment.Create(simulateElevation: "allow");
        env.WriteState(new ExtensionPolicyState { PromptDeclined = true, PromptDeclinedAt = DateTimeOffset.UtcNow });

        await using var agent = env.Launch();
        // Startup remembers the "not now": no setup window, the guided install after the grace period.
        await agent.WaitForWindowAsync(GuidedTitle, mustNotSee: SetupTitle);

        // The student picks "Set up the Edge extension…" in the tray.
        await agent.Process.OfferExtensionSetupAsync();
        await agent.WaitForWindowAsync(SetupTitle);
        Assert.True(await agent.Process.AnswerExtensionConsentAsync(allow: true), "No setup window to answer.");

        var status = await agent.Process.WaitForAsync(s => s.ExtensionPolicy?.Location == "Machine", WindowTimeout);
        Assert.Equal("Machine", status?.ExtensionPolicy?.Location);
        Assert.Equal(EdgeExtensionPolicy.ForcelistEntry, Assert.Single(env.Machine.Read()).Value);
        var state = env.ReadState();
        Assert.False(state.PromptDeclined);
        Assert.Equal("1", state.MachineEntryValueName);
    }

    [Theory]
    [InlineData("Machine")] // Intune / Group Policy, or our own entry from an earlier run
    [InlineData("User")]    // IT, or a build from before #405
    public async Task An_existing_Anchor_entry_means_the_student_is_never_asked(string where)
    {
        using var env = SetupEnvironment.Create(simulateElevation: "allow");
        var key = where == "Machine" ? env.Machine : env.User;
        key.Write("1", EdgeExtensionPolicy.ForcelistEntry);

        await using var agent = env.Launch();
        var status = await agent.Process.WaitForAsync(s => s.ExtensionPolicy is not null, WindowTimeout);
        Assert.Equal(where, status?.ExtensionPolicy?.Location);

        // The policy is in place, so the agent only waits for the extension to
        // check in, then opens the guided install — no setup window, no write.
        await agent.WaitForWindowAsync(GuidedTitle, mustNotSee: SetupTitle);
        Assert.Single(key.Read());
        Assert.Empty((where == "Machine" ? env.User : env.Machine).Read());
        Assert.False(File.Exists(env.StatePath));
    }

    [Fact]
    public async Task After_a_reboot_an_extension_installed_in_an_Edge_profile_needs_no_install_before_Edge_is_opened()
    {
        // #488: the agent starts at sign-in, Edge isn't open yet, so the extension
        // can't check in; but the policy installed it in an Edge profile long ago.
        using var env = SetupEnvironment.Create(simulateElevation: "allow", edgeRunning: false);
        env.Machine.Write("1", EdgeExtensionPolicy.ForcelistEntry);
        env.InstallExtensionInEdgeProfile("Default");

        await using var agent = env.Launch();
        var status = await agent.Process.WaitForAsync(
            s => s.ExtensionPolicy?.Verification == "Installed", WindowTimeout);
        Assert.Equal("Installed", status?.ExtensionPolicy?.Verification);
        Assert.Equal("Machine", status?.ExtensionPolicy?.Location);

        // Well past the 2 s grace period: neither the guided install nor the setup window.
        await agent.MustNotSeeAsync(TimeSpan.FromSeconds(6), GuidedTitle, SetupTitle);
    }

    [Fact]
    public async Task A_test_ring_laptop_whose_policy_force_installs_the_self_hosted_build_needs_no_install()
    {
        // #497: Intune's test-ring profile force-installs the self-hosted build of the
        // extension, whose ID (fixed by our own signing key) isn't the store's. After
        // a reboot, Edge not open yet: the agent finds that entry under HKLM and the
        // build installed in an Edge profile, so it neither asks nor opens the
        // guided install, and doesn't add the store entry next to it.
        using var env = SetupEnvironment.Create(simulateElevation: "allow", edgeRunning: false);
        env.Machine.Write("1", TestRingEntry);
        env.InstallExtensionInEdgeProfile("Default", TestRingId);

        await using var agent = env.Launch(testRingId: TestRingId);
        var status = await agent.Process.WaitForAsync(
            s => s.ExtensionPolicy?.Verification == "Installed", WindowTimeout);
        Assert.Equal("Installed", status?.ExtensionPolicy?.Verification);
        Assert.Equal("Machine", status?.ExtensionPolicy?.Location);

        // Well past the 2 s grace period: neither the guided install nor the setup window.
        await agent.MustNotSeeAsync(TimeSpan.FromSeconds(6), GuidedTitle, SetupTitle);
        Assert.Equal(TestRingEntry, Assert.Single(env.Machine.Read()).Value);
        Assert.Empty(env.User.Read());
        Assert.False(File.Exists(env.StatePath));
    }

    [Fact]
    public async Task With_the_policy_in_place_the_wait_for_the_extension_starts_only_once_Edge_runs()
    {
        // #488: no extension in any profile, Edge not running: the agent waits for
        // Edge however long it takes, and only then gives the extension its grace period.
        using var env = SetupEnvironment.Create(simulateElevation: "allow", edgeRunning: false);
        env.Machine.Write("1", EdgeExtensionPolicy.ForcelistEntry);

        await using var agent = env.Launch();
        var status = await agent.Process.WaitForAsync(
            s => s.ExtensionPolicy?.Verification == "WaitingForBrowser", WindowTimeout);
        Assert.Equal("WaitingForBrowser", status?.ExtensionPolicy?.Verification);

        // Three grace periods with Edge closed: nothing opens, the agent still waits.
        await agent.MustNotSeeAsync(TimeSpan.FromSeconds(6), GuidedTitle, SetupTitle);
        Assert.Equal("WaitingForBrowser", (await agent.Process.TryGetStatusAsync())?.ExtensionPolicy?.Verification);

        // Edge starts. The extension never checks in here (and isn't installed), so
        // the guided install opens once the grace period from Edge's start is over.
        env.StartEdge();
        await agent.WaitForWindowAsync(GuidedTitle, mustNotSee: SetupTitle);
        status = await agent.Process.TryGetStatusAsync();
        Assert.Equal("GuidedInstall", status?.ExtensionPolicy?.Verification);
    }

    /// <summary>One throwaway forcelist key standing in for a hive.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class FakeHive(string relativePath)
    {
        public Dictionary<string, string> Read()
        {
            using var key = Registry.CurrentUser.OpenSubKey(relativePath);
            var values = new Dictionary<string, string>();
            if (key is null) return values;
            foreach (var name in key.GetValueNames())
                if (key.GetValue(name) is string s)
                    values[name] = s;
            return values;
        }

        public void Write(string name, string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(relativePath, writable: true);
            key.SetValue(name, value, RegistryValueKind.String);
        }
    }

    /// <summary>The throwaway registry subtree + state file one spec runs against.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class SetupEnvironment : IDisposable
    {
        private const string Root = @"Software\AnchorTests";

        private readonly string _root;
        private readonly string _simulateElevation;
        private readonly string _edgeFolder;
        private Process? _edge;

        private SetupEnvironment(string root, string simulateElevation)
        {
            _root = root;
            _simulateElevation = simulateElevation;
            Machine = new FakeHive($@"{root}\Machine\ExtensionInstallForcelist");
            User = new FakeHive($@"{root}\User\ExtensionInstallForcelist");
            var id = Guid.NewGuid().ToString("N");
            StatePath = Path.Combine(Path.GetTempPath(), $"anchor-ext-state-{id}.json");
            _edgeFolder = Path.Combine(Path.GetTempPath(), $"anchor-e2e-edge-{id}");
            EdgeUserData = Path.Combine(_edgeFolder, "User Data");
            Directory.CreateDirectory(EdgeUserData);
            // Unique, so no real process on the box can pass for this run's Edge.
            EdgeProcessName = $"anchor-e2e-edge-{id[..12]}";
        }

        public FakeHive Machine { get; }
        public FakeHive User { get; }
        public string StatePath { get; }

        /// <summary>The Edge <c>User Data</c> folder the agent looks for the extension in (#488).</summary>
        public string EdgeUserData { get; }

        /// <summary>The process name the agent takes for Edge (#488).</summary>
        public string EdgeProcessName { get; }

        /// <param name="edgeRunning">Start this run's stand-in Edge before the agent.</param>
        public static SetupEnvironment Create(string simulateElevation, bool edgeRunning = true)
        {
            var env = new SetupEnvironment($@"{Root}\{Guid.NewGuid():N}", simulateElevation);
            if (edgeRunning) env.StartEdge();
            return env;
        }

        /// <summary>
        /// Leave the extension unpacked in profile <paramref name="profile"/>, as
        /// Edge does once it has installed it (#488).
        /// </summary>
        public void InstallExtensionInEdgeProfile(string profile, string extensionId = EdgeExtensionPolicy.ExtensionId)
        {
            var version = Path.Combine(EdgeUserData, profile, "Extensions", extensionId, "0.4.3_0");
            Directory.CreateDirectory(version);
            File.WriteAllText(Path.Combine(version, "manifest.json"), "{}");
        }

        /// <summary>
        /// Start the stand-in Edge: a copy of the system's <c>ping.exe</c> under
        /// <see cref="EdgeProcessName"/>, pinging the loopback for long enough to
        /// outlast the spec, without a window.
        /// </summary>
        public void StartEdge()
        {
            if (_edge is not null) return;
            var exe = Path.Combine(_edgeFolder, EdgeProcessName + ".exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe, overwrite: true);
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            foreach (var arg in new[] { "-n", "600", "127.0.0.1" })
                psi.ArgumentList.Add(arg);
            _edge = Process.Start(psi) ?? throw new InvalidOperationException("The stand-in Edge didn't start.");
            // Drain the output, so ping never blocks on a full pipe.
            _edge.BeginOutputReadLine();
        }

        /// <param name="testRingId">
        /// The ID this run takes for the self-hosted test-ring build's (#497); null:
        /// the ones the agent was built with.
        /// </param>
        public SetupRun Launch(string? testRingId = null) =>
            new(AgentProcess.Launch(
                // Nothing listens here: the agent keeps retrying, which is all
                // this spec needs from the connection.
                backendUrl: $"http://127.0.0.1:{TestConfig.FreeLoopbackPort()}",
                impersonateOid: TestConfig.StudentOid,
                extraArgs: ["--ui-language", "en-US"],
                extraEnvironment: new Dictionary<string, string>
                {
                    ["Extension__TestPolicyRoot"] = _root,
                    ["Extension__StatePath"] = StatePath,
                    ["Extension__SimulateElevation"] = _simulateElevation,
                    ["Extension__GracePeriodSeconds"] = "2",
                    // #488: this run's Edge profiles, so the box's own Edge can't decide the spec.
                    ["Extension__TestEdgeUserDataPath"] = EdgeUserData,
                    // No extension can check in on a pipe only this agent knows.
                    ["Witness__PipeName"] = $"anchor-witness-e2e-{Guid.NewGuid():N}",
                    // #488: Edge is running when, and only when, this run's stand-in is.
                    ["Witness__BrowserProcessName"] = EdgeProcessName,
                    // #497: empty, the agent's built-in IDs.
                    ["Extension__TestRingExtensionId"] = testRingId ?? "",
                }));

        public void WriteState(ExtensionPolicyState state) =>
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));

        public ExtensionPolicyState ReadState() =>
            JsonSerializer.Deserialize<ExtensionPolicyState>(File.ReadAllText(StatePath))
            ?? throw new InvalidOperationException("Empty state file.");

        public void Dispose()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false); } catch { }
            try { File.Delete(StatePath); } catch { }
            if (_edge is not null)
            {
                try { _edge.Kill(); _edge.WaitForExit(5000); } catch { }
                _edge.Dispose();
            }
            try { Directory.Delete(_edgeFolder, recursive: true); } catch { }
        }
    }

    /// <summary>One agent launch, with a window probe; killed on dispose.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class SetupRun(AgentProcess process) : IAsyncDisposable
    {
        public AgentProcess Process { get; } = process;

        /// <summary>
        /// Poll until a visible agent window titled <paramref name="title"/> is on
        /// screen; with <paramref name="mustNotSee"/>, fail as soon as a window with
        /// that title shows up instead.
        /// </summary>
        public async Task<IntPtr> WaitForWindowAsync(string title, string? mustNotSee = null)
        {
            var deadline = DateTime.UtcNow + WindowTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (mustNotSee is not null)
                {
                    Assert.True(
                        WindowCapture.FindVisibleWindow(Process.Pid, WinUiWindowClass, mustNotSee) == IntPtr.Zero,
                        $"'{mustNotSee}' appeared while waiting for '{title}'.");
                }

                var hwnd = WindowCapture.FindVisibleWindow(Process.Pid, WinUiWindowClass, title);
                if (hwnd != IntPtr.Zero) return hwnd;

                if (Process.HasExited)
                    throw new InvalidOperationException(
                        $"The agent exited before showing '{title}'. If its exit code is 0, another agent " +
                        "instance probably holds the single-instance lock.");
                await Task.Delay(100);
            }
            throw new TimeoutException(
                $"No '{title}' window within {WindowTimeout.TotalSeconds:N0}s. Windows:\n" +
                WindowCapture.DescribeWindows(Process.Pid));
        }

        /// <summary>
        /// Watch for <paramref name="duration"/> and fail as soon as a visible agent
        /// window with one of <paramref name="titles"/> shows up.
        /// </summary>
        public async Task MustNotSeeAsync(TimeSpan duration, params string[] titles)
        {
            var deadline = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < deadline)
            {
                foreach (var title in titles)
                {
                    Assert.True(
                        WindowCapture.FindVisibleWindow(Process.Pid, WinUiWindowClass, title) == IntPtr.Zero,
                        $"'{title}' appeared.");
                }
                Assert.False(Process.HasExited, "The agent exited.");
                await Task.Delay(100);
            }
        }

        public ValueTask DisposeAsync() => Process.DisposeAsync();
    }
}

/// <summary>
/// The Edge extension setup specs (#405) open focus-taking windows, so they run
/// serially and never alongside the other specs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExtensionPolicySetupCollection
{
    public const string Name = "extension-policy-setup";
}
