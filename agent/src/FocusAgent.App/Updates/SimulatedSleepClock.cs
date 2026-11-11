namespace FocusAgent.App.Updates;

/// <summary>
/// Dev-only clock for the update service (#456), used under <c>--status-endpoint</c>
/// so the e2e can make the agent behave as after a night asleep without sleeping the
/// machine. Sleep moves the clock (<see cref="GetUtcNow"/>) but not the timers, which
/// run on time awake; <see cref="Sleep"/> does exactly that (POST /simulate-sleep).
/// The wake itself is the real one: the e2e sends the agent's SystemEvents window the
/// message Windows sends on resume or unlock. Everything else is the system clock. A
/// normal launch uses <see cref="TimeProvider.System"/>.
/// </summary>
internal sealed class SimulatedSleepClock : TimeProvider
{
    private long _sleptTicks;

    /// <summary>Move the clock forward by <paramref name="duration"/>, leaving every timer where it is.</summary>
    public void Sleep(TimeSpan duration) => Interlocked.Add(ref _sleptTicks, duration.Ticks);

    public override DateTimeOffset GetUtcNow() =>
        base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _sleptTicks));
}
