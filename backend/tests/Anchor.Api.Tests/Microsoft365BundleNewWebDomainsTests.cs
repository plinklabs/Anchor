using System.Text.RegularExpressions;
using Anchor.Domain.Bundles;
using Anchor.Infrastructure.Persistence;
using Anchor.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Anchor.Api.Tests;

/// <summary>
/// #531: Microsoft 365 on the web moved to <c>*.cloud.microsoft</c>, reached
/// through www.microsoft365.com, so the Microsoft 365 bundle lists both now.
/// The seeds only insert a bundle that is missing, so a deployment's existing
/// bundle (the pilot's) gets them from the Microsoft365BundleNewWebDomains
/// migration, which the deploy pipeline applies. These tests run that
/// migration's SQL against SQLite and hold the two seeds to each other.
/// </summary>
public sealed class Microsoft365BundleNewWebDomainsTests : IAsyncLifetime
{
    private const string Ms365 = "Microsoft 365";

    /// <summary>The Microsoft 365 bundle as both seeds inserted it before #531.</summary>
    private static readonly (string Value, BundleEntryMatchType MatchType)[] SeededBefore531 =
    {
        ("*.office.com", BundleEntryMatchType.Wildcard),
        ("*.office365.com", BundleEntryMatchType.Wildcard),
        ("*.microsoft.com", BundleEntryMatchType.Wildcard),
        ("*.microsoftonline.com", BundleEntryMatchType.Wildcard),
        ("*.live.com", BundleEntryMatchType.Wildcard),
        ("*.sharepoint.com", BundleEntryMatchType.Wildcard),
        ("outlook.office.com", BundleEntryMatchType.Exact),
        ("teams.microsoft.com", BundleEntryMatchType.Exact),
    };

    private SqliteConnection _connection = null!;
    private AnchorDbContext _db = null!;

