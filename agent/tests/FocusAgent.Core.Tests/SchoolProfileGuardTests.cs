using FocusAgent.Core.Auth;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using FocusAgent.Core.Tamper;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FocusAgent.Core.Tests;

/// <summary>
/// #525: during a session only the Edge profile signed in with the school account
/// may be used. The guard looks it up every few seconds, hands the windows outside
/// it (another profile, Guest, InPrivate) that are on screen to the focus
/// controller, says when there is no school profile at all, and tells the teacher
/// only what the student doesn't resolve in time.
/// </summary>
public sealed class SchoolProfileGuardTests : IDisposable
{
    private const string SchoolOid = "28c4a3c2-1f0e-4f55-9a7c-6d3f0a1b2c3d";
    private const string SchoolAddress = "ada.lovelace@student.school.be";
    private static readonly TimeSpan Scan = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Report = TimeSpan.FromSeconds(60);

    // The windows, as Edge 157 names and titles them.
    private static readonly BrowserWindowInfo School = new(0x10, "MSEdge.UserData.Profile1", "Lesson - School - Microsoft​ Edge", false);
    private static readonly BrowserWindowInfo SchoolInPrivate = new(0x11, "MSEdge.UserData.Profile1", "about:blank - [InPrivate] - Microsoft​ Edge", false);
    private static readonly BrowserWindowInfo Personal = new(0x20, "MSEdge", "News - Personal - Microsoft​ Edge", false);
    private static readonly BrowserWindowInfo Guest = new(0x30, "MSEdge.UserData.GuestProfile", "about:blank - [Guest] - Microsoft​ Edge", false);

    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Windows_outside_the_school_profile_on_screen_are_handed_over_a_scan_after_the_join()
    {
        _h.Windows.Open(School, SchoolInPrivate, Personal, Guest with { Minimized = true });
        _h.ExtensionConnected();

        await _h.JoinAsync();
        _h.FirstScan();

        Assert.Equal(SchoolProfileStatus.Found, _h.Guard.Status);
        Assert.Equal("Profile 1", _h.Guard.Profile);
        // The minimized Guest window is outside it too, but down already.
        Assert.Equal(new nint[] { 0x11, 0x20, 0x30 }, _h.Guard.OutsideWindows.Order());
        Assert.Equal(new nint[] { 0x11, 0x20 }, Assert.Single(_h.OutsideEvents).Order());

        // Every scan hands over what is on screen again: a window the student
        // restored without a foreground change goes down too.
        _h.Clock.Advance(Scan);
        Assert.Equal(2, _h.OutsideEvents.Count);
    }

    [Fact]
    public async Task Only_an_ordinary_window_of_the_school_profile_is_a_school_profile_window()
    {
        _h.Windows.Open(School, SchoolInPrivate, Personal, Guest);
        _h.ExtensionConnected();

        // Outside a session nothing can be told.
        Assert.False(_h.Guard.IsSchoolProfileWindow(School.Handle));

        await _h.JoinAsync();
        _h.FirstScan();

        Assert.True(_h.Guard.IsSchoolProfileWindow(School.Handle));
        Assert.False(_h.Guard.IsSchoolProfileWindow(SchoolInPrivate.Handle));
        Assert.False(_h.Guard.IsSchoolProfileWindow(Personal.Handle));
        Assert.False(_h.Guard.IsSchoolProfileWindow(Guest.Handle));
        Assert.False(_h.Guard.IsOutsideSchoolProfile(School.Handle));
        Assert.True(_h.Guard.IsOutsideSchoolProfile(SchoolInPrivate.Handle));
        Assert.True(_h.Guard.IsOutsideSchoolProfile(Personal.Handle));
        Assert.True(_h.Guard.IsOutsideSchoolProfile(Guest.Handle));
        // A window that is gone is no window of any profile.
        Assert.False(_h.Guard.IsOutsideSchoolProfile(0x99));
    }

