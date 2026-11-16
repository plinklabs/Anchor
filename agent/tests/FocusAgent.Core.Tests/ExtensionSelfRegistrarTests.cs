using FocusAgent.Core.Extension;

namespace FocusAgent.Core.Tests;

/// <summary>
/// The Edge extension setup's decisions (#211, #405): when a policy is already in
/// place (HKLM or HKCU, ours or Intune's), when to ask, what a "not now" / declined
/// UAC prompt / failed write leads to, what is remembered, and what uninstall
/// removes; and (#488) when the extension counts as there: installed in an Edge
/// profile, or checked in once Edge runs. Every side effect is faked: no registry,
/// no UAC prompt, no window, no Edge.
/// </summary>
public class ExtensionSelfRegistrarTests
{
    private const string Other = "someotherextensionidaaaaaaaaaaaa;https://example/crx";
    private const string TestRing = "abcdefghijklmnopabcdefghijklmnop";
    private const string TestRingEntry =
        TestRing + ";https://github.com/plinklabs/Anchor/releases/download/extension-test-ring/updates.xml";
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(7);

    // --- Detecting an existing policy (no elevation) -------------------------

    [Fact]
    public void FindPolicy_reports_an_entry_under_HKLM()
    {
        var h = new Harness();
        h.Machine.Entries["4"] = EdgeExtensionPolicy.ForcelistEntry;

        Assert.Equal(ExtensionPolicyLocation.Machine, h.Registrar().FindPolicy());
    }

    [Fact]
    public void FindPolicy_reports_an_entry_under_HKCU()
    {
        var h = new Harness();
        h.User.Entries["1"] = EdgeExtensionPolicy.ForcelistEntry;

        Assert.Equal(ExtensionPolicyLocation.User, h.Registrar().FindPolicy());
    }

    [Fact]
    public void FindPolicy_ignores_other_products_entries()
    {
        var h = new Harness();
        h.Machine.Entries["1"] = Other;
        h.User.Entries["1"] = Other;

        Assert.Equal(ExtensionPolicyLocation.None, h.Registrar().FindPolicy());
    }

    [Theory]
    [InlineData(ExtensionPolicyLocation.Machine)] // Intune's test-ring profile, device scope
    [InlineData(ExtensionPolicyLocation.User)]    // the same profile, user scope
    public async Task A_test_ring_entry_is_a_policy_in_place_so_the_student_is_never_asked(ExtensionPolicyLocation where)
    {
        // #497: Intune's test-ring profile force-installs the self-hosted build, whose
        // ID isn't the store's. That laptop has the extension; nothing to set up.
        var h = new Harness { ExtensionIds = AnchorExtensionIds.WithTestRing(TestRing), InstalledIn = @"Edge\Default" };
        (where == ExtensionPolicyLocation.Machine ? h.Machine : h.User).Entries["1"] = TestRingEntry;

        Assert.Equal(where, h.Registrar().FindPolicy());
        var ok = await h.Registrar().RegisterAndVerifyAsync();

        Assert.True(ok);
        Assert.Equal(0, h.ConsentAsked);
        Assert.Empty(h.Elevated.AddCalls);
        Assert.Equal(0, h.GuidedShown);
    }

    [Fact]
    public void Without_a_test_ring_configured_its_entry_is_another_products_as_before()
    {
        var h = new Harness { ExtensionIds = AnchorExtensionIds.StoreOnly };
        h.Machine.Entries["1"] = TestRingEntry;

        Assert.Equal(ExtensionPolicyLocation.None, h.Registrar().FindPolicy());
    }

    [Fact]
    public async Task Next_to_a_test_ring_entry_the_agent_writes_the_store_entry_only_when_asked()
    {
        // The test-ring ID is only ever accepted, never written: a laptop without a
        // policy still gets the store build's entry.
        var h = new Harness
        {
            ExtensionIds = AnchorExtensionIds.WithTestRing(TestRing),
            ConsentAnswer = true,
            CheckedInAfterGrace = true,
        };

        await h.Registrar().RegisterAndVerifyAsync();

        Assert.Equal(EdgeExtensionPolicy.ForcelistEntry, Assert.Single(h.Elevated.AddCalls).Entry);
    }

