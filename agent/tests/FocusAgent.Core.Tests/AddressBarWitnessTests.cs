using System.Text.Json;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using FocusAgent.Core.Tamper;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FocusAgent.Core.Tests;

/// <summary>
/// #526 (test phase only, #515): during a session the agent reads the address bar
/// of the school profile's foreground Edge window and reports an off-list host the
/// extension didn't block, once, by its host only. Nothing is read outside a
/// session, nothing is kept or sent for an allowed site, and the block page,
/// Edge's own pages, what the student types and a navigation the extension is
/// about to redirect are left alone.
/// </summary>
public sealed class AddressBarWitnessTests : IDisposable
{
    private static readonly TimeSpan Scan = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);
    private const nint SchoolWindow = 0x10;
    private const nint OtherWindow = 0x20;

    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task An_off_list_page_left_open_past_the_grace_is_reported_once_by_its_host_only()
    {
        var sessionId = await _h.JoinAsync();
        _h.Reader.Show(SchoolWindow, "offlist.test:8099/some/path?q=secret");

        // First seen by the scan at 2 s; within the grace period at 4 s and 6 s.
        _h.Run(Scan + Grace);
        Assert.Empty(_h.Hub.Reports);

        // Past it at 8 s.
        _h.Run(Scan);
        var report = Assert.Single(_h.Hub.Reports);
        Assert.Equal(sessionId, report.SessionId);
        Assert.Equal("UnfilteredSite", report.Kind);
        // The host, nothing else: no URL, path, query string or port.
        Assert.Equal("""{"host":"offlist.test"}""", report.PayloadJson);
        // Dated when the address bar first showed it.
        Assert.Equal(DateTimeOffset.UnixEpoch + Scan, report.OccurredAt);
        Assert.Equal(new[] { "offlist.test" }, _h.Witness.Reported);

        // It stays open, the student goes elsewhere and comes back: still once.
        _h.Run(TimeSpan.FromSeconds(20));
        _h.Reader.Show(SchoolWindow, "https://other.test/");
        _h.Run(Grace + Scan + Scan);
        _h.Reader.Show(SchoolWindow, "http://offlist.test/again");
        _h.Run(TimeSpan.FromSeconds(20));
        Assert.Equal(new[] { "offlist.test", "other.test" }, _h.Hub.Reports.Select(HostOf));
    }

    [Fact]
    public async Task A_navigation_the_extension_redirects_within_the_grace_is_not_reported()
    {
        await _h.JoinAsync();
        _h.Reader.Show(SchoolWindow, "offlist.test/page");
        _h.Run(Scan + Scan);

        // The extension sends the tab to its block page.
        _h.Reader.Show(SchoolWindow, "extension://dnkimhodjfogjibnbbfdjdapgmmiojio/block-page.html?url=http%3A%2F%2Foff.test%2F");
        _h.Run(TimeSpan.FromSeconds(30));

        // Back to it later: its grace starts over.
        _h.Reader.Show(SchoolWindow, "offlist.test/page");
        _h.Run(Scan + Scan);
        _h.Reader.Show(SchoolWindow, "127.0.0.1:5276/allowed");
        _h.Run(TimeSpan.FromSeconds(30));

        Assert.Empty(_h.Hub.Reports);
        Assert.True(_h.Witness.Reads > 0);
    }

    [Theory]
    [InlineData("127.0.0.1:5276/allowed")]
    [InlineData("https://login.microsoftonline.com/common/oauth2?client_id=x")]
    [InlineData("granted.test/after-approval")]
    [InlineData("extension://dnkimhodjfogjibnbbfdjdapgmmiojio/block-page.html?url=http%3A%2F%2Foff.test%2F")]
    [InlineData("edge://settings")]
    [InlineData("about:blank")]
    [InlineData("")]
    [InlineData("C:/Users/student/notes.pdf")]
    public async Task Allowed_sites_the_block_page_and_Edge_s_own_pages_are_never_reported(string shown)
    {
        await _h.JoinAsync();
        // A host the teacher approved mid-session: the extension was handed it.
        _h.Domains.Add(new AllowedDomainDto("Exact", "granted.test"));
        _h.Reader.Show(SchoolWindow, shown);

        _h.Run(TimeSpan.FromSeconds(60));

        Assert.Empty(_h.Hub.Reports);
        Assert.Empty(_h.Witness.Reported);
    }

    [Fact]
    public async Task What_the_student_types_in_the_address_bar_is_never_read_as_a_page()
    {
        await _h.JoinAsync();
        _h.Reader.Show(SchoolWindow, "offlist.test", hasFocus: true);

        _h.Run(TimeSpan.FromSeconds(60));

        Assert.Empty(_h.Hub.Reports);
    }

    [Fact]
    public async Task Only_the_foreground_window_of_the_school_profile_is_read()
    {
        await _h.JoinAsync();
        // Another profile's window (paused by #525's rule, but say it came forward).
        _h.Reader.Show(OtherWindow, "offlist.test/page");
        _h.Run(TimeSpan.FromSeconds(60));
        Assert.Equal(0, _h.Witness.Reads);

        // Edge isn't in front at all.
        _h.Reader.Foreground = IntPtr.Zero;
        _h.Run(TimeSpan.FromSeconds(60));
        Assert.Equal(0, _h.Witness.Reads);
        Assert.Empty(_h.Hub.Reports);

        // A switch of windows starts the grace over: the same host in the school
        // profile's window counts from when it shows there.
        _h.Reader.Show(SchoolWindow, "offlist.test/page");
        _h.Run(Scan + Scan);
        _h.Reader.Foreground = IntPtr.Zero;
        _h.Run(Scan);
        _h.Reader.Foreground = SchoolWindow;
        _h.Run(Scan + Scan);
        Assert.Empty(_h.Hub.Reports);
        _h.Run(Grace);
        Assert.Equal(new[] { "offlist.test" }, _h.Hub.Reports.Select(HostOf));
        Assert.Equal("msedge", _h.Reader.AskedFor);
    }

    [Fact]
    public async Task Nothing_is_read_outside_a_session_and_a_new_session_reports_afresh()
    {
        _h.Reader.Show(SchoolWindow, "offlist.test/page");
        _h.Run(TimeSpan.FromSeconds(60));
        Assert.Equal(0, _h.Witness.Reads);
        Assert.False(_h.Witness.Watching);

        var first = await _h.JoinAsync();
        Assert.True(_h.Witness.Watching);
        _h.Run(TimeSpan.FromSeconds(10));
        Assert.Single(_h.Hub.Reports);

        _h.Hub.RaiseSessionEnded(first);
        Assert.False(_h.Witness.Watching);
        Assert.Empty(_h.Witness.Reported);
        var reads = _h.Witness.Reads;
        _h.Run(TimeSpan.FromSeconds(60));
        Assert.Equal(reads, _h.Witness.Reads);

        var second = await _h.JoinAsync();
        _h.Run(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { first, second }, _h.Hub.Reports.Select(r => r.SessionId));
    }

    [Fact]
    public async Task An_address_bar_that_can_t_be_read_reports_nothing()
    {
        await _h.JoinAsync();
        _h.Reader.Foreground = SchoolWindow;
        _h.Reader.Unreadable = true;

        _h.Run(TimeSpan.FromSeconds(60));

        Assert.Empty(_h.Hub.Reports);
        Assert.True(_h.Witness.Reads > 0);
    }

    [Fact]
    public async Task A_report_that_fails_to_send_is_not_retried()
    {
        await _h.JoinAsync();
        _h.Hub.FailReports = true;
        _h.Reader.Show(SchoolWindow, "offlist.test/page");

        _h.Run(TimeSpan.FromSeconds(60));

        Assert.Single(_h.Hub.Reports);
        Assert.Equal(new[] { "offlist.test" }, _h.Witness.Reported);
    }

    private static string? HostOf((Guid SessionId, string Kind, string PayloadJson, DateTimeOffset? OccurredAt) report)
    {
        using var doc = JsonDocument.Parse(report.PayloadJson);
        return doc.RootElement.GetProperty("host").GetString();
    }

    private sealed class Harness : IDisposable
    {
        public FakeHub Hub { get; } = new();
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public FakeAddressBar Reader { get; } = new();
        public List<AllowedDomainDto> Domains { get; } = new();
        public SessionCoordinator Coordinator { get; }
        public AddressBarWitness Witness { get; }

        public Harness()
        {
            Coordinator = new SessionCoordinator(
                Hub,
                new ConfirmingUi(),
                Options.Create(new RealtimeSettings { JoinConfirmationDuration = TimeSpan.FromSeconds(5) }),
                Clock);
            Witness = new AddressBarWitness(
                Coordinator,
                Reader,
                window => window == SchoolWindow,
                () => Domains.ToArray(),
                Hub,
                Options.Create(new SessionSettings
                {
                    AddressBarScanSeconds = (int)Scan.TotalSeconds,
                    AddressBarGraceSeconds = (int)Grace.TotalSeconds,
                }),
                Options.Create(new WitnessSettings { BrowserProcessName = "msedge.exe" }),
                Clock);
        }

        /// <summary>Lets <paramref name="duration"/> pass a second at a time, so every scan reads the clock it falls on.</summary>
        public void Run(TimeSpan duration)
        {
            for (var left = duration; left > TimeSpan.Zero; left -= TimeSpan.FromSeconds(1))
                Clock.Advance(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
        }

        public async Task<Guid> JoinAsync()
        {
            Domains.Clear();
            Domains.AddRange(new[]
            {
                new AllowedDomainDto("Wildcard", "*.microsoftonline.com"),
                new AllowedDomainDto("Exact", "127.0.0.1"),
            });
            var payload = new SessionStartedPayload(
                SessionId: Guid.NewGuid(),
                ClassId: Guid.NewGuid(),
                StartedAt: Clock.GetUtcNow(),
                JoinCode: "123456",
                Apps: new[] { new AllowedAppDto("ProcessName", "msedge") },
                Domains: Domains.ToArray());
            await Coordinator.HandleSessionStartedAsync(payload);
            Assert.Equal(payload.SessionId, Coordinator.JoinedSessionId);
            return payload.SessionId;
        }

        public void Dispose() => Witness.Dispose();
    }

    /// <summary>Edge's foreground window and what its address bar shows.</summary>
    private sealed class FakeAddressBar : IAddressBarReader
    {
        private readonly object _gate = new();
        private readonly Dictionary<nint, AddressBarReading> _bars = new();
        public nint Foreground { get; set; }
        public bool Unreadable { get; set; }
        public string? AskedFor { get; private set; }

        public void Show(nint window, string text, bool hasFocus = false)
        {
            lock (_gate)
            {
                _bars[window] = new AddressBarReading(text, hasFocus);
                Foreground = window;
            }
        }

        public nint ForegroundWindowOf(string processName)
        {
            AskedFor = processName;
            return Foreground;
        }

        public AddressBarReading? Read(nint windowHandle)
        {
            lock (_gate)
                return Unreadable ? null : _bars.GetValueOrDefault(windowHandle);
        }
    }

    private sealed class ConfirmingUi : ISessionUiHost
    {
        public Task<JoinDecision> ShowJoinConfirmationAsync(JoinConfirmation confirmation, CancellationToken ct = default)
            => Task.FromResult(JoinDecision.Confirmed);
        public void DismissJoinConfirmation() { }
    }

    private sealed class FakeHub : ISessionHubConnection
    {
        public List<(Guid SessionId, string Kind, string PayloadJson, DateTimeOffset? OccurredAt)> Reports { get; } = new();
        public bool FailReports { get; set; }

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

        public Task ReportEventAsync(Guid sessionId, string kind, string payloadJson, DateTimeOffset? occurredAt = null, CancellationToken ct = default)
        {
            lock (Reports) Reports.Add((sessionId, kind, payloadJson, occurredAt));
            return FailReports ? Task.FromException(new InvalidOperationException("hub down")) : Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task JoinSessionAsync(Guid sessionId, string? joinCode, CancellationToken ct = default) => Task.CompletedTask;
        public Task LeaveSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclineSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExtensionHeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> HeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> IsInSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<SessionStartedPayload?> GetStartedSessionAsync(CancellationToken ct = default) => Task.FromResult<SessionStartedPayload?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