    [Fact]
    public async Task The_school_profile_s_window_on_screen_is_the_one_to_bring_the_student_to()
    {
        var minimized = School with { Handle = 0x12, Minimized = true };
        _h.Windows.Open(minimized, Personal, School);
        _h.ExtensionConnected();
        await _h.JoinAsync();
        _h.FirstScan();

        Assert.Equal(School.Handle, _h.Guard.FindSchoolProfileWindow());

        _h.Windows.Close(School.Handle);
        Assert.Equal(minimized.Handle, _h.Guard.FindSchoolProfileWindow());

        _h.Windows.Close(minimized.Handle);
        Assert.Equal(IntPtr.Zero, _h.Guard.FindSchoolProfileWindow());
    }

    [Fact]
    public async Task Without_a_profile_signed_in_with_the_school_account_it_says_so_and_again_once_there_is_one()
    {
        _h.Profiles.WriteLocalState(("Default", "ada.personal@outlook.com", null), ("Profile 1", "", null));
        _h.Windows.Open(School, Personal);
        _h.ExtensionConnected();
        await _h.JoinAsync();
        _h.FirstScan();

        Assert.True(_h.Guard.IsMissing);
        Assert.Equal(new[] { true }, _h.MissingEvents);
        // Edge is paused as a whole for it; no window is handed over on its own.
        Assert.Empty(_h.OutsideEvents);
        Assert.False(_h.Guard.IsSchoolProfileWindow(School.Handle));

        // The student signs in to Profile 1 with the school account.
        _h.Profiles.WriteLocalState(("Default", "ada.personal@outlook.com", null), ("Profile 1", SchoolAddress, SchoolOid));
        _h.Clock.Advance(Scan);

        Assert.False(_h.Guard.IsMissing);
        Assert.Equal(new[] { true, false }, _h.MissingEvents);
        Assert.Empty(_h.Reporter.Calls);
    }

    [Fact]
    public async Task A_window_outside_the_school_profile_left_open_is_told_to_the_teacher_once()
    {
        _h.Windows.Open(School, Personal);
        _h.ExtensionConnected();
        var sessionId = await _h.JoinAsync();
        _h.FirstScan();

        _h.Run(Report - Scan);
        Assert.Empty(_h.Reporter.Calls);

        _h.Run(Scan);
        Assert.Equal(new[] { (sessionId, TamperKinds.OutsideSchoolProfile) }, _h.Reporter.Calls);

        // Once per outage.
        _h.Run(Report * 3);
        Assert.Single(_h.Reporter.Calls);

        // Closed, then another one opened: a new one, told again if left open.
        _h.Windows.Close(Personal.Handle);
        _h.Run(Scan);
        _h.Windows.Open(Guest);
        _h.Run(Report + Scan);
        Assert.Equal(2, _h.Reporter.Calls.Count(c => c.Kind == TamperKinds.OutsideSchoolProfile));
    }

    [Fact]
    public async Task A_window_closed_in_time_and_an_inprivate_window_are_not_told_by_this_guard()
    {
        _h.Windows.Open(School, Personal, SchoolInPrivate);
        _h.ExtensionConnected();
        await _h.JoinAsync();
        _h.FirstScan();

        _h.Run(Report / 2);
        _h.Windows.Close(Personal.Handle);
        _h.Run(Report * 2);

        // The InPrivate window is still open, paused: it has its own report (#148).
        Assert.Contains(SchoolInPrivate.Handle, _h.Guard.OutsideWindows);
        Assert.Empty(_h.Reporter.Calls);
    }

    [Fact]
    public async Task No_school_profile_left_unresolved_is_told_to_the_teacher_once()
    {
        _h.Profiles.WriteLocalState(("Profile 1", "", null));
        _h.Windows.Open(School);
        _h.ExtensionConnected();
        var sessionId = await _h.JoinAsync();
        _h.FirstScan();

        _h.Run(Report - Scan);
        Assert.Empty(_h.Reporter.Calls);
        _h.Run(Scan);
        _h.Run(Report * 2);

        Assert.Equal(new[] { (sessionId, TamperKinds.SchoolProfileMissing) }, _h.Reporter.Calls);
    }