    [Fact]
    public void FindPolicy_treats_an_unreadable_key_as_empty_and_still_checks_the_other()
    {
        var h = new Harness { Machine = { ThrowOnRead = true } };
        h.User.Entries["1"] = EdgeExtensionPolicy.ForcelistEntry;

        Assert.Equal(ExtensionPolicyLocation.User, h.Registrar().FindPolicy());
    }

    [Theory]
    [InlineData(ExtensionPolicyLocation.Machine)] // ours from an earlier run, or Intune/GPO
    [InlineData(ExtensionPolicyLocation.User)]    // IT, or a build from before #405
    public async Task Startup_with_a_policy_in_place_never_asks_or_elevates(ExtensionPolicyLocation where)
    {
        var h = new Harness { CheckedIn = true };
        (where == ExtensionPolicyLocation.Machine ? h.Machine : h.User).Entries["1"] = EdgeExtensionPolicy.ForcelistEntry;

        var ok = await h.Registrar().RegisterAndVerifyAsync();

        Assert.True(ok);
        Assert.Equal(0, h.ConsentAsked);
        Assert.Empty(h.Elevated.AddCalls);
        Assert.Equal(0, h.GuidedShown);
    }

    // --- Asking + the one elevated write --------------------------------------

    [Fact]
    public async Task Startup_without_a_policy_asks_then_writes_the_HKLM_entry_through_one_elevated_write()
    {
        var h = new Harness { ConsentAnswer = true, CheckedInAfterGrace = true };

        var ok = await h.Registrar().RegisterAndVerifyAsync();

        Assert.True(ok);
        Assert.Equal(1, h.ConsentAsked);
        var call = Assert.Single(h.Elevated.AddCalls);
        Assert.Equal(("1", EdgeExtensionPolicy.ForcelistEntry), call);
        Assert.Equal(EdgeExtensionPolicy.ForcelistEntry, Assert.Single(h.Machine.Entries).Value);
        Assert.Empty(h.User.Entries); // the agent never writes HKCU
        Assert.Equal("1", h.State.Saved.MachineEntryValueName);
        Assert.False(h.State.Saved.PromptDeclined);
        Assert.Equal(0, h.GuidedShown); // the extension checked in within the grace period
    }

    [Fact]
    public async Task The_elevated_write_takes_the_next_free_slot_and_leaves_other_entries_alone()
    {
        var h = new Harness { ConsentAnswer = true, CheckedInAfterGrace = true };
        h.Machine.Entries["1"] = Other;
        h.Machine.Entries["3"] = Other + "x";

        await h.Registrar().RegisterAndVerifyAsync();

        Assert.Equal("2", Assert.Single(h.Elevated.AddCalls).Slot);
        Assert.Equal(Other, h.Machine.Entries["1"]);
        Assert.Equal(Other + "x", h.Machine.Entries["3"]);
        Assert.Equal(EdgeExtensionPolicy.ForcelistEntry, h.Machine.Entries["2"]);
        Assert.Equal("2", h.State.Saved.MachineEntryValueName);
    }

    [Fact]
    public async Task After_a_successful_write_the_guided_install_still_opens_if_the_extension_never_checks_in()
    {
        var h = new Harness { ConsentAnswer = true };

        var ok = await h.Registrar().RegisterAndVerifyAsync();

        Assert.False(ok);
        Assert.Equal(1, h.Delays);       // waited the grace period for Edge first
        Assert.Equal(1, h.GuidedShown);
    }

    [Fact]
    public async Task Not_now_falls_back_to_the_guided_install_at_once_and_is_remembered()
    {
        var h = new Harness { ConsentAnswer = false };

        var ok = await h.Registrar().RegisterAndVerifyAsync();

        Assert.False(ok);
        Assert.Empty(h.Elevated.AddCalls); // no UAC prompt after "not now"
        Assert.Empty(h.Machine.Entries);
        Assert.Equal(1, h.GuidedShown);
        Assert.Equal(0, h.Delays);         // the student is at the screen: no grace wait
        Assert.True(h.State.Saved.PromptDeclined);
        Assert.NotNull(h.State.Saved.PromptDeclinedAt);
    }

