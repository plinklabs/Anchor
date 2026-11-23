using FocusAgent.Core.Dtos;
using FocusAgent.Core.Extension;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using FocusAgent.Core.Tamper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FocusAgent.Core.Tests;

/// <summary>
/// The witness link self-check (#561): when a session is joined with no
/// extension connected, the agent's log says what is wrong with the link's
/// set-up, one line per check, and the host is registered again. On two laptops
/// in the classroom test of 2026-11-20 the extension never connected and the
/// log said nothing (#556). Each spec breaks one piece of a working set-up.
/// </summary>
public sealed class WitnessLinkSelfCheckTests
{
    private const string AgentDir = @"C:\Anchor\current";
    private static readonly string AgentExe = Path.Combine(AgentDir, WitnessHostManifest.HostExeName);
    private static readonly string Manifest = WitnessHostManifest.ManifestPathFor(AgentDir);
    private static readonly string StoreId = EdgeExtensionPolicy.ExtensionId;
    private const string TestRingId = "abcdefghijklmnopabcdefghijklmnop";

    [Fact]
    public void A_working_set_up_reports_nothing_wrong_and_says_what_it_found()
    {
        var h = new Harness();

        Assert.Equal(WitnessLinkProblems.None, h.Run());

        h.Logged(LogLevel.Information, $@"key HKCU\{WitnessHostManifest.RegistryKeyPath}: (default) = {Manifest}");
        h.Logged(LogLevel.Information, $"manifest {Manifest} parses: name net.anchor.witness, path {AgentExe}, allowed_origins [chrome-extension://{StoreId}/]");
        h.Logged(LogLevel.Information, $"host exe {AgentExe} exists; the one next to this agent ({AgentExe}): yes");
        h.Logged(LogLevel.Information, $@"extension {StoreId} is installed in Edge profile Edge\Default, and in allowed_origins");
        h.Logged(LogLevel.Information, "Edge policy (HKLM): NativeMessagingUserLevelHosts not set, NativeMessagingBlocklist not set, NativeMessagingAllowlist not set");
        h.Logged(LogLevel.Information, "Edge policy (HKCU): NativeMessagingUserLevelHosts not set");
        h.Logged(LogLevel.Information, "the set-up looks right");
        h.Logged(LogLevel.Information, "(self-check)", "already up to date");
    }

    [Fact]
    public void No_key()
    {
        var h = new Harness();
        h.Store.ManifestPath = null;

        Assert.Equal(WitnessLinkProblems.NoKey, h.Run());

        h.Logged(LogLevel.Warning, $@"key HKCU\{WitnessHostManifest.RegistryKeyPath}: missing, or without a (default) value");
        // Repaired on the spot: the registration writes the key again, and says so.
        Assert.Equal(Manifest, h.Store.ManifestPath);
        h.Logged(LogLevel.Information, "Wrote witness host key", Manifest);
        h.Logged(LogLevel.Information, "(self-check)", "rewrote it");
    }

    [Fact]
    public void A_key_pointing_at_a_missing_manifest()
    {
        var h = new Harness();
        var gone = @"C:\Anchor\app-0.5.0\net.anchor.witness.json";
        h.Store.ManifestPath = gone;

        Assert.Equal(WitnessLinkProblems.ManifestMissing, h.Run());

        h.Logged(LogLevel.Warning, $"manifest {gone} doesn't exist");
        Assert.Equal(Manifest, h.Store.ManifestPath);
        h.Logged(LogLevel.Information, "Updated witness host key", Manifest);
    }

    [Fact]
    public void A_manifest_that_does_not_parse()
    {
        var h = new Harness();
        h.Store.Files[Manifest] = "{ \"name\": \"net.anchor.witness\", ";

        Assert.Equal(WitnessLinkProblems.ManifestUnreadable, h.Run());

        h.Logged(LogLevel.Warning, $"manifest {Manifest} doesn't parse");
        // The installs are still listed, with nothing to hold them against.
        h.Logged(LogLevel.Information, $@"extension {StoreId} is installed in Edge profile Edge\Default.");
        h.Logged(LogLevel.Information, "Wrote witness host manifest", Manifest);
    }

