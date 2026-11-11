using Anchor.Api.Diagnostics;
using Anchor.Api.Events;
using Anchor.Domain.Sessions;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Anchor.Api.Tests;

/// <summary>
/// The agents' log uploads (#446) are deleted 14 days after they arrived,
/// whether or not uploads are still switched on, at the event pruner's quiet
/// hour and never at startup. The host's loop is off in tests
/// (<c>LogUpload:EnablePruner=false</c> on <see cref="AnchorApiFactory"/>);
/// these tests run <see cref="LogUploadPruner"/> on a clock of their own.
/// </summary>
public sealed class LogUploadPrunerTests : IClassFixture<LogUploadPrunerTests.PrunerTestFactory>
{
    private readonly PrunerTestFactory _factory;

    public LogUploadPrunerTests(PrunerTestFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Own database; uploads switched off, as after the test phase.</summary>
    public sealed class PrunerTestFactory : AnchorApiFactory { }

    [Fact]
    public async Task Uploads_older_than_14_days_are_deleted_and_younger_ones_kept()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        var old = await SeedUploadAsync(uploadedAt: clock.GetUtcNow().AddDays(-14).AddMinutes(-1));
        var young = await SeedUploadAsync(uploadedAt: clock.GetUtcNow().AddDays(-13));

        var deleted = await BuildPruner(clock).PruneOnceAsync(CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.False(await ExistsAsync(old));
        Assert.True(await ExistsAsync(young));
    }

    [Fact]
    public async Task The_pruner_waits_for_the_quiet_hour_instead_of_pruning_at_startup()
    {
        // Started at 12:00 UTC with the event pruner's run hour at 02:00: the
        // first prune is 14 h away, and nothing is deleted before it.
        var clock = new TimerRecordingTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var old = await SeedUploadAsync(uploadedAt: clock.GetUtcNow().AddDays(-20));
        var timeout = TimeSpan.FromSeconds(10);

        var pruner = BuildPruner(clock);
        await pruner.StartAsync(CancellationToken.None);
        try
        {
            var firstWait = await clock.WaitForTimerAsync(_ => true, timeout);
            Assert.Equal(TimeSpan.FromHours(14), firstWait);
            Assert.True(await ExistsAsync(old));

            clock.Advance(firstWait);
            await clock.WaitForTimerAsync(due => due == TimeSpan.FromHours(24), timeout);
            Assert.False(await ExistsAsync(old));
        }
        finally
        {
            await pruner.StopAsync(CancellationToken.None);
        }
    }

    private LogUploadPruner BuildPruner(TimeProvider clock) =>
        new(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            clock,
            new StaticOptionsMonitor<LogUploadOptions>(new LogUploadOptions { RetentionDays = 14, EnablePruner = false }),
            new StaticOptionsMonitor<EventRetentionOptions>(new EventRetentionOptions { PruneHourUtc = 2, PruneIntervalMinutes = 1440 }),
            NullLogger<LogUploadPruner>.Instance);

    private async Task<Guid> SeedUploadAsync(DateTimeOffset uploadedAt)
    {
        var (teacher, students, @class) = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);
        var session = await TestSeed.AddSessionAsync(_factory, teacher.Id, @class.Id, [students[0].Id], ended: true);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var upload = new SessionLogUpload
        {
            SessionId = session.Id,
            UserId = students[0].Id,
            WindowFrom = session.StartedAt.AddMinutes(-30),
            WindowTo = session.EndedAt!.Value.AddMinutes(10),
            IsComplete = true,
            UploadedAt = uploadedAt,
            ContentLength = 2,
            Content = [0x1f, 0x8b],
        };
        db.SessionLogUploads.Add(upload);
        await db.SaveChangesAsync();
        return upload.Id;
    }

    private async Task<bool> ExistsAsync(Guid uploadId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        return await db.SessionLogUploads.AnyAsync(u => u.Id == uploadId);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