    [Theory]
    [InlineData(ElevatedPolicyWriteResult.Declined)] // said no in the UAC prompt, or no admin password
    [InlineData(ElevatedPolicyWriteResult.Failed)]
    public async Task A_declined_or_failed_elevation_falls_back_to_the_guided_install_and_is_remembered(
        ElevatedPolicyWriteResult result)
    {
        var h = new Harness { ConsentAnswer = true, Elevated = { Result = result } };

        var ok = await h.Registrar().RegisterAndVerifyAsync();

        Assert.False(ok);
        Assert.Single(h.Elevated.AddCalls);
        Assert.Empty(h.Machine.Entries);
        Assert.Equal(1, h.GuidedShown);
        Assert.True(h.State.Saved.PromptDeclined);
        Assert.Null(h.State.Saved.MachineEntryValueName);
    }

    [Fact]
    public async Task A_reported_success_without_the_entry_in_HKLM_counts_as_a_failure()
    {
        var h = new Harness { ConsentAnswer = true, Elevated = { WriteOnSuccess = false } };

        var ok = await h.Registrar().RegisterAndVerifyAsync();

        Assert.False(ok);
        Assert.Equal(1, h.GuidedShown);
        Assert.Null(h.State.Saved.MachineEntryValueName);
        Assert.True(h.State.Saved.PromptDeclined);
    }

    [Fact]
    public async Task A_window_closed_without_an_answer_remembers_nothing_and_opens_nothing()
    {
        // The agent shut down with the setup window still open: not the student's
        // answer, so the next start asks again.
        var h = new Harness { ConsentAnswer = null };

        var ok = await h.Registrar().RegisterAndVerifyAsync();

        Assert.False(ok);
        Assert.Empty(h.Elevated.AddCalls);
        Assert.Equal(0, h.GuidedShown);
        Assert.False(h.State.Saved.PromptDeclined);
    }

    [Fact]
    public async Task Not_now_with_the_extension_already_running_opens_no_guided_install()
    {
        // Installed from the store earlier: nothing more to do.
        var h = new Harness { ConsentAnswer = false, CheckedIn = true };

        var ok = await h.Registrar().RegisterAndVerifyAsync();

        Assert.True(ok);
        Assert.Equal(0, h.GuidedShown);
    }

    [Fact]
    public async Task A_setup_window_that_fails_to_open_falls_back_without_remembering_a_decline()
    {
        var h = new Harness { ConsentThrows = true };

        var ok = await h.Registrar().RegisterAndVerifyAsync();

        Assert.False(ok);
        Assert.Empty(h.Elevated.AddCalls);
        Assert.Equal(1, h.GuidedShown);
        Assert.False(h.State.Saved.PromptDeclined); // ask again next start
    }

    // --- Remembering a "not now" ----------------------------------------------

    [Fact]
    public async Task Startup_after_a_remembered_decline_doesnt_ask_and_keeps_the_guided_flow()
    {
        var h = new Harness { ConsentAnswer = true };
        h.State.Saved = new ExtensionPolicyState { PromptDeclined = true, PromptDeclinedAt = DateTimeOffset.UtcNow };

        var ok = await h.Registrar().RegisterAndVerifyAsync();

        Assert.False(ok);
        Assert.Equal(0, h.ConsentAsked);
        Assert.Empty(h.Elevated.AddCalls);
        Assert.Equal(1, h.Delays);      // waits for a store install to check in first
        Assert.Equal(1, h.GuidedShown);
    }