    [Fact]
    public void A_manifest_that_exists_but_cannot_be_read()
    {
        var h = new Harness();
        h.Store.Unreadable.Add(Manifest);

        Assert.Equal(WitnessLinkProblems.ManifestUnreadable, h.Run());

        h.Logged(LogLevel.Warning, $"manifest {Manifest} exists but can't be read");
    }

    [Fact]
    public void A_manifest_for_another_host()
    {
        var h = new Harness();
        h.Store.Files[Manifest] = WitnessHostManifest.BuildManifest(AgentExe).Replace("net.anchor.witness", "com.example.other");

        Assert.Equal(WitnessLinkProblems.ManifestWrongName, h.Run());

        h.Logged(LogLevel.Warning, "the manifest names host com.example.other, not net.anchor.witness");
    }

    [Fact]
    public void A_missing_exe()
    {
        var h = new Harness();
        var gone = @"C:\Anchor\app-0.5.0\anchor-witness-host.exe";
        h.Store.Files[Manifest] = WitnessHostManifest.BuildManifest(gone);

        Assert.Equal(WitnessLinkProblems.HostExeMissing, h.Run());

        h.Logged(LogLevel.Warning, $"host exe {gone} doesn't exist; the one next to this agent is {AgentExe}");
    }

    [Fact]
    public void An_exe_that_is_not_the_one_next_to_this_agent()
    {
        var h = new Harness();
        var dev = @"C:\src\Anchor\agent\src\FocusAgent.WitnessHost\bin\Debug\net10.0\anchor-witness-host.exe";
        h.Store.Exes.Add(dev);
        h.Store.Files[Manifest] = WitnessHostManifest.BuildManifest(dev);

        Assert.Equal(WitnessLinkProblems.HostExeNotThisAgents, h.Run());

        h.Logged(LogLevel.Information, $"host exe {dev} exists; the one next to this agent ({AgentExe}): no");
    }

    [Fact]
    public void An_installed_id_missing_from_allowed_origins()
    {
        // A test-ring laptop with the self-hosted build, and a manifest written by
        // an agent that only knows the store build.
        var h = new Harness(installs: [new InstalledExtension(@"Edge\Profile 1", TestRingId)]);

        Assert.Equal(WitnessLinkProblems.ExtensionNotAllowed, h.Run());

        h.Logged(LogLevel.Warning, $@"extension {TestRingId} is installed in Edge profile Edge\Profile 1, but not in allowed_origins");
    }

    [Fact]
    public void No_extension_installed()
    {
        var h = new Harness(installs: []);

        Assert.Equal(WitnessLinkProblems.NoExtensionInstalled, h.Run());

        h.Logged(LogLevel.Warning, "no Edge profile has one of Anchor's extensions installed");
    }

    [Fact]
    public void A_policy_that_blocks_user_level_hosts()
    {
        var h = new Harness();
        h.Policies.Machine = new EdgeNativeMessagingPolicy(UserLevelHosts: false, null, null);

        Assert.Equal(WitnessLinkProblems.UserLevelHostsBlocked, h.Run());

        h.Logged(LogLevel.Information, "Edge policy (HKLM): NativeMessagingUserLevelHosts 0");
        h.Logged(LogLevel.Warning, "NativeMessagingUserLevelHosts lets only machine-wide hosts start");
    }

    [Fact]
    public void A_user_policy_blocking_user_level_hosts_counts_unless_the_machine_policy_allows_them()
    {
        var h = new Harness();
        h.Policies.User = new EdgeNativeMessagingPolicy(UserLevelHosts: false, null, null);
        Assert.Equal(WitnessLinkProblems.UserLevelHostsBlocked, h.Run());

        // The machine's policy wins over the user's.
        h.Policies.Machine = new EdgeNativeMessagingPolicy(UserLevelHosts: true, null, null);
        Assert.Equal(WitnessLinkProblems.None, h.Run());
    }

