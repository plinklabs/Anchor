using FocusAgent.Core.Tamper;

namespace FocusAgent.Core.Tests;

public class ExtensionWitnessMonitorTests
{
    [Fact]
    public async Task Drop_during_a_joined_session_reports_extension_disabled()
    {
        var transport = new FakeWitnessTransport();
        var reporter = new RecordingReporter();
        var sessionId = Guid.NewGuid();
        await using var monitor = new ExtensionWitnessMonitor(transport, reporter, () => sessionId);

        transport.RaiseConnected();
        transport.RaiseDisconnected();

        var call = Assert.Single(reporter.Calls);
        Assert.Equal(sessionId, call.SessionId);
        Assert.Equal(TamperKinds.ExtensionDisabled, call.Kind);
        Assert.Equal("extension_disabled", call.Kind);
    }

    [Fact]
    public async Task Drop_outside_a_session_is_not_reported()
    {
        var transport = new FakeWitnessTransport();
        var reporter = new RecordingReporter();
        await using var monitor = new ExtensionWitnessMonitor(transport, reporter, () => null);

        transport.RaiseConnected();
        transport.RaiseDisconnected();

        Assert.Empty(reporter.Calls);
    }

    [Fact]
    public async Task Disconnect_without_a_prior_connect_is_not_reported()
    {
        // A failed accept or a stop can surface a disconnect we never saw connect;
        // that is not the student disabling the extension.
        var transport = new FakeWitnessTransport();
        var reporter = new RecordingReporter();
        await using var monitor = new ExtensionWitnessMonitor(transport, reporter, () => Guid.NewGuid());

        transport.RaiseDisconnected();

        Assert.Empty(reporter.Calls);
    }

    [Fact]
    public async Task Reconnect_then_drop_again_reports_each_disable()
    {
        var transport = new FakeWitnessTransport();
        var reporter = new RecordingReporter();
        var sessionId = Guid.NewGuid();
        await using var monitor = new ExtensionWitnessMonitor(transport, reporter, () => sessionId);

        transport.RaiseConnected();
        transport.RaiseDisconnected();   // student disables the extension
        transport.RaiseConnected();      // student re-enables it
        transport.RaiseDisconnected();   // …and disables it again

        Assert.Equal(2, reporter.Calls.Count);
        Assert.All(reporter.Calls, c => Assert.Equal(TamperKinds.ExtensionDisabled, c.Kind));
    }

    [Fact]
    public async Task A_reporter_failure_does_not_throw_out_of_the_drop_handler()
    {
        var transport = new FakeWitnessTransport();
        var reporter = new RecordingReporter { Throw = true };
        await using var monitor = new ExtensionWitnessMonitor(transport, reporter, () => Guid.NewGuid());

        transport.RaiseConnected();
        var ex = Record.Exception(() => transport.RaiseDisconnected());

        Assert.Null(ex);
    }

    [Fact]
    public async Task ConnectionChanged_fires_once_per_genuine_transition_in_or_out_of_a_session()
    {
        // #427: the Edge pause listens to this, so a repeated connect (the
        // transport re-accepting) must not look like a new connection, and a drop
        // must be announced even outside a session (where nothing is reported).
        var transport = new FakeWitnessTransport();
        var reporter = new RecordingReporter();
        await using var monitor = new ExtensionWitnessMonitor(transport, reporter, () => null);
        var changes = new List<bool>();
        monitor.ConnectionChanged += (_, connected) => changes.Add(connected);

        Assert.False(monitor.HasConnected);
        transport.RaiseDisconnected();   // never connected: not a transition
        transport.RaiseConnected();
        transport.RaiseConnected();      // already up
        transport.RaiseDisconnected();

        Assert.Equal(new[] { true, false }, changes);
        Assert.True(monitor.HasConnected);
        Assert.False(monitor.IsConnected);
        Assert.Empty(reporter.Calls);
    }

    [Fact]
    public async Task A_throwing_ConnectionChanged_subscriber_does_not_stop_the_tamper_report()
    {
        var transport = new FakeWitnessTransport();
        var reporter = new RecordingReporter();
        var sessionId = Guid.NewGuid();
        await using var monitor = new ExtensionWitnessMonitor(transport, reporter, () => sessionId);
        monitor.ConnectionChanged += (_, _) => throw new InvalidOperationException("subscriber bug");

        transport.RaiseConnected();
        var ex = Record.Exception(() => transport.RaiseDisconnected());

        Assert.Null(ex);
        Assert.Single(reporter.Calls);
    }