    [Fact]
    public async Task The_tray_offer_asks_again_after_a_decline_and_a_write_clears_it()
    {
        var h = new Harness { ConsentAnswer = true, CheckedInAfterGrace = true };
        h.State.Saved = new ExtensionPolicyState { PromptDeclined = true, PromptDeclinedAt = DateTimeOffset.UtcNow };

        var ok = await h.Registrar().OfferAgainAsync();

        Assert.True(ok);
        Assert.Equal(1, h.ConsentAsked);
        Assert.Single(h.Elevated.AddCalls);
        Assert.False(h.State.Saved.PromptDeclined);
        Assert.Null(h.State.Saved.PromptDeclinedAt);
        Assert.Equal("1", h.State.Saved.MachineEntryValueName);
    }

    [Fact]
    public async Task The_tray_offer_does_nothing_when_a_policy_is_in_place()
    {
        var h = new Harness { ConsentAnswer = true };
        h.Machine.Entries["1"] = EdgeExtensionPolicy.ForcelistEntry;

        var ok = await h.Registrar().OfferAgainAsync();

        Assert.True(ok);
        Assert.Equal(0, h.ConsentAsked);
        Assert.Empty(h.Elevated.AddCalls);
    }

    [Fact]
    public async Task A_second_offer_while_the_first_is_on_screen_doesnt_ask_twice()
    {
        var h = new Harness();
        var answer = new TaskCompletionSource<bool?>();
        h.ConsentOverride = _ => answer.Task;
        var registrar = h.Registrar();

        var first = registrar.RegisterAndVerifyAsync();
        var second = await registrar.OfferAgainAsync();
        answer.SetResult(false);
        await first;

        Assert.False(second);
        Assert.Equal(1, h.ConsentAsked);
    }

    [Fact]
    public async Task A_state_store_that_cant_save_still_falls_back()
    {
        var h = new Harness { ConsentAnswer = false, State = { ThrowOnSave = true } };

        var ok = await h.Registrar().RegisterAndVerifyAsync();

        Assert.False(ok);
        Assert.Equal(1, h.GuidedShown);
    }

    // --- The grace-period verify (unchanged from #211) ------------------------

    [Fact]
    public async Task Verify_skips_the_wait_when_already_checked_in()
    {
        var h = new Harness { CheckedIn = true };
        h.Machine.Entries["1"] = EdgeExtensionPolicy.ForcelistEntry;

        Assert.True(await h.Registrar().RegisterAndVerifyAsync());
        Assert.Equal(0, h.Delays);
        Assert.Equal(0, h.GuidedShown);
    }

    [Fact]
    public async Task Verify_falls_back_to_guided_install_when_the_extension_never_checks_in()
    {
        var h = new Harness();
        h.Machine.Entries["1"] = EdgeExtensionPolicy.ForcelistEntry;

        Assert.False(await h.Registrar().RegisterAndVerifyAsync());
        Assert.Equal(1, h.Delays);
        Assert.Equal(1, h.GuidedShown);
    }

    // --- After a reboot: Edge not running yet (#488) ---------------------------

    [Fact]
    public async Task With_the_policy_in_place_an_extension_installed_in_an_Edge_profile_needs_no_wait_and_no_install()
    {
        // The reboot case: the agent starts at sign-in, Edge isn't open, so the
        // extension can't check in, but Edge installed it long ago.
        var h = new Harness { BrowserRunning = false, InstalledIn = @"Edge\Default" };
        h.Machine.Entries["1"] = EdgeExtensionPolicy.ForcelistEntry;
        var registrar = h.Registrar();

        Assert.True(await registrar.RegisterAndVerifyAsync());
        Assert.Equal(ExtensionVerification.Installed, registrar.Verification);
        Assert.Empty(h.DelayedIn);
        Assert.Equal(0, h.GuidedShown);
    }

