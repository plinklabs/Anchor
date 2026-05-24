using Anchor.Domain.Bundles;
using Anchor.Domain.Classes;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

internal static class TestSeed
{
    public static async Task<TestScenario> SeedClassWithTeacherAndStudentsAsync(
        AnchorApiFactory factory,
        int studentCount = 2)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();

        var teacher = new User
        {
            EntraOid = Guid.NewGuid(),
            DisplayName = "Teacher " + Guid.NewGuid().ToString("N").Substring(0, 6),
            Role = UserRole.Teacher,
        };
        var students = Enumerable.Range(0, studentCount)
            .Select(i => new User
            {
                EntraOid = Guid.NewGuid(),
                DisplayName = $"Student {i} {Guid.NewGuid():N}".Substring(0, 16),
                Role = UserRole.Student,
            })
            .ToList();

        var @class = new Class
        {
            Name = "Class-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            SchoolYear = "2025-2026",
        };

        var memberships = new List<ClassMembership>
        {
            new() { ClassId = @class.Id, UserId = teacher.Id, Role = ClassMembershipRole.Teacher },
        };
        memberships.AddRange(students.Select(s => new ClassMembership
        {
            ClassId = @class.Id,
            UserId = s.Id,
            Role = ClassMembershipRole.Member,
        }));

        db.Users.Add(teacher);
        db.Users.AddRange(students);
        db.Classes.Add(@class);
        db.ClassMemberships.AddRange(memberships);

        await db.SaveChangesAsync();

        return new TestScenario(teacher, students, @class);
    }

    public static async Task<User> AddUserAsync(AnchorApiFactory factory, UserRole role, string displayName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var user = new User
        {
            EntraOid = Guid.NewGuid(),
            DisplayName = displayName,
            Role = role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public static async Task<Bundle> AddBundleAsync(AnchorApiFactory factory, string name, bool isArchived = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var bundle = new Bundle { Name = name, Version = 1, IsArchived = isArchived };
        db.Bundles.Add(bundle);
        await db.SaveChangesAsync();
        return bundle;
    }

    public static async Task AddBundleEntryAsync(
        AnchorApiFactory factory,
        Guid bundleId,
        BundleEntryKind kind,
        string value,
        BundleEntryMatchType matchType)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        db.BundleEntries.Add(new BundleEntry
        {
            BundleId = bundleId,
            Kind = kind,
            Value = value,
            MatchType = matchType,
        });
        await db.SaveChangesAsync();
    }

    public static async Task<Session> AddSessionAsync(
        AnchorApiFactory factory,
        Guid teacherId,
        Guid classId,
        IReadOnlyCollection<Guid> participantUserIds,
        SessionMode mode = SessionMode.Strict,
        bool ended = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var session = new Session
        {
            TeacherId = teacherId,
            ClassId = classId,
            Mode = mode,
            StartedAt = startedAt,
            EndedAt = ended ? startedAt.AddMinutes(2) : null,
            JoinCode = Random.Shared.Next(0, 1_000_000).ToString("D6"),
        };
        db.Sessions.Add(session);
        foreach (var userId in participantUserIds)
        {
            db.SessionParticipants.Add(new SessionParticipant
            {
                SessionId = session.Id,
                UserId = userId,
            });
        }
        await db.SaveChangesAsync();
        return session;
    }
}

internal sealed record TestScenario(User Teacher, IReadOnlyList<User> Students, Class Class);
