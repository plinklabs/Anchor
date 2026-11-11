using FocusAgent.Core.Startup;
using Microsoft.Extensions.Time.Testing;

namespace FocusAgent.Core.Tests;

/// <summary>
/// #461: the single-instance claim steps aside only for an agent that is running,
/// and waits (bounded) for one that was just ended to be gone.
/// </summary>
public class SingleInstanceClaimTests
{
    private const uint HolderPid = 4242;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public void A_free_key_is_claimed_at_once_without_looking_at_any_process()
    {
        var key = new FakeKey((uint?)null);
        var processes = new FakeProcesses();

        Assert.True(Claim(key, processes));
        Assert.Equal(1, key.Calls);
        Assert.Empty(processes.Looked);
    }

    [Fact]
    public void A_running_holder_is_left_alone_and_this_launch_steps_aside_at_once()
    {
        var key = new FakeKey(HolderPid);
        var processes = new FakeProcesses { [HolderPid] = KeyHolderState.Running };

        Assert.False(Claim(key, processes));
        Assert.Equal(1, key.Calls);
        Assert.Empty(processes.Waits);
    }

    [Fact]
    public void An_exiting_holder_is_waited_for_and_the_key_claimed_once_it_is_gone()
    {
        // The killed agent's exit code is set but Windows is still tearing it down:
        // the key is held until it is gone, then free.
        var processes = new FakeProcesses { [HolderPid] = KeyHolderState.Exiting };
        var key = new FakeKey(HolderPid, null);

        Assert.True(Claim(key, processes));
        Assert.Equal(2, key.Calls);
        var wait = Assert.Single(processes.Waits);
        Assert.Equal(HolderPid, wait.Pid);
        Assert.Equal(Budget, wait.Timeout);
    }

    [Fact]
    public void A_holder_that_is_already_gone_is_retried_after_a_short_pause()
    {
        var processes = new FakeProcesses { [HolderPid] = KeyHolderState.Gone };
        var key = new FakeKey(HolderPid, null);
        var pauses = new List<TimeSpan>();

        Assert.True(Claim(key, processes, pause: pauses.Add));
        Assert.Equal(new[] { SingleInstanceClaim.RetryDelay }, pauses);
        Assert.Empty(processes.Waits);
    }

    [Fact]
    public void A_held_key_whose_holder_cannot_be_named_is_retried_too()
    {
        // WinAppSDK names no holder when the one holding it finished exiting in
        // between.
        var key = new FakeKey(0u, null);
        var processes = new FakeProcesses();

        Assert.True(Claim(key, processes));
        Assert.Equal(2, key.Calls);
        Assert.Empty(processes.Looked);
    }

    [Fact]
    public void An_exiting_holder_that_never_goes_is_given_up_on_after_the_budget()
    {
        var time = new FakeTimeProvider();
        var processes = new FakeProcesses { [HolderPid] = KeyHolderState.Exiting };
        processes.OnWait = t => time.Advance(t);
        var key = new FakeKey(HolderPid);
        var start = time.GetUtcNow();

        Assert.False(Claim(key, processes, time));
        Assert.Equal(Budget, time.GetUtcNow() - start);
        Assert.Single(processes.Waits);
    }

    [Fact]
    public void Waiting_out_one_exiting_holder_spends_the_budget_for_the_next()
    {
        // Two holders in a row on their way out (a restart after two quick kills): the
        // second wait only gets what the first left.
        var time = new FakeTimeProvider();
        var processes = new FakeProcesses
        {
            [HolderPid] = KeyHolderState.Exiting,
            [HolderPid + 4] = KeyHolderState.Exiting,
        };
        processes.OnWait = _ => time.Advance(TimeSpan.FromSeconds(4));
        var key = new FakeKey(HolderPid, HolderPid + 4, null);

        Assert.True(Claim(key, processes, time));
        Assert.Equal(
            new[] { (HolderPid, Budget), (HolderPid + 4, Budget - TimeSpan.FromSeconds(4)) },
            processes.Waits);
    }

    [Fact]
    public void A_holder_that_turns_out_to_be_running_after_a_wait_is_left_alone()
    {
        // The key moved on to another launch that won the race: it is running now.
        var processes = new FakeProcesses
        {
            [HolderPid] = KeyHolderState.Exiting,
            [HolderPid + 4] = KeyHolderState.Running,
        };
        var key = new FakeKey(HolderPid, HolderPid + 4);

        Assert.False(Claim(key, processes));
        Assert.Equal(2, key.Calls);
    }

    private static bool Claim(
        FakeKey key, FakeProcesses processes, FakeTimeProvider? time = null, Action<TimeSpan>? pause = null) =>
        SingleInstanceClaim.TryClaim(key, processes, Budget, time ?? new FakeTimeProvider(), pause ?? (_ => { }));

    /// <summary>Answers each registration with the next holder; repeats the last one.</summary>
    private sealed class FakeKey(params uint?[] holders) : ISingleInstanceKey
    {
        private readonly Queue<uint?> _holders = new(holders);
        public int Calls { get; private set; }

        public uint? RegisterOrFindHolder()
        {
            Calls++;
            return _holders.Count > 1 ? _holders.Dequeue() : _holders.Peek();
        }
    }

    private sealed class FakeProcesses : Dictionary<uint, KeyHolderState>, IKeyHolderProcesses
    {
        public List<uint> Looked { get; } = [];
        public List<(uint Pid, TimeSpan Timeout)> Waits { get; } = [];
        public Action<TimeSpan>? OnWait { get; set; }

        public KeyHolderState StateOf(uint processId)
        {
            Looked.Add(processId);
            return this[processId];
        }

        public bool WaitUntilGone(uint processId, TimeSpan timeout)
        {
            Waits.Add((processId, timeout));
            OnWait?.Invoke(timeout);
            return true;
        }
    }
}