    [Fact]
    public async Task With_the_policy_in_place_the_grace_period_starts_only_once_Edge_runs()
    {
        var h = new Harness { BrowserRunning = false, BrowserStartsAfterPolls = 3 };
        h.Machine.Entries["1"] = EdgeExtensionPolicy.ForcelistEntry;
        var registrar = h.Registrar();

        Assert.False(await registrar.RegisterAndVerifyAsync());

        // Three looks for Edge, however long that takes, then one grace period.
        Assert.Equal(
            new[]
            {
                ExtensionVerification.WaitingForBrowser,
                ExtensionVerification.WaitingForBrowser,
                ExtensionVerification.WaitingForBrowser,
                ExtensionVerification.WaitingForCheckIn,
            },
            h.DelayedIn);
        Assert.Equal(new[] { PollInterval, PollInterval, PollInterval, Grace }, h.Delayed);
        Assert.Equal(1, h.GuidedShown);
        Assert.Equal(ExtensionVerification.GuidedInstall, registrar.Verification);
    }

    [Fact]
    public async Task While_Edge_isnt_running_nothing_opens()
    {
        // Edge stays closed: the agent keeps waiting, the guided install never opens.
        var h = new Harness { BrowserRunning = false };
        h.Machine.Entries["1"] = EdgeExtensionPolicy.ForcelistEntry;
        using var stop = new CancellationTokenSource();
        h.OnDelay = _ =>
        {
            if (h.DelayedIn.Count == 50) stop.Cancel();
        };
        var registrar = h.Registrar();

        Assert.False(await registrar.RegisterAndVerifyAsync(stop.Token));
        Assert.All(h.DelayedIn, state => Assert.Equal(ExtensionVerification.WaitingForBrowser, state));
        Assert.Equal(0, h.GuidedShown);
    }

    [Fact]
    public async Task An_extension_that_checks_in_while_waiting_for_Edge_ends_the_wait()
    {
        var h = new Harness { BrowserRunning = false };
        h.Machine.Entries["1"] = EdgeExtensionPolicy.ForcelistEntry;
        h.OnDelay = _ =>
        {
            if (h.DelayedIn.Count == 2) h.CheckedIn = true;
        };
        var registrar = h.Registrar();

        Assert.True(await registrar.RegisterAndVerifyAsync());
        Assert.Equal(2, h.DelayedIn.Count); // no grace period after it
        Assert.Equal(ExtensionVerification.CheckedIn, registrar.Verification);
        Assert.Equal(0, h.GuidedShown);
    }

    [Fact]
    public async Task An_extension_Edge_installed_between_two_looks_ends_the_wait()
    {
        // Edge was opened and closed again between two looks for it, and installed
        // the force-installed extension meanwhile.
        var h = new Harness { BrowserRunning = false };
        h.Machine.Entries["1"] = EdgeExtensionPolicy.ForcelistEntry;
        h.OnDelay = _ =>
        {
            if (h.DelayedIn.Count == 2) h.InstalledIn = @"Edge\Profile 1";
        };
        var registrar = h.Registrar();

        Assert.True(await registrar.RegisterAndVerifyAsync());
        Assert.Equal(ExtensionVerification.Installed, registrar.Verification);
        Assert.Equal(0, h.GuidedShown);
    }

    [Fact]
    public async Task An_extension_installed_by_the_end_of_the_grace_period_that_hasnt_checked_in_needs_no_install()
    {
        // Edge installed it, but it hasn't connected (yet): installing it again
        // from the store can't help.
        var h = new Harness();
        h.Machine.Entries["1"] = EdgeExtensionPolicy.ForcelistEntry;
        h.OnDelay = _ => h.InstalledIn = @"Edge\Default";
        var registrar = h.Registrar();

        Assert.True(await registrar.RegisterAndVerifyAsync());
        Assert.Equal(ExtensionVerification.WaitingForCheckIn, Assert.Single(h.DelayedIn));
        Assert.Equal(ExtensionVerification.Installed, registrar.Verification);
        Assert.Equal(0, h.GuidedShown);
    }

    [Fact]
    public async Task After_a_remembered_not_now_an_extension_installed_from_the_store_needs_no_install()
    {
        var h = new Harness { BrowserRunning = false, InstalledIn = @"Edge\Default" };
        h.State.Saved = new ExtensionPolicyState { PromptDeclined = true, PromptDeclinedAt = DateTimeOffset.UtcNow };

        Assert.True(await h.Registrar().RegisterAndVerifyAsync());
        Assert.Empty(h.DelayedIn);
        Assert.Equal(0, h.GuidedShown);
    }

