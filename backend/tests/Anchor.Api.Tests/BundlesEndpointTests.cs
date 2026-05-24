using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Domain.Bundles;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

public sealed class BundlesEndpointTests : IClassFixture<AnchorApiFactory>
{
    private readonly AnchorApiFactory _factory;

    public BundlesEndpointTests(AnchorApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GET_bundles_unauthenticated_returns_401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/bundles");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_bundles_returns_seeded_bundles_for_teacher()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var a = await TestSeed.AddBundleAsync(_factory, "Alpha-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        var b = await TestSeed.AddBundleAsync(_factory, "Beta-" + Guid.NewGuid().ToString("N").Substring(0, 6));

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var body = await client.GetFromJsonAsync<List<BundleSummary>>("/bundles");

        Assert.NotNull(body);
        Assert.Contains(body!, s => s.Id == a.Id && s.Name == a.Name && s.Version == a.Version);
        Assert.Contains(body!, s => s.Id == b.Id && s.Name == b.Name && s.Version == b.Version);
    }

    [Fact]
    public async Task GET_bundles_returns_seeded_bundles_for_student()
    {
        // Per #69: any role can read. A student needs to be able to fetch
        // bundles only insofar as the same API surface stays usable across
        // roles — we don't have a student-facing UI for it yet, but the
        // endpoint must not gate on Teacher.
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var bundle = await TestSeed.AddBundleAsync(_factory, "Gamma-" + Guid.NewGuid().ToString("N").Substring(0, 6));

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[0]);

        var response = await client.GetAsync("/bundles");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<List<BundleSummary>>();
        Assert.NotNull(body);
        Assert.Contains(body!, s => s.Id == bundle.Id);
    }

    [Fact]
    public async Task GET_bundle_returns_404_for_missing_id()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.GetAsync($"/bundles/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GET_bundle_returns_detail_with_entries()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory);
        var bundle = await AddBundleWithEntriesAsync("Microsoft-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            new EntryFixture("*.office.com", BundleEntryMatchType.Wildcard),
            new EntryFixture("outlook.office.com", BundleEntryMatchType.Exact));

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var body = await client.GetFromJsonAsync<BundleDetail>($"/bundles/{bundle.Id}");

        Assert.NotNull(body);
        Assert.Equal(bundle.Id, body!.Id);
        Assert.Equal(bundle.Name, body.Name);
        Assert.Equal(2, body.Entries.Count);
        Assert.Contains(body.Entries, e => e.Value == "*.office.com" && e.MatchType == BundleEntryMatchType.Wildcard);
        Assert.Contains(body.Entries, e => e.Value == "outlook.office.com" && e.MatchType == BundleEntryMatchType.Exact);
        Assert.All(body.Entries, e => Assert.Equal(BundleEntryKind.Domain, e.Kind));
    }

    private async Task<Bundle> AddBundleWithEntriesAsync(string name, params EntryFixture[] entries)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var bundle = new Bundle { Name = name, Version = 1 };
        db.Bundles.Add(bundle);
        foreach (var e in entries)
        {
            db.BundleEntries.Add(new BundleEntry
            {
                BundleId = bundle.Id,
                Kind = BundleEntryKind.Domain,
                Value = e.Value,
                MatchType = e.MatchType,
            });
        }
        await db.SaveChangesAsync();
        return bundle;
    }

    private sealed record EntryFixture(string Value, BundleEntryMatchType MatchType);
}
