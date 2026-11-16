using FocusAgent.Core.Tamper;

namespace FocusAgent.Core.Tests;

public class ExtensionWitnessMonitorTests
{
    [Fact]
    public async Task ConnectionChanged_fires_once_per_genuine_transition_in_or_out_of_a_session()
    {
        // #427: the Edge pause listens to this, so a repeated connect (the
        // transport re-accepting) must not look like a new connection, and a drop
        // must be announced even outside a session (where nothing is reported).
        var transport = new FakeWitnessTransport();
        await using var monitor = new ExtensionWitnessMonitor(transport);
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
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_throw_out_of_the_transport_s_handlers()
    {
        var transport = new FakeWitnessTransport();
        await using var monitor = new ExtensionWitnessMonitor(transport);
        monitor.ConnectionChanged += (_, _) => throw new InvalidOperationException("subscriber bug");
        monitor.ConnectionOpened += (_, _) => throw new InvalidOperationException("subscriber bug");
        monitor.ConnectionClosed += (_, _) => throw new InvalidOperationException("subscriber bug");

        var ex = Record.Exception(() =>
        {
            transport.RaiseConnected();
            transport.RaiseDisconnected();
        });

        Assert.Null(ex);
        Assert.False(monitor.IsConnected);
    }

    [Fact]
    public async Task One_of_two_profiles_dropping_leaves_the_extension_connected()
    {
        // #517: every Edge profile has its own link. Closing one profile's last
        // window drops its link while the other profile is still filtered; that
        // must not start the Edge pause (or the report that goes with it).
        var transport = new FakeWitnessTransport();
        await using var monitor = new ExtensionWitnessMonitor(transport);
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
    }

    [Fact]
    public async Task The_last_of_two_profiles_dropping_is_the_extension_going_away()
    {
        var transport = new FakeWitnessTransport();
        await using var monitor = new ExtensionWitnessMonitor(transport);
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
    }

    [Fact]
    public async Task Every_connection_opening_and_closing_is_announced_by_its_identity()
    {
        var transport = new FakeWitnessTransport();
        await using var monitor = new ExtensionWitnessMonitor(transport);
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
        await using var monitor = new ExtensionWitnessMonitor(transport);

        await monitor.StartAsync();

        Assert.Equal(1, transport.StartCalls);
    }

    [Fact]
    public async Task Dispose_unsubscribes_so_a_later_drop_is_ignored()
    {
        var transport = new FakeWitnessTransport();
        var monitor = new ExtensionWitnessMonitor(transport);
        var changes = new List<bool>();
        monitor.ConnectionChanged += (_, connected) => changes.Add(connected);

        transport.RaiseConnected();
        await monitor.DisposeAsync();

        transport.RaiseDisconnected();

        Assert.Equal(new[] { true }, changes);
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
}