    [Fact]
    public async Task Not_now_with_the_extension_installed_from_the_store_opens_no_guided_install()
    {
        var h = new Harness { ConsentAnswer = false, BrowserRunning = false, InstalledIn = @"Edge\Default" };

        Assert.True(await h.Registrar().RegisterAndVerifyAsync());
        Assert.Equal(0, h.GuidedShown);
    }

    [Fact]
    public async Task The_tray_setup_while_startup_waits_for_Edge_joins_that_wait_and_opens_one_guided_install()
    {
        // Startup remembered a "not now" and waits for Edge; meanwhile the student
        // sets the policy up from the tray. One wait, one guided install at most.
        var h = new Harness { ConsentAnswer = true, BrowserRunning = false };
        h.State.Saved = new ExtensionPolicyState { PromptDeclined = true, PromptDeclinedAt = DateTimeOffset.UtcNow };
        var edgeStarted = new TaskCompletionSource();
        h.DelayOverride = async (state, _) =>
        {
            if (state == ExtensionVerification.WaitingForBrowser)
            {
                await edgeStarted.Task;
                h.BrowserRunning = true;
            }
        };
        var registrar = h.Registrar();

        var startup = registrar.RegisterAndVerifyAsync();
        var tray = registrar.OfferAgainAsync();
        Assert.Single(h.Elevated.AddCalls); // the tray's write went through...
        Assert.False(tray.IsCompleted);     // ...and joined the wait for Edge
        edgeStarted.SetResult();

        Assert.False(await startup);
        Assert.False(await tray);
        Assert.Equal(1, h.GuidedShown);
    }

    [Fact]
    public async Task A_failing_Edge_check_counts_Edge_as_running()
    {
        var h = new Harness { BrowserThrows = true };
        h.Machine.Entries["1"] = EdgeExtensionPolicy.ForcelistEntry;

        Assert.False(await h.Registrar().RegisterAndVerifyAsync());
        Assert.Equal(ExtensionVerification.WaitingForCheckIn, Assert.Single(h.DelayedIn));
        Assert.Equal(1, h.GuidedShown);
    }

    [Fact]
    public async Task A_failing_profile_probe_counts_the_extension_as_not_installed()
    {
        var h = new Harness { ProbeThrows = true };
        h.Machine.Entries["1"] = EdgeExtensionPolicy.ForcelistEntry;

        Assert.False(await h.Registrar().RegisterAndVerifyAsync());
        Assert.Equal(1, h.GuidedShown);
    }

    // --- Uninstall -----------------------------------------------------------

    [Fact]
    public async Task Uninstall_removes_the_HKLM_entry_this_agent_wrote_through_the_elevated_writer()
    {
        var h = new Harness();
        h.Machine.Entries["1"] = Other;
        h.Machine.Entries["2"] = EdgeExtensionPolicy.ForcelistEntry;
        h.State.Saved = new ExtensionPolicyState { MachineEntryValueName = "2" };

        await h.Registrar().RemoveForUninstall();

        Assert.Equal("2", Assert.Single(h.Elevated.RemoveCalls));
    }

    [Fact]
    public async Task Uninstall_leaves_an_HKLM_entry_it_didnt_write_alone()
    {
        // Intune/GPO pushed the entry; this agent never wrote one, so no UAC prompt.
        var h = new Harness();
        h.Machine.Entries["1"] = EdgeExtensionPolicy.ForcelistEntry;

        await h.Registrar().RemoveForUninstall();

        Assert.Empty(h.Elevated.RemoveCalls);
    }

    [Fact]
    public async Task Uninstall_leaves_HKLM_alone_when_our_old_slot_now_holds_another_entry()
    {
        var h = new Harness();
        h.Machine.Entries["2"] = Other;
        h.State.Saved = new ExtensionPolicyState { MachineEntryValueName = "2" };

        await h.Registrar().RemoveForUninstall();

        Assert.Empty(h.Elevated.RemoveCalls);
    }