    [Fact]
    public async Task One_of_two_profiles_dropping_is_not_reported_and_the_extension_stays_connected()
    {
        // #517: every Edge profile has its own link. Closing one profile's last
        // window drops its link while the other profile is still filtered; that
        // must not alarm the teacher or start the Edge pause.
        var transport = new FakeWitnessTransport();
        var reporter = new RecordingReporter();
        var sessionId = Guid.NewGuid();
        await using var monitor = new ExtensionWitnessMonitor(transport, reporter, () => sessionId);
        var changes = new List<bool>();
        monitor.ConnectionChanged += (_, connected) => changes.Add(connected);
        var first = new WitnessConnection(1);
        var second = new WitnessConnection(2);

        transport.RaiseConnected(first);
        transport.RaiseConnected(second);
        transport.RaiseDisconnected(first);

        Assert.True(monitor.IsConnected);
        Assert.Equal(new[] { second }, monitor.Connections);
        Assert.Equal(new[] { true }, changes);
        Assert.Empty(reporter.Calls);
    }

    [Fact]
    public async Task The_last_of_two_profiles_dropping_reports_extension_disabled_once()
    {
        var transport = new FakeWitnessTransport();
        var reporter = new RecordingReporter();
        var sessionId = Guid.NewGuid();
        await using var monitor = new ExtensionWitnessMonitor(transport, reporter, () => sessionId);
        var changes = new List<bool>();
        monitor.ConnectionChanged += (_, connected) => changes.Add(connected);
        var first = new WitnessConnection(1);
        var second = new WitnessConnection(2);

        transport.RaiseConnected(first);
        transport.RaiseConnected(second);
        transport.RaiseDisconnected(second);
        transport.RaiseDisconnected(first);

        Assert.False(monitor.IsConnected);
        Assert.True(monitor.HasConnected);
        Assert.Empty(monitor.Connections);
        Assert.Equal(new[] { true, false }, changes);
        var call = Assert.Single(reporter.Calls);
        Assert.Equal((sessionId, TamperKinds.ExtensionDisabled), call);
    }

    [Fact]
    public async Task Every_connection_opening_and_closing_is_announced_by_its_identity()
    {
        var transport = new FakeWitnessTransport();
        await using var monitor = new ExtensionWitnessMonitor(transport, new RecordingReporter(), () => null);
        var events = new List<string>();
        monitor.ConnectionOpened += (_, c) => events.Add($"opened {c.Id}");
        monitor.ConnectionClosed += (_, c) => events.Add($"closed {c.Id}");
        monitor.ConnectionChanged += (_, connected) => events.Add($"connected={connected}");
        var first = new WitnessConnection(1);
        var second = new WitnessConnection(2);

        transport.RaiseConnected(first);
        transport.RaiseConnected(second);
        transport.RaiseConnected(second);       // announced twice: still one connection
        transport.RaiseDisconnected(first);
        transport.RaiseDisconnected(first);     // already gone
        transport.RaiseDisconnected(second);

        Assert.Equal(
            new[] { "opened 1", "connected=True", "opened 2", "closed 1", "closed 2", "connected=False" },
            events);
    }

    [Fact]
    public async Task StartAsync_delegates_to_the_transport()
    {
        var transport = new FakeWitnessTransport();
        await using var monitor = new ExtensionWitnessMonitor(transport, new RecordingReporter(), () => null);

        await monitor.StartAsync();

        Assert.Equal(1, transport.StartCalls);
    }

    [Fact]
    public async Task Dispose_unsubscribes_so_a_later_drop_is_ignored()
    {
        var transport = new FakeWitnessTransport();
        var reporter = new RecordingReporter();
        var monitor = new ExtensionWitnessMonitor(transport, reporter, () => Guid.NewGuid());

        transport.RaiseConnected();
        await monitor.DisposeAsync();

        transport.RaiseDisconnected();

        Assert.Empty(reporter.Calls);
        Assert.Equal(1, transport.StopCalls);
    }

    private sealed class FakeWitnessTransport : IExtensionWitnessTransport
    {
        // One Edge profile's extension, unless a test names another.
        private static readonly WitnessConnection Only = new(1);
        public event EventHandler<WitnessConnection>? WitnessConnected;
        public event EventHandler<WitnessConnection>? WitnessDisconnected;

        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public void RaiseConnected(WitnessConnection? connection = null) => WitnessConnected?.Invoke(this, connection ?? Only);
        public void RaiseDisconnected(WitnessConnection? connection = null) => WitnessDisconnected?.Invoke(this, connection ?? Only);

        public Task StartAsync(CancellationToken ct = default)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            StopCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReporter : ITamperReporter
    {
        public List<(Guid SessionId, string Kind)> Calls { get; } = new();
        public bool Throw { get; set; }

        public Task ReportAsync(Guid sessionId, string kind, CancellationToken ct = default)
        {
            Calls.Add((sessionId, kind));
            if (Throw) throw new InvalidOperationException("simulated hub failure");
            return Task.CompletedTask;
        }
    }
}