    [Fact]
    public void A_blocklist_that_blocks_every_host_unless_the_allowlist_names_ours()
    {
        var h = new Harness();
        h.Policies.Machine = new EdgeNativeMessagingPolicy(null, ["*"], null);

        Assert.Equal(WitnessLinkProblems.HostBlocklisted, h.Run());
        h.Logged(LogLevel.Information, "Edge policy (HKLM): NativeMessagingUserLevelHosts not set, NativeMessagingBlocklist [*], NativeMessagingAllowlist not set");
        h.Logged(LogLevel.Warning, "NativeMessagingBlocklist blocks net.anchor.witness");

        h.Policies.Machine = new EdgeNativeMessagingPolicy(null, ["*"], ["com.example.other", "net.anchor.witness"]);
        Assert.Equal(WitnessLinkProblems.None, h.Run());

        h.Policies.Machine = new EdgeNativeMessagingPolicy(null, ["net.anchor.witness"], null);
        Assert.Equal(WitnessLinkProblems.HostBlocklisted, h.Run());
    }

    [Fact]
    public void A_check_that_throws_is_logged_and_the_others_still_run()
    {
        var h = new Harness();
        h.Policies.Throw = true;

        var problems = WitnessLinkProblems.None;
        Assert.Null(Record.Exception(() => problems = h.Run()));

        Assert.Equal(WitnessLinkProblems.None, problems);
        h.Logged(LogLevel.Warning, "checking Edge's policies failed", "registry is locked");
        h.Logged(LogLevel.Information, "(self-check)", "already up to date");
    }

    [Fact]
    public void Without_the_host_exe_next_to_the_agent_it_says_so_and_leaves_the_registration_alone()
    {
        var h = new Harness();
        h.Store.ManifestPath = null;
        h.Store.Exes.Remove(AgentExe);

        h.Run();

        Assert.Null(h.Store.ManifestPath);
        h.Logged(LogLevel.Warning, $"Witness host registration (self-check): {AgentExe} doesn't exist");
    }