    [Fact]
    public async Task Uninstall_still_clears_old_HKCU_entries_but_only_ours()
    {
        var h = new Harness();
        h.User.Entries["1"] = Other;
        h.User.Entries["2"] = EdgeExtensionPolicy.ForcelistEntry;

        await h.Registrar().RemoveForUninstall();

        Assert.Equal(Other, Assert.Single(h.User.Entries).Value);
    }

    [Fact]
    public async Task Uninstall_never_throws_and_still_reaches_HKLM_when_HKCU_is_locked()
    {
        var h = new Harness { User = { ThrowOnWrite = true } };
        h.Machine.Entries["1"] = EdgeExtensionPolicy.ForcelistEntry;
        h.State.Saved = new ExtensionPolicyState { MachineEntryValueName = "1" };

        var ex = await Record.ExceptionAsync(() => h.Registrar().RemoveForUninstall());

        Assert.Null(ex);
        Assert.Equal("1", Assert.Single(h.Elevated.RemoveCalls));
    }

    // --- The dev CLI helper --------------------------------------------------

    [Fact]
    public void AddAnchorEntryIfMissing_writes_the_first_free_slot_once_and_keeps_other_entries()
    {
        var store = new FakePolicyStore();
        store.Entries["1"] = Other;

        Assert.Equal("2", ExtensionSelfRegistrar.AddAnchorEntryIfMissing(store));
        Assert.Null(ExtensionSelfRegistrar.AddAnchorEntryIfMissing(store)); // idempotent

        Assert.Equal(2, store.Entries.Count);
        Assert.Equal(Other, store.Entries["1"]);
        Assert.Equal(EdgeExtensionPolicy.ForcelistEntry, store.Entries["2"]);
    }

    // --- Fakes ---------------------------------------------------------------

    private sealed class Harness
    {
        public FakePolicyStore Machine { get; init; } = new();
        public FakePolicyStore User { get; init; } = new();
        public FakeElevatedWriter Elevated { get; init; }
        public FakeStateStore State { get; init; } = new();

        public bool? ConsentAnswer { get; init; }
        public bool ConsentThrows { get; init; }
        public Func<CancellationToken, Task<bool?>>? ConsentOverride { get; set; }
        public bool CheckedIn { get; set; }
        public bool CheckedInAfterGrace { get; init; }

        /// <summary>The Edge profile the extension is installed in on disk; null: none.</summary>
        public string? InstalledIn { get; set; }
        public bool ProbeThrows { get; init; }

        /// <summary>Whether Edge is running; it is unless a spec says otherwise.</summary>
        public bool BrowserRunning { get; set; } = true;
        public bool BrowserThrows { get; init; }

        /// <summary>Edge starts after this many looks for it.</summary>
        public int? BrowserStartsAfterPolls { get; init; }

        /// <summary>Anchor's extension IDs (#497); the store build's only unless a spec says otherwise.</summary>
        public AnchorExtensionIds ExtensionIds { get; init; } = AnchorExtensionIds.StoreOnly;

        /// <summary>Runs after each wait is recorded.</summary>
        public Action<TimeSpan>? OnDelay { get; set; }

        /// <summary>Replaces the instant wait, given what the registrar was waiting for.</summary>
        public Func<ExtensionVerification, CancellationToken, Task>? DelayOverride { get; set; }

        public int ConsentAsked { get; private set; }
        public int GuidedShown { get; private set; }
        public int Delays => Delayed.Count;

        /// <summary>Every wait, and what the registrar was waiting for during it.</summary>
        public List<TimeSpan> Delayed { get; } = new();
        public List<ExtensionVerification> DelayedIn { get; } = new();

        public Harness() => Elevated = new FakeElevatedWriter(Machine);

