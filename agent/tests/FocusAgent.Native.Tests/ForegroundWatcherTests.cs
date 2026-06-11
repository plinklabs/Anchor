using System.Runtime.Versioning;
using FocusAgent.Core.Focus;
using FocusAgent.Native;

namespace FocusAgent.Native.Tests;

[SupportedOSPlatform("windows")]
public class ForegroundWatcherTests
{
    [Fact]
    public async Task Start_marshals_through_captured_sync_context_when_called_from_another_thread()
    {
        var sc = new RecordingSyncContext();
        using var watcher = new ForegroundWatcher(new StubIdentifier(), sc);

        await Task.Run(() => watcher.Start());

        // Hook registration ran via the captured sync context's Post rather
        // than inline on the worker thread — the regression #64 guards against.
        // (Send is intentionally not used: DispatcherQueueSynchronizationContext
        // throws NotSupportedException on Send.)
        Assert.True(sc.PostCount >= 1, $"Expected Post to be invoked at least once; saw {sc.PostCount}.");
        Assert.Equal(0, sc.SendCount);
    }

    [Fact]
    public void Start_runs_inline_when_already_on_sync_context_thread()
    {
        var sc = new RecordingSyncContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(sc);
        try
        {
            using var watcher = new ForegroundWatcher(new StubIdentifier(), sc);

            watcher.Start();

            // No marshal needed when caller is already on the target context —
            // a Post + wait to self would deadlock on the real dispatcher.
            Assert.Equal(0, sc.PostCount);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task Start_throws_NotSupportedException_when_sync_context_rejects_Send()
    {
        // Regression for the WinUI 3 DispatcherQueueSynchronizationContext
        // behavior — the watcher must use Post, never Send.
        var sc = new SendRejectingSyncContext();
        using var watcher = new ForegroundWatcher(new StubIdentifier(), sc);

        var ex = await Record.ExceptionAsync(() => Task.Run(() => watcher.Start()));

        Assert.Null(ex);
        Assert.Equal(0, sc.SendCount);
        Assert.True(sc.PostCount >= 1);
    }

    [Theory]
    [InlineData(0x0003u, true)]  // EVENT_SYSTEM_FOREGROUND — genuine focus change
    [InlineData(0x0017u, true)]  // EVENT_SYSTEM_MINIMIZEEND — restore-from-minimized (#92)
    [InlineData(0x0016u, false)] // EVENT_SYSTEM_MINIMIZESTART — minimize itself is not a visit
    [InlineData(0x0008u, false)] // EVENT_SYSTEM_CAPTURESTART — unrelated
    public void ShouldHandle_accepts_foreground_and_restore_events_only(uint eventType, bool expected)
    {
        // #92: a window minimized by our own process keeps logical foreground
        // status, so the student re-activating it fires MINIMIZEEND but no
        // FOREGROUND event. Both must flow into the same enforcement path.
        Assert.Equal(expected, ForegroundWatcher.ShouldHandle(eventType, idObject: 0, idChild: 0, hwnd: 0x100));
    }

    [Theory]
    [InlineData(1, 0, 0x100L)]  // child object (not OBJID_WINDOW)
    [InlineData(0, 2, 0x100L)]  // child id set
    [InlineData(0, 0, 0L)]      // no hwnd
    public void ShouldHandle_rejects_non_window_notifications(int idObject, int idChild, long hwnd)
    {
        Assert.False(ForegroundWatcher.ShouldHandle(0x0003u, idObject, idChild, (nint)hwnd));
        Assert.False(ForegroundWatcher.ShouldHandle(0x0017u, idObject, idChild, (nint)hwnd));
    }

    private sealed class RecordingSyncContext : SynchronizationContext
    {
        public int SendCount;
        public int PostCount;

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref SendCount);
            d(state);
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref PostCount);
            d(state);
        }
    }

    /// <summary>Mimics <c>DispatcherQueueSynchronizationContext</c>: Post works, Send throws.</summary>
    private sealed class SendRejectingSyncContext : SynchronizationContext
    {
        public int SendCount;
        public int PostCount;

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref SendCount);
            throw new NotSupportedException("Use Post instead.");
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref PostCount);
            d(state);
        }
    }

    private sealed class StubIdentifier : IAppIdentifier
    {
        public AppInfo? Identify(nint windowHandle, int processId) => null;
        public bool LaunchOrActivate(AllowedAppRule rule) => false;
    }
}
