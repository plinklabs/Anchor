using FocusAgent.Core.Dtos;
using FocusAgent.Core.Sessions;
using Microsoft.Extensions.Time.Testing;

namespace FocusAgent.Core.Tests;

public class JoinConfirmationTests
{
    private static SessionStartedPayload SamplePayload() =>
        new(SessionId: Guid.NewGuid(),
            ClassId: Guid.NewGuid(),
            Mode: "strict",
            StartedAt: DateTimeOffset.UnixEpoch,
            JoinCode: "123456",
            Apps: Array.Empty<AllowedAppDto>(),
            Domains: Array.Empty<AllowedDomainDto>());

    [Fact]
    public void Confirms_when_timer_elapses()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var payload = SamplePayload();
        using var confirmation = new JoinConfirmation(payload, "Mr. De Vos", TimeSpan.FromSeconds(5), clock, tickInterval: TimeSpan.FromMilliseconds(250));

        var ticks = 0;
        confirmation.Tick += (_, _) => ticks++;
        JoinDecision? finished = null;
        confirmation.Finished += (_, d) => finished = d;

        confirmation.Start();
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(JoinDecision.Confirmed, finished);
        Assert.Equal(JoinDecision.Confirmed, confirmation.Decision);
        Assert.True(ticks >= 1, "Tick should fire at least once before completion");
    }

    [Fact]
    public void Cancel_before_timer_decides_declined()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var confirmation = new JoinConfirmation(SamplePayload(), "Mr. De Vos", TimeSpan.FromSeconds(5), clock);

        confirmation.Start();
        clock.Advance(TimeSpan.FromSeconds(2));
        confirmation.Cancel();
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(JoinDecision.Declined, confirmation.Decision);
    }

    [Fact]
    public async Task Abort_marks_aborted_and_completes()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var confirmation = new JoinConfirmation(SamplePayload(), "Mr. De Vos", TimeSpan.FromSeconds(5), clock);

        confirmation.Start();
        confirmation.Abort();

        Assert.Equal(JoinDecision.Aborted, confirmation.Decision);
        Assert.Equal(JoinDecision.Aborted, await confirmation.Completion);
    }

    [Fact]
    public void Subsequent_decisions_are_ignored_once_resolved()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var confirmation = new JoinConfirmation(SamplePayload(), "Mr. De Vos", TimeSpan.FromSeconds(5), clock);

        confirmation.Start();
        confirmation.Cancel();
        confirmation.Abort();

        Assert.Equal(JoinDecision.Declined, confirmation.Decision);
    }

    [Fact]
    public void Remaining_shrinks_with_clock()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var confirmation = new JoinConfirmation(SamplePayload(), "Mr. De Vos", TimeSpan.FromSeconds(5), clock);

        confirmation.Start();
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(confirmation.Remaining <= TimeSpan.FromSeconds(3));
        Assert.True(confirmation.Remaining > TimeSpan.Zero);
    }
}