        public ExtensionSelfRegistrar Registrar()
        {
            ExtensionSelfRegistrar? registrar = null;
            var polls = 0;
            registrar = new(
                Machine,
                User,
                Elevated,
                State,
                askConsent: ct =>
                {
                    ConsentAsked++;
                    if (ConsentThrows) throw new InvalidOperationException("window failed");
                    return ConsentOverride?.Invoke(ct) ?? Task.FromResult(ConsentAnswer);
                },
                extensionCheckedIn: () => CheckedIn,
                showGuidedInstall: _ =>
                {
                    GuidedShown++;
                    return Task.CompletedTask;
                },
                installedProfile: () => ProbeThrows ? throw new IOException("locked") : InstalledIn,
                browserRunning: () => BrowserThrows ? throw new InvalidOperationException("no access") : BrowserRunning,
                delayAsync: (delay, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    var waitingFor = registrar!.Verification;
                    Delayed.Add(delay);
                    DelayedIn.Add(waitingFor);
                    if (waitingFor == ExtensionVerification.WaitingForBrowser &&
                        BrowserStartsAfterPolls is { } after && ++polls >= after)
                    {
                        BrowserRunning = true;
                    }
                    if (waitingFor == ExtensionVerification.WaitingForCheckIn && CheckedInAfterGrace) CheckedIn = true;
                    OnDelay?.Invoke(delay);
                    return DelayOverride?.Invoke(waitingFor, ct) ?? Task.CompletedTask;
                },
                gracePeriod: Grace,
                browserPollInterval: PollInterval,
                extensionIds: ExtensionIds);
            return registrar;
        }
    }

    private sealed class FakeElevatedWriter(FakePolicyStore machine) : IElevatedPolicyWriter
    {
        public ElevatedPolicyWriteResult Result { get; set; } = ElevatedPolicyWriteResult.Succeeded;
        public bool WriteOnSuccess { get; set; } = true;
        public List<(string Slot, string Entry)> AddCalls { get; } = new();
        public List<string> RemoveCalls { get; } = new();

        public Task<ElevatedPolicyWriteResult> AddMachineEntryAsync(
            string valueName, string entry, CancellationToken ct = default)
        {
            AddCalls.Add((valueName, entry));
            if (Result == ElevatedPolicyWriteResult.Succeeded && WriteOnSuccess)
                machine.Entries[valueName] = entry;
            return Task.FromResult(Result);
        }

        public Task StartRemovingMachineEntry(string valueName)
        {
            RemoveCalls.Add(valueName);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStateStore : IExtensionPolicyStateStore
    {
        public ExtensionPolicyState Saved { get; set; } = new();
        public bool ThrowOnSave { get; set; }

        public ExtensionPolicyState Load() => Saved;

        public void Save(ExtensionPolicyState state)
        {
            if (ThrowOnSave) throw new IOException("disk full");
            Saved = state;
        }
    }

    private sealed class FakePolicyStore : IExtensionPolicyStore
    {
        public Dictionary<string, string> Entries { get; } = new();
        public bool ThrowOnRead { get; set; }
        public bool ThrowOnWrite { get; set; }

        public IReadOnlyList<string> GetForcelistEntries() => Read(() => Entries.Values.ToList());
        public IReadOnlyList<string> GetForcelistValueNames() => Read(() => Entries.Keys.ToList());
        public string? GetForcelistValue(string valueName) => Read(() => Entries.GetValueOrDefault(valueName));

        public void AddForcelistEntry(string valueName, string entry)
        {
            if (ThrowOnWrite) throw new UnauthorizedAccessException("locked");
            Entries[valueName] = entry;
        }

        public void RemoveAnchorForcelistEntries()
        {
            if (ThrowOnWrite) throw new UnauthorizedAccessException("locked");
            foreach (var name in Entries.Where(kv => EdgeExtensionPolicy.IsAnchorEntry(kv.Value))
                                        .Select(kv => kv.Key).ToList())
            {
                Entries.Remove(name);
            }
        }

        private T Read<T>(Func<T> read) =>
            ThrowOnRead ? throw new UnauthorizedAccessException("unreadable") : read();
    }
}
