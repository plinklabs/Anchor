using Anchor.Api.Users;
using Anchor.Domain.Schools;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anchor.Api.Schools;

internal sealed class SchoolDirectory : ISchoolDirectory
{
    private readonly IUserDirectorySearch _search;
    private readonly AnchorDbContext _db;

    public SchoolDirectory(IUserDirectorySearch search, AnchorDbContext db)
    {
        _search = search;
        _db = db;
    }

    public async Task<IReadOnlyList<SchoolState>> ListAllAsync(CancellationToken cancellationToken)
    {
        await ReconcileAsync(cancellationToken);
        return await ListPersistedAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SchoolState>> ListPersistedAsync(CancellationToken cancellationToken)
    {
        var schools = await _db.Schools.AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.Name,
                s.IsActive,
                Periods = s.LessonPeriods.Select(p => new SchoolLessonPeriod(p.Start, p.End)).ToList(),
            })
            .ToListAsync(cancellationToken);
        return schools
            .Select(s => new SchoolState(s.Name, s.IsActive, InStartOrder(s.Periods)))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ListActiveNamesAsync(CancellationToken cancellationToken)
    {
        // Steady state: serve teachers straight from the DB — no Graph round-trip.
        if (await _db.Schools.AnyAsync(cancellationToken))
        {
            return await _db.Schools.AsNoTracking()
                .Where(s => s.IsActive)
                .OrderBy(s => s.Name)
                .Select(s => s.Name)
                .ToListAsync(cancellationToken);
        }

        // Feature not configured yet (no school reconciled): fall back to the
        // live company list so teachers keep seeing every school until an admin
        // first opens the Schools tab. No DB write on this hot path.
        return await _search.ListCompaniesAsync(cancellationToken);
    }

    public async Task<SchoolState> SetActiveAsync(string name, bool isActive, CancellationToken cancellationToken)
    {
        var school = await _db.Schools
            .Include(s => s.LessonPeriods)
            .FirstOrDefaultAsync(s => s.Name == name, cancellationToken);
        if (school is null)
        {
            school = new School { Name = name, IsActive = isActive };
            _db.Schools.Add(school);
        }
        else
        {
            school.IsActive = isActive;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return StateOf(school);
    }

    public async Task<SchoolState> SetLessonPeriodsAsync(
        string name, IReadOnlyList<SchoolLessonPeriod> periods, CancellationToken cancellationToken)
    {
        var school = await _db.Schools
            .Include(s => s.LessonPeriods)
            .FirstOrDefaultAsync(s => s.Name == name, cancellationToken);
        if (school is null)
        {
            // Like the activation toggle: a school the admin sees but that has
            // no row yet gets one, active as new schools are.
            school = new School { Name = name, IsActive = true };
            _db.Schools.Add(school);
        }

        // Replaced wholesale: the editor sends the whole timetable each time.
        // Added through the set, not the navigation: a new row with its Guid
        // key already set would otherwise be taken for an existing one.
        _db.LessonPeriods.RemoveRange(school.LessonPeriods.ToList());
        foreach (var period in periods)
        {
            _db.LessonPeriods.Add(new LessonPeriod
            {
                SchoolId = school.Id,
                Start = period.Start,
                End = period.End,
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        return new SchoolState(school.Name, school.IsActive, InStartOrder(periods));
    }

    private static SchoolState StateOf(School school) =>
        new(
            school.Name,
            school.IsActive,
            InStartOrder(school.LessonPeriods.Select(p => new SchoolLessonPeriod(p.Start, p.End))));

    private static IReadOnlyList<SchoolLessonPeriod> InStartOrder(IEnumerable<SchoolLessonPeriod> periods) =>
        periods.OrderBy(p => p.Start).ToList();

    /// <summary>
    /// Discover the live Graph companies and persist any not yet known as active.
    /// Existing rows — including admin deactivations and schools that have since
    /// dropped out of Entra — are left untouched. New schools therefore surface
    /// as active and visible; an admin opts the irrelevant ones out afterwards.
    /// </summary>
    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var companies = await _search.ListCompaniesAsync(cancellationToken);
        if (companies.Count == 0)
            return;

        var known = await _db.Schools.Select(s => s.Name).ToListAsync(cancellationToken);
        var seen = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);

        var added = false;
        foreach (var company in companies)
        {
            if (seen.Add(company))
            {
                _db.Schools.Add(new School { Name = company, IsActive = true });
                added = true;
            }
        }

        if (added)
            await _db.SaveChangesAsync(cancellationToken);
    }
}
