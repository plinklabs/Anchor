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
        public event EventHandler? WitnessConnected;
        public event EventHandler? WitnessDisconnected;

        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public void RaiseConnected() => WitnessConnected?.Invoke(this, EventArgs.Empty);
        public void RaiseDisconnected() => WitnessDisconnected?.Invoke(this, EventArgs.Empty);

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
