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
/// <c>anchor-witness</c> pipe and the guided install would never open. Everything
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

        private SetupEnvironment(string root, string simulateElevation)
        {
            _root = root;
            _simulateElevation = simulateElevation;
            Machine = new FakeHive($@"{root}\Machine\ExtensionInstallForcelist");
            User = new FakeHive($@"{root}\User\ExtensionInstallForcelist");
            StatePath = Path.Combine(Path.GetTempPath(), $"anchor-ext-state-{Guid.NewGuid():N}.json");
        }

        public FakeHive Machine { get; }
        public FakeHive User { get; }
        public string StatePath { get; }

        public static SetupEnvironment Create(string simulateElevation) =>
            new($@"{Root}\{Guid.NewGuid():N}", simulateElevation);

        public SetupRun Launch() =>
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
                    // No extension can check in on a pipe only this agent knows.
                    ["Witness__PipeName"] = $"anchor-witness-e2e-{Guid.NewGuid():N}",
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
