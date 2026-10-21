using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using FocusAgent.Native;
using FocusAgent.Native.Win32;
using Microsoft.Extensions.Logging;

namespace FocusAgent.Native.Tests;

/// <summary>
/// <see cref="FocusEnforcer.Block"/> on real windows (#438). The blocked window is
/// minimized by its own thread, so the agent never waits for an app; where the
/// foreground goes then needs two processes and is covered by the e2e
/// FocusFallbackTests.
/// </summary>
[SupportedOSPlatform("windows")]
public class FocusEnforcerWindowTests
{
    [Fact]
    public async Task A_blocked_window_goes_down_without_a_warning()
    {
        var log = new ListLogger<FocusEnforcer>();
        using var allowed = TestWindow.Open("Anchor test allowed app");
        using var offList = TestWindow.Open("Anchor test off-list app");
        var enforcer = new FocusEnforcer(log);
        enforcer.RememberAllowed(allowed.Handle);

        Assert.True(enforcer.Block(offList.Handle));

        Assert.True(
            await WaitUntilAsync(() => NativeMethods.IsIconic(offList.Handle), TimeSpan.FromSeconds(2)),
            "The blocked window was not minimized.");
        Assert.False(NativeMethods.IsIconic(allowed.Handle), "The allowed window was minimized.");
        await Task.Delay(FocusEnforcer.OwnThreadMinimizeTimeout + TimeSpan.FromMilliseconds(300));
        Assert.DoesNotContain(log.Messages, m => m.Contains("did not minimize"));
    }

    [Fact]
    public async Task Blocking_an_app_that_is_not_responding_never_holds_up_the_agent()
    {
        var log = new ListLogger<FocusEnforcer>();
        using var allowed = TestWindow.Open("Anchor test allowed app");
        using var offList = TestWindow.Open("Anchor test hung off-list app");
        var enforcer = new FocusEnforcer(log);
        enforcer.RememberAllowed(allowed.Handle);
        offList.Freeze();

        var clock = Stopwatch.StartNew();
        Assert.True(enforcer.Block(offList.Handle));
        clock.Stop();

        // A ShowWindow from the agent waits until the app handles messages again, and
        // holds up every foreground change behind it meanwhile.
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(1),
            $"Block took {clock.ElapsedMilliseconds} ms while the off-list app was not responding.");
        Assert.True(
            await WaitUntilAsync(() => log.Messages.Any(m => m.Contains("did not minimize")), TimeSpan.FromSeconds(3)),
            "The agent did not log that the blocked app is not responding.");
        Assert.False(NativeMethods.IsIconic(offList.Handle), "A window whose thread is frozen went down.");

        // The request stays queued: the window goes down the moment its app responds.
        offList.Thaw();
        Assert.True(
            await WaitUntilAsync(() => NativeMethods.IsIconic(offList.Handle), TimeSpan.FromSeconds(2)),
            "The blocked window did not go down once its app responded again.");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _messages.Enqueue(formatter(state, exception));
    }
}
