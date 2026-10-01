using Microsoft.Extensions.Time.Testing;

namespace Anchor.Api.Tests;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that lets a test await the creation of a
/// timer — a background service calling <c>Task.Delay(..., timeProvider, ...)</c>
/// creates one — so it can tell when that service has parked on its wait
/// without sleeping. Every timer created since construction counts, so the
/// order of "service parks" and "test starts waiting" doesn't matter.
/// </summary>
public sealed class TimerRecordingTimeProvider : FakeTimeProvider
{
    private readonly object _gate = new();
    private readonly List<TimeSpan> _dueTimes = new();
    private readonly List<(Func<TimeSpan, bool> Match, TaskCompletionSource<TimeSpan> Signal)> _waiters = new();

    public TimerRecordingTimeProvider(DateTimeOffset startDateTime)
        : base(startDateTime)
    {
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        lock (_gate)
        {
            _dueTimes.Add(dueTime);
            foreach (var waiter in _waiters.Where(w => w.Match(dueTime)).ToList())
            {
                waiter.Signal.TrySetResult(dueTime);
                _waiters.Remove(waiter);
            }
        }
        return timer;
    }

    /// <summary>
    /// Returns the due time of the first timer matching <paramref name="match"/>
    /// once one has been created. After <paramref name="timeout"/> of real time
    /// it throws, listing the timers that were created instead.
    /// </summary>
    public async Task<TimeSpan> WaitForTimerAsync(Func<TimeSpan, bool> match, TimeSpan timeout)
    {
        Task<TimeSpan> signal;
        lock (_gate)
        {
            foreach (var due in _dueTimes)
            {
                if (match(due)) return due;
            }
            var waiter = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((match, waiter));
            signal = waiter.Task;
        }

        try
        {
            return await signal.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            string seen;
            lock (_gate)
            {
                seen = _dueTimes.Count == 0 ? "none" : string.Join(", ", _dueTimes);
            }
            throw new TimeoutException(
                $"No matching timer was created within {timeout.TotalSeconds:0} s. Timers created: {seen}.");
        }
    }
}
