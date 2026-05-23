using Anchor.Domain.Classes;
using Anchor.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Anchor.Infrastructure.Persistence;

public static class DevDataSeeder
{
    private static readonly Guid TeacherEntraOid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid StudentEntraOid = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static async Task SeedAsync(AnchorDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Users.AnyAsync(cancellationToken)) return;

        var teacher = new User
        {
            EntraOid = TeacherEntraOid,
            DisplayName = "Dev Teacher",
            Role = UserRole.Teacher,
        };
        var student = new User
        {
            EntraOid = StudentEntraOid,
            DisplayName = "Dev Student",
            Role = UserRole.Student,
        };

        var klas = new Class
        {
            Name = "3A",
            SchoolYear = "2025-2026",
        };

        var teacherMembership = new ClassMembership
        {
            ClassId = klas.Id,
            UserId = teacher.Id,
            Role = ClassMembershipRole.Teacher,
        };
        var studentMembership = new ClassMembership
        {
            ClassId = klas.Id,
            UserId = student.Id,
            Role = ClassMembershipRole.Member,
        };

        db.Users.AddRange(teacher, student);
        db.Classes.Add(klas);
        db.ClassMemberships.AddRange(teacherMembership, studentMembership);

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Dev convenience: make sure any real teacher who signs in ends up with a
    /// Teacher membership in the seeded class so the end-to-end "Start session"
    /// flow works without manual class-data wiring. No-op outside development.
    /// </summary>
    public static async Task EnsureDevTeacherMembershipAsync(
        AnchorDbContext db, Guid userId, CancellationToken cancellationToken = default)
    {
        var seededClass = await db.Classes
            .FirstOrDefaultAsync(c => c.Name == "3A", cancellationToken);
        if (seededClass is null) return;

        var alreadyTeacher = await db.ClassMemberships.AnyAsync(
            m => m.ClassId == seededClass.Id
                 && m.UserId == userId
                 && m.Role == ClassMembershipRole.Teacher,
            cancellationToken);
        if (alreadyTeacher) return;

        db.ClassMemberships.Add(new ClassMembership
        {
            ClassId = seededClass.Id,
            UserId = userId,
            Role = ClassMembershipRole.Teacher,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