    [Fact]
    public void The_student_s_own_folder_is_written_as_its_variable()
    {
        // #565: the log leaves the laptop; the student's user name has no business in it.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var agentDir = Path.Combine(localAppData, "Anchor.Agent", "current");
        var h = new Harness(agentDir: agentDir);

        h.Run();

        var text = h.Log.Text() + h.RegistrarLog.Text();
        Assert.Contains(@"%LOCALAPPDATA%\Anchor.Agent\current\net.anchor.witness.json", text);
        Assert.DoesNotContain(localAppData, text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- when it runs ------------------------------------------------------

    [Fact]
    public async Task A_session_joined_with_no_extension_connected_runs_it_once()
    {
        var h = new Harness();
        h.Store.ManifestPath = null;
        using var onJoin = h.OnJoin(out var coordinator, out _);

        var sessionId = await Join(coordinator);
        await onJoin.LastCheck;

        h.Logged(LogLevel.Warning, $"session {sessionId} joined with no extension connected");
        h.Logged(LogLevel.Warning, "missing, or without a (default) value");
        Assert.Equal(Manifest, h.Store.ManifestPath);

        // The same session again (a reconnect rejoins it): not checked twice.
        h.Log.Entries.Clear();
        await Join(coordinator, sessionId);
        await onJoin.LastCheck;
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task A_session_joined_with_the_extension_connected_says_so_and_checks_nothing()
    {
        var h = new Harness();
        h.Store.ManifestPath = null;
        var joinLog = new RecordingLogger<WitnessLinkSelfCheckOnJoin>();
        using var onJoin = h.OnJoin(out var coordinator, out var transport, joinLog);
        transport.RaiseConnected();

        var sessionId = await Join(coordinator);
        await onJoin.LastCheck;

        Assert.Empty(h.Log.Entries);
        Assert.Null(h.Store.ManifestPath);
        Assert.Contains(joinLog.Entries, e => e.Message.Contains($"Session {sessionId} joined with the extension connected (1 link(s))"));
    }

    private static async Task<Guid> Join(SessionCoordinator coordinator, Guid? sessionId = null)
    {
        var payload = new SessionStartedPayload(
            SessionId: sessionId ?? Guid.NewGuid(),
            ClassId: Guid.NewGuid(),
            StartedAt: DateTimeOffset.UnixEpoch,
            JoinCode: "123456",
            Apps: [],
            Domains: []);
        await coordinator.HandleSessionStartedAsync(payload);
        return payload.SessionId;
    }

    private sealed class Harness
    {
        private readonly IReadOnlyList<InstalledExtension> _installs;
        private readonly string _agentExe;

        public Harness(IReadOnlyList<InstalledExtension>? installs = null, string agentDir = AgentDir)
        {
            _installs = installs ?? [new InstalledExtension(@"Edge\Default", StoreId)];
            _agentExe = Path.Combine(agentDir, WitnessHostManifest.HostExeName);
            var manifest = WitnessHostManifest.ManifestPathFor(agentDir);
            Store.Exes.Add(_agentExe);
            Store.Files[manifest] = WitnessHostManifest.BuildManifest(_agentExe, AnchorExtensionIds.StoreOnly);
            Store.ManifestPath = manifest;
        }

        public FakeWitnessHostStore Store { get; } = new();
        public FakePolicies Policies { get; } = new();
        public RecordingLogger<WitnessLinkSelfCheck> Log { get; } = new();
        public RecordingLogger<WitnessHostRegistrar> RegistrarLog { get; } = new();

        public WitnessLinkSelfCheck Check() => new(
            Store,
            Policies,
            () => _installs,
            _agentExe,
            new WitnessHostRegistrar(Store, RegistrarLog, AnchorExtensionIds.StoreOnly),
            Log);

        public WitnessLinkProblems Run() => Check().Run(Guid.NewGuid());

        public WitnessLinkSelfCheckOnJoin OnJoin(
            out SessionCoordinator coordinator,
            out FakeTransport transport,
            RecordingLogger<WitnessLinkSelfCheckOnJoin>? log = null)
        {
            coordinator = new SessionCoordinator(
                new SchoolProfileGuardTests.FakeHub(),
                new ConfirmingUi(),
                Options.Create(new RealtimeSettings { JoinConfirmationDuration = TimeSpan.FromSeconds(5) }),
                new FakeTimeProvider(DateTimeOffset.UnixEpoch));
            transport = new FakeTransport();
            return new WitnessLinkSelfCheckOnJoin(coordinator, new ExtensionWitnessMonitor(transport), Check(), log);
        }

        /// <summary>A line at <paramref name="level"/> from the check or the registration that holds every one of <paramref name="parts"/>.</summary>
        public void Logged(LogLevel level, params string[] parts)
        {
            var entries = Log.Entries.Concat(RegistrarLog.Entries).ToList();
            Assert.True(
                entries.Any(e => e.Level == level && parts.All(p => e.Message.Contains(p, StringComparison.Ordinal))),
                $"No [{level}] line with \"{string.Join("\" and \"", parts)}\" in:{Environment.NewLine}{Log.Text()}{Environment.NewLine}{RegistrarLog.Text()}");
        }
    }

    internal sealed class FakePolicies : IEdgeNativeMessagingPolicySource
    {
        public EdgeNativeMessagingPolicy Machine { get; set; } = EdgeNativeMessagingPolicy.None;
        public EdgeNativeMessagingPolicy User { get; set; } = EdgeNativeMessagingPolicy.None;
        public bool Throw { get; set; }

        public EdgeNativeMessagingPolicy Read(bool machine) =>
            Throw ? throw new UnauthorizedAccessException("registry is locked") : machine ? Machine : User;
    }

    internal sealed class FakeTransport : IExtensionWitnessTransport
    {
        public event EventHandler<WitnessConnection>? WitnessConnected;
#pragma warning disable CS0067
        public event EventHandler<WitnessConnection>? WitnessDisconnected;
#pragma warning restore CS0067
        public void RaiseConnected() => WitnessConnected?.Invoke(this, new WitnessConnection(1));
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ConfirmingUi : ISessionUiHost
    {
        public Task<JoinDecision> ShowJoinConfirmationAsync(JoinConfirmation confirmation, CancellationToken ct = default)
            => Task.FromResult(JoinDecision.Confirmed);
        public void DismissJoinConfirmation() { }
    }
}
