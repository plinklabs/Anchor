using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Anchor.Api.Controllers;
using Anchor.Api.Realtime;
using Anchor.Api.Tests.FakeAuth;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

/// <summary>
/// The backend's side of contracts/session-baseline.json (#516): the domains
/// every session allows on top of the teacher's bundles. The extension's
/// end-to-end specs hand that file down as every session's baseline and check
/// in Edge that a student can still sign in while the rest of Microsoft's web
/// stays blocked; this test makes sure the file is what the backend really
/// sends, so those specs can't pass against a list the backend no longer uses.
/// <para>
/// Runs the real host in the non-Development "Test" environment with no
/// dashboard origins configured, so a session without bundles carries the
/// baseline and nothing else: no Development carve-outs (#125), no dashboard
/// host (#409).
/// </para>
/// </summary>
public sealed class SessionBaselineContractTests : IClassFixture<AnchorApiFactory>
{
    private const string RelativePath = "contracts/session-baseline.json";

    private readonly AnchorApiFactory _factory;

    public SessionBaselineContractTests(AnchorApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_session_without_bundles_allows_exactly_the_domains_in_the_contract()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);

        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(scenario.Class.Id, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<StartSessionResponse>();

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        var broadcast = Assert.Single(broadcaster.SessionStartedCalls, p => p.SessionId == body!.Id);

        var contract = LoadContractDomains();
        Assert.True(
            contract.SequenceEqual(broadcast.Payload.Domains),
            $"""
            {RelativePath} no longer describes the baseline the backend sends (#516).
            Contract: {Describe(contract)}
            Backend:  {Describe(broadcast.Payload.Domains)}
            Change the file with SessionAllowlist.BaselineDomains, and section 7.2 of focus-system-design.md with them.
            """);
    }

    private static IReadOnlyList<AllowedDomainDto> LoadContractDomains()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, RelativePath);
            if (!File.Exists(path)) continue;

            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            return root["domains"]!.AsArray()
                .Select(d => new AllowedDomainDto(
                    d!["matchType"]!.GetValue<string>(),
                    d["value"]!.GetValue<string>()))
                .ToList();
        }
        throw new FileNotFoundException($"No {RelativePath} above {AppContext.BaseDirectory}.");
    }

    private static string Describe(IEnumerable<AllowedDomainDto> domains) =>
        string.Join(", ", domains.Select(d => $"{d.MatchType} {d.Value}"));
}
