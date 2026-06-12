using Anchor.Api.Sessions;
using Anchor.Domain.Bundles;
using Anchor.Domain.Classes;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Anchor.Api.Tests;

/// <summary>
/// Unit tests for the bundle → wire-DTO expansion (#70). Each test stands up
/// its own in-memory SQLite so bundle state can't bleed between cases.
/// </summary>
public sealed class SessionAllowlistExpanderTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private AnchorDbContext _db = null!;

    public Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AnchorDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AnchorDbContext(options);
        _db.Database.EnsureCreated();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Empty_bundle_set_returns_baseline_only()
    {
        var expander = new SessionAllowlistExpander(_db, Env(Environments.Production));

        var expanded = await expander.ExpandAsync(Array.Empty<Guid>());

        Assert.Equal(SessionAllowlist.BaselineApps.Count, expanded.Apps.Count);
        Assert.Equal(SessionAllowlist.BaselineDomains.Count, expanded.Domains.Count);
        Assert.Contains(expanded.Apps, a => a.MatchKind == "ProcessName" && a.Value == "msedge");
        Assert.Contains(expanded.Apps, a => a.MatchKind == "ProcessName" && a.Value == "explorer");
        Assert.Contains(expanded.Domains, d => d.Value == "*.microsoftonline.com");
    }

    [Fact]
    public async Task Bundle_domain_entries_are_merged_with_baseline_and_match_types_translated()
    {
        var bundleId = await SeedBundleAsync("Smartschool", entries: new[]
        {
            (BundleEntryKind.Domain, "*.smartschool.be", BundleEntryMatchType.Wildcard),
            (BundleEntryKind.Domain, "exact.example.com", BundleEntryMatchType.Exact),
            (BundleEntryKind.Domain, "school.local", BundleEntryMatchType.Suffix),
        });
        var expander = new SessionAllowlistExpander(_db, Env(Environments.Production));

        var expanded = await expander.ExpandAsync(new[] { bundleId });

        Assert.Contains(expanded.Domains, d => d.MatchType == "Wildcard" && d.Value == "*.smartschool.be");
        Assert.Contains(expanded.Domains, d => d.MatchType == "Exact" && d.Value == "exact.example.com");
        Assert.Contains(expanded.Domains, d => d.MatchType == "Suffix" && d.Value == "school.local");
        // Baseline survives.
        Assert.Contains(expanded.Domains, d => d.Value == "*.office.com");
    }

    [Fact]
    public async Task Bundle_app_entries_map_match_type_to_app_kind()
    {
        var bundleId = await SeedBundleAsync("Math Tools", entries: new[]
        {
            (BundleEntryKind.App, "winword", BundleEntryMatchType.Exact),
            (BundleEntryKind.App, "International GeoGebra Institute", BundleEntryMatchType.SignedPublisher),
        });
        var expander = new SessionAllowlistExpander(_db, Env(Environments.Production));

        var expanded = await expander.ExpandAsync(new[] { bundleId });

        Assert.Contains(expanded.Apps, a => a.MatchKind == "ProcessName" && a.Value == "winword");
        Assert.Contains(expanded.Apps, a => a.MatchKind == "Publisher" && a.Value == "International GeoGebra Institute");
    }

    [Fact]
    public async Task Duplicate_entries_across_baseline_and_bundles_are_deduped()
    {
        var bundleId = await SeedBundleAsync("Already-Baseline", entries: new[]
        {
            (BundleEntryKind.App, "MSEDGE", BundleEntryMatchType.Exact),
            (BundleEntryKind.Domain, "*.office.com", BundleEntryMatchType.Wildcard),
        });
        var expander = new SessionAllowlistExpander(_db, Env(Environments.Production));

        var expanded = await expander.ExpandAsync(new[] { bundleId });

        Assert.Single(expanded.Apps, a => a.MatchKind == "ProcessName" && a.Value.Equals("msedge", StringComparison.OrdinalIgnoreCase));
        Assert.Single(expanded.Domains, d => d.MatchType == "Wildcard" && d.Value.Equals("*.office.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Multi_bundle_expansion_unions_entries()
    {
        var a = await SeedBundleAsync("A", entries: new[]
        {
            (BundleEntryKind.Domain, "*.a.example", BundleEntryMatchType.Wildcard),
        });
        var b = await SeedBundleAsync("B", entries: new[]
        {
            (BundleEntryKind.Domain, "*.b.example", BundleEntryMatchType.Wildcard),
        });
        var expander = new SessionAllowlistExpander(_db, Env(Environments.Production));

        var expanded = await expander.ExpandAsync(new[] { a, b });

        Assert.Contains(expanded.Domains, d => d.Value == "*.a.example");
        Assert.Contains(expanded.Domains, d => d.Value == "*.b.example");
    }

    [Fact]
    public async Task ExpandForSessionAsync_pulls_bundles_through_SessionBundles_join()
    {
        // The Start path passes bundle ids in directly; the rejoin path
        // doesn't have them in hand and has to walk SessionBundles. Pin
        // that the two code paths produce the same expansion.
        var bundleId = await SeedBundleAsync("Live", entries: new[]
        {
            (BundleEntryKind.Domain, "*.live.example", BundleEntryMatchType.Wildcard),
        });
        var teacher = new User { EntraOid = Guid.NewGuid(), DisplayName = "T", Role = UserRole.Teacher };
        var @class = new Class { Name = "Maths 3", SchoolYear = "2025-2026" };
        _db.Users.Add(teacher);
        _db.Classes.Add(@class);
        await _db.SaveChangesAsync();

        var sessionId = Guid.NewGuid();
        _db.Sessions.Add(new Session
        {
            Id = sessionId,
            TeacherId = teacher.Id,
            ClassId = @class.Id,
            StartedAt = DateTimeOffset.UtcNow,
            JoinCode = "000000",
        });
        _db.SessionBundles.Add(new SessionBundle { SessionId = sessionId, BundleId = bundleId });
        await _db.SaveChangesAsync();

        var expander = new SessionAllowlistExpander(_db, Env(Environments.Production));
        var expanded = await expander.ExpandForSessionAsync(sessionId);

        Assert.Contains(expanded.Domains, d => d.Value == "*.live.example");
    }

    [Fact]
    public async Task ExpandForSessionAsync_folds_whole_class_unblock_grants()
    {
        // #101: a whole-class grant applies to everyone in the session, so the
        // rejoin / join-by-code expansion must carry it — otherwise a late
        // joiner or reconnecting student would miss a host opened for the class.
        var teacher = new User { EntraOid = Guid.NewGuid(), DisplayName = "T", Role = UserRole.Teacher };
        var @class = new Class { Name = "Maths 4", SchoolYear = "2025-2026" };
        _db.Users.Add(teacher);
        _db.Classes.Add(@class);
        await _db.SaveChangesAsync();

        var sessionId = Guid.NewGuid();
        _db.Sessions.Add(new Session
        {
            Id = sessionId,
            TeacherId = teacher.Id,
            ClassId = @class.Id,
            StartedAt = DateTimeOffset.UtcNow,
            JoinCode = "111111",
        });
        _db.SessionWideUnblockGrants.Add(new SessionWideUnblockGrant
        {
            SessionId = sessionId,
            Host = "reddit.com",
            GrantedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();

        var expander = new SessionAllowlistExpander(_db, Env(Environments.Production));
        var expanded = await expander.ExpandForSessionAsync(sessionId);

        Assert.Contains(expanded.Domains, d => d.MatchType == "Suffix" && d.Value == "reddit.com");
        // Baseline still present — the grant is additive, not a replacement.
        Assert.Contains(expanded.Domains, d => d.Value == "*.office.com");
    }

    [Fact]
    public async Task Empty_or_whitespace_bundle_values_are_skipped()
    {
        var bundleId = await SeedBundleAsync("Sloppy", entries: new[]
        {
            (BundleEntryKind.Domain, "   ", BundleEntryMatchType.Exact),
            (BundleEntryKind.App, "", BundleEntryMatchType.Exact),
            (BundleEntryKind.Domain, "real.example", BundleEntryMatchType.Exact),
        });
        var expander = new SessionAllowlistExpander(_db, Env(Environments.Production));

        var expanded = await expander.ExpandAsync(new[] { bundleId });

        Assert.Contains(expanded.Domains, d => d.Value == "real.example");
        Assert.DoesNotContain(expanded.Domains, d => string.IsNullOrWhiteSpace(d.Value));
        Assert.DoesNotContain(expanded.Apps, a => string.IsNullOrWhiteSpace(a.Value));
    }

    [Fact]
    public async Task Development_build_adds_localhost_and_vscode_carveouts()
    {
        // #125: a dev build must keep the dashboard/backend reachable (localhost)
        // and the editor usable (VS Code) even while a session enforces the list.
        var expander = new SessionAllowlistExpander(_db, Env(Environments.Development));

        var expanded = await expander.ExpandAsync(Array.Empty<Guid>());

        Assert.Contains(expanded.Domains, d => d.MatchType == "Exact" && d.Value == "localhost");
        Assert.Contains(expanded.Domains, d => d.MatchType == "Exact" && d.Value == "127.0.0.1");
        Assert.Contains(expanded.Apps, a => a.MatchKind == "ProcessName" && a.Value == "Code");
        // Baseline survives alongside the carve-outs.
        Assert.Contains(expanded.Apps, a => a.MatchKind == "ProcessName" && a.Value == "msedge");
    }

    [Fact]
    public async Task NonDevelopment_build_includes_neither_carveout()
    {
        // Acceptance: a Release/non-Development build must include NEITHER the
        // localhost domain nor VS Code — production stays fully locked down.
        var expander = new SessionAllowlistExpander(_db, Env(Environments.Production));

        var expanded = await expander.ExpandAsync(Array.Empty<Guid>());

        Assert.DoesNotContain(expanded.Domains, d => d.Value == "localhost");
        Assert.DoesNotContain(expanded.Domains, d => d.Value == "127.0.0.1");
        Assert.DoesNotContain(expanded.Apps, a => a.Value == "Code");
        // Baseline still present — we only stripped the dev carve-outs.
        Assert.Contains(expanded.Apps, a => a.Value == "msedge");
    }

    private static IHostEnvironment Env(string environmentName) =>
        new StubHostEnvironment { EnvironmentName = environmentName };

    /// <summary>
    /// Minimal <see cref="IHostEnvironment"/> whose only meaningful state is the
    /// environment name — that is all <c>IsDevelopment()</c> reads.
    /// </summary>
    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Anchor.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private async Task<Guid> SeedBundleAsync(
        string name,
        IReadOnlyList<(BundleEntryKind Kind, string Value, BundleEntryMatchType MatchType)> entries)
    {
        var bundle = new Bundle { Name = name, Version = 1 };
        _db.Bundles.Add(bundle);
        foreach (var (kind, value, matchType) in entries)
        {
            _db.BundleEntries.Add(new BundleEntry
            {
                BundleId = bundle.Id,
                Kind = kind,
                Value = value,
                MatchType = matchType,
            });
        }
        await _db.SaveChangesAsync();
        return bundle.Id;
    }
}