    [Fact]
    public async Task A_device_where_the_extension_never_connected_is_left_alone()
    {
        _h.Windows.Open(School, Personal);
        await _h.JoinAsync();
        _h.FirstScan();
        _h.Run(Report * 2);

        Assert.Empty(_h.OutsideEvents);
        Assert.Empty(_h.Reporter.Calls);
    }

    [Fact]
    public async Task Without_edge_s_local_state_the_rule_is_off()
    {
        File.Delete(Path.Combine(_h.Profiles.UserData, "Local State"));
        _h.Windows.Open(School, Personal);
        _h.ExtensionConnected();
        await _h.JoinAsync();
        _h.FirstScan();
        _h.Run(Report * 2);

        Assert.Equal(SchoolProfileStatus.Unknown, _h.Guard.Status);
        Assert.Empty(_h.OutsideEvents);
        Assert.Empty(_h.MissingEvents);
        Assert.Empty(_h.Reporter.Calls);
    }

    [Fact]
    public async Task When_the_session_ends_nothing_is_outside_or_missing_any_more()
    {
        _h.Profiles.WriteLocalState(("Profile 1", "", null));
        _h.Windows.Open(School, Personal);
        _h.ExtensionConnected();
        var sessionId = await _h.JoinAsync();
        _h.FirstScan();
        Assert.True(_h.Guard.IsMissing);

        _h.Hub.RaiseSessionEnded(sessionId);
        _h.Run(Report * 2);

        Assert.Equal(SchoolProfileStatus.Unknown, _h.Guard.Status);
        Assert.Empty(_h.Guard.OutsideWindows);
        Assert.Equal(new[] { true }, _h.MissingEvents);
        Assert.Empty(_h.Reporter.Calls);
    }

    private sealed class Harness : IDisposable
    {
        public FakeHub Hub { get; } = new();
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public FakeTransport Transport { get; } = new();
        public FakeWindowReader Windows { get; } = new();
        public FakeEdgeProfiles Profiles { get; } = new();
        public RecordingReporter Reporter { get; } = new();
        public SessionCoordinator Coordinator { get; }
        public SchoolProfileGuard Guard { get; }
        public List<IReadOnlyList<nint>> OutsideEvents { get; } = new();
        public List<bool> MissingEvents { get; } = new();

        public Harness()
        {
            // Default: a personal profile; Profile 1: the school profile. Both have
            // the extension.
            Profiles.SwitchOn("Default");
            Profiles.SwitchOn("Profile 1");
            Profiles.WriteLocalState(("Default", "ada.personal@outlook.com", null), ("Profile 1", SchoolAddress, SchoolOid));

            Coordinator = new SessionCoordinator(
                Hub,
                new ConfirmingUi(),
                Options.Create(new RealtimeSettings { JoinConfirmationDuration = TimeSpan.FromSeconds(5) }),
                Clock);
            var account = new SignedInAccount { Current = new SchoolAccount(SchoolAddress, SchoolOid) };
            var probe = new EdgeSchoolProfileProbe(
                Profiles.UserData,
                new EdgeOpenProfileExtensionProbe(Profiles.UserData, new NoAppIds(), "msedge", new[] { FakeEdgeProfiles.ExtensionId }));
            Guard = new SchoolProfileGuard(
                Coordinator,
                new ExtensionWitnessMonitor(Transport),
                probe,
                Windows,
                account,
                Reporter,
                Options.Create(new SessionSettings
                {
                    SchoolProfileScanSeconds = (int)Scan.TotalSeconds,
                    SchoolProfileReportSeconds = (int)Report.TotalSeconds,
                }),
                Options.Create(new WitnessSettings()),
                Clock);
            Guard.WindowsOutside += (_, windows) => OutsideEvents.Add(windows);
            Guard.MissingChanged += (_, missing) => MissingEvents.Add(missing);
        }

