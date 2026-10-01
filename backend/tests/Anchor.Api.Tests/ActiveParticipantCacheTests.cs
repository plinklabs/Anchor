using Anchor.Api.Realtime;
using Anchor.Domain.Sessions;
using Microsoft.Extensions.Time.Testing;

namespace Anchor.Api.Tests;

/// <summary>
/// Unit tests for <see cref="ActiveParticipantCache"/> (#342): the in-memory
/// participant check that keeps heartbeat validation off the database. The
/// database lookup is a counting stand-in so each test can see exactly when the
/// cache falls back to it.
/// </summary>
public sealed class ActiveParticipantCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly ActiveParticipantCache _cache;
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    public ActiveParticipantCacheTests()
    {
        _cache = new ActiveParticipantCache(_clock);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_miss_queries_the_database_once_and_caches_the_answer(bool inDatabase)
    {
        var lookup = new CountingLookup(inDatabase);

        for (var i = 0; i < 5; i++)
            Assert.Equal(inDatabase, await _cache.IsActiveAsync(_sessionId, _userId, lookup.Run, CancellationToken.None));

        Assert.Equal(1, lookup.Calls);
    }

    [Fact]
    public async Task Update_answers_without_the_database_and_follows_leave_and_rejoin()
    {
        var lookup = new CountingLookup(answer: false);
        var participant = new SessionParticipant
        {
            SessionId = _sessionId,
            UserId = _userId,
            JoinedAt = Start,
        };

        _cache.Update(participant);
        Assert.True(await _cache.IsActiveAsync(_sessionId, _userId, lookup.Run, CancellationToken.None));

        participant.LeftAt = Start.AddMinutes(5);
        _cache.Update(participant);
        Assert.False(await _cache.IsActiveAsync(_sessionId, _userId, lookup.Run, CancellationToken.None));

        participant.LeftAt = null;
        _cache.Update(participant);
        Assert.True(await _cache.IsActiveAsync(_sessionId, _userId, lookup.Run, CancellationToken.None));

        Assert.Equal(0, lookup.Calls);
    }

    [Fact]
    public void A_participant_that_never_joined_is_not_active()
    {
        // Session start creates the roster rows with JoinedAt null; declining
        // keeps it null. Neither may heartbeat.
        _cache.Update(new SessionParticipant { SessionId = _sessionId, UserId = _userId, DeclinedAt = Start });

        Assert.True(_cache.TryGet(_sessionId, _userId, out var isActive));
        Assert.False(isActive);
    }

    [Fact]
    public async Task Inactive_answers_expire_after_the_inactive_lifetime()
    {
        var lookup = new CountingLookup(answer: false);
        await _cache.IsActiveAsync(_sessionId, _userId, lookup.Run, CancellationToken.None);

        _clock.Advance(ActiveParticipantCache.InactiveEntryLifetime - TimeSpan.FromSeconds(1));
        await _cache.IsActiveAsync(_sessionId, _userId, lookup.Run, CancellationToken.None);
        Assert.Equal(1, lookup.Calls);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await _cache.IsActiveAsync(_sessionId, _userId, lookup.Run, CancellationToken.None);
        Assert.Equal(2, lookup.Calls);
    }

    [Fact]
    public async Task Active_answers_last_for_the_active_lifetime()
    {
        var lookup = new CountingLookup(answer: true);
        await _cache.IsActiveAsync(_sessionId, _userId, lookup.Run, CancellationToken.None);

        // A whole lesson of 10-second pings: still one query.
        for (var elapsed = TimeSpan.Zero;
             elapsed < ActiveParticipantCache.ActiveEntryLifetime - TimeSpan.FromSeconds(10);
             elapsed += TimeSpan.FromSeconds(10))
        {
            _clock.Advance(TimeSpan.FromSeconds(10));
            Assert.True(await _cache.IsActiveAsync(_sessionId, _userId, lookup.Run, CancellationToken.None));
        }
        Assert.Equal(1, lookup.Calls);

        _clock.Advance(TimeSpan.FromSeconds(10));
        await _cache.IsActiveAsync(_sessionId, _userId, lookup.Run, CancellationToken.None);
        Assert.Equal(2, lookup.Calls);
    }

    [Fact]
    public async Task An_update_landing_while_a_lookup_is_in_flight_is_not_overwritten_by_it()
    {
        // The lookup read the row before LeaveSession committed, so it answers
        // "active"; LeaveSession's update lands before the lookup returns. The
        // cache must keep "left", or the departed student's pings would be
        // accepted until the entry expires.
        var gate = new TaskCompletionSource<bool>();
        var pending = _cache.IsActiveAsync(_sessionId, _userId, _ => gate.Task, CancellationToken.None);

        _cache.Update(new SessionParticipant
        {
            SessionId = _sessionId,
            UserId = _userId,
            JoinedAt = Start,
            LeftAt = Start.AddMinutes(1),
        });
        gate.SetResult(true);
        await pending;

        Assert.True(_cache.TryGet(_sessionId, _userId, out var isActive));
        Assert.False(isActive);
    }

    [Fact]
    public async Task A_lookup_started_after_an_update_replaces_it()
    {
        _cache.Update(new SessionParticipant { SessionId = _sessionId, UserId = _userId, JoinedAt = Start });
        _clock.Advance(ActiveParticipantCache.ActiveEntryLifetime);

        // The update has expired, so this lookup's fresh answer is the one kept.
        Assert.False(await _cache.IsActiveAsync(_sessionId, _userId, _ => Task.FromResult(false), CancellationToken.None));
        Assert.True(_cache.TryGet(_sessionId, _userId, out var isActive));
        Assert.False(isActive);
    }

    [Fact]
    public void ClearSession_drops_only_that_sessions_pairs()
    {
        var otherSessionId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        _cache.Update(new SessionParticipant { SessionId = _sessionId, UserId = _userId, JoinedAt = Start });
        _cache.Update(new SessionParticipant { SessionId = _sessionId, UserId = otherUserId, JoinedAt = Start });
        _cache.Update(new SessionParticipant { SessionId = otherSessionId, UserId = _userId, JoinedAt = Start });

        _cache.ClearSession(_sessionId);

        Assert.False(_cache.TryGet(_sessionId, _userId, out _));
        Assert.False(_cache.TryGet(_sessionId, otherUserId, out _));
        Assert.True(_cache.TryGet(otherSessionId, _userId, out _));
    }

    [Fact]
    public async Task Expired_entries_are_swept_from_memory()
    {
        // Pings for sessions the caller isn't in (or made up) each leave an
        // inactive entry behind; they must not accumulate.
        for (var i = 0; i < 10; i++)
            await _cache.IsActiveAsync(Guid.NewGuid(), _userId, _ => Task.FromResult(false), CancellationToken.None);
        _cache.Update(new SessionParticipant { SessionId = _sessionId, UserId = _userId, JoinedAt = Start });
        Assert.Equal(11, _cache.Count);

        _clock.Advance(ActiveParticipantCache.InactiveEntryLifetime);
        await _cache.IsActiveAsync(_sessionId, _userId, _ => Task.FromResult(true), CancellationToken.None);

        // Only the still-valid active pair survives.
        Assert.Equal(1, _cache.Count);
        Assert.True(_cache.TryGet(_sessionId, _userId, out var isActive));
        Assert.True(isActive);
    }

    private sealed class CountingLookup
    {
        private readonly bool _answer;
        private int _calls;

        public CountingLookup(bool answer) => _answer = answer;

        public int Calls => Volatile.Read(ref _calls);

        public Task<bool> Run(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(_answer);
        }
    }
}