    public Task InitializeAsync()
    {
        (_connection, _db) = OpenDatabase();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task A_bundle_seeded_before_531_ends_up_as_the_seeds_list_it_now()
    {
        var bundleId = await AddBundleAsync(Ms365, version: 1, SeededBefore531);

        Up();

        var entries = await EntriesAsync(bundleId);
        Assert.Contains(("Domain", "Wildcard", "*.cloud.microsoft"), entries);
        Assert.Contains(("Domain", "Wildcard", "*.microsoft365.com"), entries);
        // Exactly what a deployment seeded today starts with, so a pilot that
        // was seeded before #531 doesn't differ from a fresh one.
        Assert.Equal(await EntriesSeededTodayAsync(), entries);
        // As an edit in the catalogue editor would leave it.
        Assert.Equal(2, await VersionAsync(bundleId));
    }

    [Fact]
    public async Task What_a_teacher_changed_stays_and_a_domain_already_listed_is_not_added_twice()
    {
        // Edited in the catalogue: *.live.com taken out, a site and one of the
        // new domains (in another case) put in.
        var bundleId = await AddBundleAsync(Ms365, version: 3, new[]
        {
            ("*.office.com", BundleEntryMatchType.Wildcard),
            ("*.sharepoint.com", BundleEntryMatchType.Wildcard),
            ("kahoot.it", BundleEntryMatchType.Exact),
            ("*.Cloud.Microsoft", BundleEntryMatchType.Wildcard),
        });

        Up();

        Assert.Equal(
            new[]
            {
                ("Domain", "Wildcard", "*.Cloud.Microsoft"),
                ("Domain", "Wildcard", "*.microsoft365.com"),
                ("Domain", "Wildcard", "*.office.com"),
                ("Domain", "Wildcard", "*.sharepoint.com"),
                ("Domain", "Exact", "kahoot.it"),
            },
            await EntriesAsync(bundleId));
        Assert.Equal(4, await VersionAsync(bundleId));
    }

    [Fact]
    public async Task Only_the_live_Microsoft_365_bundle_changes()
    {
        var archived = await AddBundleAsync(Ms365, version: 1, SeededBefore531, archived: true);
        var live = await AddBundleAsync(Ms365, version: 2, SeededBefore531);
        var smartschool = await AddBundleAsync("Smartschool", version: 1, new[]
        {
            ("*.smartschool.be", BundleEntryMatchType.Wildcard),
        });

        Up();

        Assert.Contains(("Domain", "Wildcard", "*.cloud.microsoft"), await EntriesAsync(live));
        // Above every version of the name, archived ones included.
        Assert.Equal(3, await VersionAsync(live));
        Assert.Equal(SeededBefore531.Length, (await EntriesAsync(archived)).Count);
        Assert.Equal(1, await VersionAsync(archived));
        Assert.Equal(new[] { ("Domain", "Wildcard", "*.smartschool.be") }, await EntriesAsync(smartschool));
        Assert.Equal(1, await VersionAsync(smartschool));
    }

    [Fact]
    public async Task A_bundle_that_lists_both_already_is_left_alone()
    {
        var bundleId = await AddBundleAsync(Ms365, version: 5, new[]
        {
            ("*.cloud.microsoft", BundleEntryMatchType.Wildcard),
            ("*.microsoft365.com", BundleEntryMatchType.Wildcard),
        });

        Up();

        Assert.Equal(2, (await EntriesAsync(bundleId)).Count);
        Assert.Equal(5, await VersionAsync(bundleId));
    }

    [Fact]
    public async Task A_database_without_the_bundle_is_left_alone()
    {
        // A fresh deployment: the deploy migrates before seed-bundles.ps1 has
        // inserted anything. The seed then brings the new domains itself.
        Up();

        Assert.Empty(await _db.Bundles.ToListAsync());
        Assert.Empty(await _db.BundleEntries.ToListAsync());
    }

    [Fact]
    public async Task Down_takes_out_only_what_Up_added()
    {
        var bundleId = await AddBundleAsync(Ms365, version: 1, new[]
        {
            ("*.office.com", BundleEntryMatchType.Wildcard),
            ("*.cloud.microsoft", BundleEntryMatchType.Wildcard),
        });

        Up();
        Down();

        // *.cloud.microsoft was the teacher's, so it stays; *.microsoft365.com
        // was the migration's.
        Assert.Equal(
            new[]
            {
                ("Domain", "Wildcard", "*.cloud.microsoft"),
                ("Domain", "Wildcard", "*.office.com"),
            },
            await EntriesAsync(bundleId));
    }

    [Fact]
    public void The_deploy_applies_the_migration()
    {
        // backend-deploy.yml applies every migration EF finds in the
        // infrastructure assembly; this one must be among them.
        Assert.Contains(_db.Database.GetMigrations(), id => id.EndsWith("_Microsoft365BundleNewWebDomains", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Both_seeds_list_the_same_Microsoft_365_bundle()
    {
        // DevDataSeeder seeds a development database, scripts/seed-bundles.ps1 a
        // deployed one; a domain in only one of them reaches only half the
        // deployments.
        var script = File.ReadAllText(FindRepoFile("scripts/seed-bundles.ps1"));
        var block = Regex.Match(script, @"Name\s*=\s*'Microsoft 365'(?<body>.*?)\n\s*\)", RegexOptions.Singleline);
        Assert.True(block.Success, "No 'Microsoft 365' bundle in scripts/seed-bundles.ps1.");
        var scripted = Regex.Matches(
                block.Groups["body"].Value,
                @"@\{\s*Value\s*=\s*'(?<value>[^']+)';\s*MatchType\s*=\s*'(?<match>[^']+)';\s*Kind\s*=\s*'(?<kind>[^']+)'\s*\}")
            .Select(m => (m.Groups["kind"].Value, m.Groups["match"].Value, m.Groups["value"].Value))
            .OrderBy(e => e.Item3, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(await EntriesSeededTodayAsync(), scripted);
    }

    private void Up() => Run(new Microsoft365BundleNewWebDomains().UpOperations);

    private void Down() => Run(new Microsoft365BundleNewWebDomains().DownOperations);

    private void Run(IReadOnlyList<MigrationOperation> operations)
    {
        foreach (var operation in operations)
        {
            _db.Database.ExecuteSqlRaw(Assert.IsType<SqlOperation>(operation).Sql);
        }
        _db.ChangeTracker.Clear();
    }

    private async Task<Guid> AddBundleAsync(
        string name,
        int version,
        IEnumerable<(string Value, BundleEntryMatchType MatchType)> entries,
        bool archived = false)
    {
        var bundle = new Bundle { Name = name, Version = version, IsArchived = archived };
        _db.Bundles.Add(bundle);
        foreach (var (value, matchType) in entries)
        {
            _db.BundleEntries.Add(new BundleEntry
            {
                BundleId = bundle.Id,
                Kind = BundleEntryKind.Domain,
                Value = value,
                MatchType = matchType,
            });
        }
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return bundle.Id;
    }

    private Task<List<(string Kind, string MatchType, string Value)>> EntriesAsync(Guid bundleId) =>
        EntriesAsync(_db, bundleId);

    private async Task<int> VersionAsync(Guid bundleId) =>
        (await _db.Bundles.AsNoTracking().SingleAsync(b => b.Id == bundleId)).Version;

    /// <summary>The Microsoft 365 bundle DevDataSeeder inserts into a new database.</summary>
    private static async Task<List<(string Kind, string MatchType, string Value)>> EntriesSeededTodayAsync()
    {
        var (connection, db) = OpenDatabase();
        await using var _ = connection;
        await using var __ = db;
        await DevDataSeeder.EnsureDevBundlesAsync(db);
        var bundle = await db.Bundles.AsNoTracking().SingleAsync(b => b.Name == Ms365);
        return await EntriesAsync(db, bundle.Id);
    }

    private static async Task<List<(string Kind, string MatchType, string Value)>> EntriesAsync(
        AnchorDbContext db, Guid bundleId)
    {
        var rows = await db.BundleEntries.AsNoTracking().Where(e => e.BundleId == bundleId).ToListAsync();
        return rows
            .Select(e => (e.Kind.ToString(), e.MatchType.ToString(), e.Value))
            .OrderBy(e => e.Item3, StringComparer.Ordinal)
            .ToList();
    }

    private static (SqliteConnection, AnchorDbContext) OpenDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var db = new AnchorDbContext(new DbContextOptionsBuilder<AnchorDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        return (connection, db);
    }

    private static string FindRepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException($"No {relativePath} above {AppContext.BaseDirectory}.");
    }
}