        public void ExtensionConnected() => Transport.RaiseConnected();

        /// <summary>The first scan, one interval after the join.</summary>
        public void FirstScan() => Clock.Advance(Scan);

        /// <summary>
        /// Lets <paramref name="duration"/> pass a second at a time: each scan reads
        /// the clock, and one long advance would hand them all its end.
        /// </summary>
        public void Run(TimeSpan duration)
        {
            for (var left = duration; left > TimeSpan.Zero; left -= TimeSpan.FromSeconds(1))
                Clock.Advance(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
        }

        public async Task<Guid> JoinAsync()
        {
            var payload = new SessionStartedPayload(
                SessionId: Guid.NewGuid(),
                ClassId: Guid.NewGuid(),
                StartedAt: DateTimeOffset.UnixEpoch,
                JoinCode: "123456",
                Apps: new[] { new AllowedAppDto("ProcessName", "msedge") },
                Domains: Array.Empty<AllowedDomainDto>());
            await Coordinator.HandleSessionStartedAsync(payload);
            Assert.Equal(payload.SessionId, Coordinator.JoinedSessionId);
            return payload.SessionId;
        }

        public void Dispose()
        {
            Guard.Dispose();
            Profiles.Dispose();
        }
    }

    /// <summary>Edge's windows, as the native reader gives them.</summary>
    internal sealed class FakeWindowReader : IBrowserWindowReader
    {
        private readonly List<BrowserWindowInfo> _open = new();

        public void Open(params BrowserWindowInfo[] windows)
        {
            lock (_open) _open.AddRange(windows);
        }

        public void Close(nint handle)
        {
            lock (_open) _open.RemoveAll(w => w.Handle == handle);
        }

        public IReadOnlyList<BrowserWindowInfo> GetBrowserWindows(string processName)
        {
            lock (_open) return _open.ToArray();
        }

        public BrowserWindowInfo? Describe(nint windowHandle)
        {
            lock (_open) return _open.FirstOrDefault(w => w.Handle == windowHandle);
        }
    }

    private sealed class NoAppIds : IBrowserWindowAppIds
    {
        public IReadOnlyList<string> GetWindowAppIds(string processName) => Array.Empty<string>();
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

    internal sealed class RecordingReporter : ITamperReporter
    {
        public List<(Guid SessionId, string Kind)> Calls { get; } = new();

        public Task ReportAsync(Guid sessionId, string kind, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add((sessionId, kind));
            return Task.CompletedTask;
        }
    }

    private sealed class ConfirmingUi : ISessionUiHost
    {
        public Task<JoinDecision> ShowJoinConfirmationAsync(JoinConfirmation confirmation, CancellationToken ct = default)
            => Task.FromResult(JoinDecision.Confirmed);
        public void DismissJoinConfirmation() { }
    }

    internal sealed class FakeHub : ISessionHubConnection
    {
        public AgentConnectionState State => AgentConnectionState.Connected;
#pragma warning disable CS0067
        public event EventHandler<AgentConnectionState>? StateChanged;
        public event EventHandler<SessionStartedPayload>? SessionStarted;
        public event EventHandler<SessionBundlesUpdatedPayload>? SessionBundlesUpdated;
        public event EventHandler<AllowlistAmendedPayload>? AllowlistAmended;
        public event EventHandler<UnblockDeniedPayload>? UnblockDenied;
#pragma warning restore CS0067
        public event EventHandler<Guid>? SessionEnded;

        public void RaiseSessionEnded(Guid sessionId) => SessionEnded?.Invoke(this, sessionId);

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task JoinSessionAsync(Guid sessionId, string? joinCode, CancellationToken ct = default) => Task.CompletedTask;
        public Task LeaveSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclineSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReportEventAsync(Guid sessionId, string kind, string payloadJson, DateTimeOffset? occurredAt = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExtensionHeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> HeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> IsInSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<SessionStartedPayload?> GetStartedSessionAsync(CancellationToken ct = default) => Task.FromResult<SessionStartedPayload?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
