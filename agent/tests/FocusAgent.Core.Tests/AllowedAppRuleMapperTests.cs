using FocusAgent.Core.Dtos;
using FocusAgent.Core.Focus;

namespace FocusAgent.Core.Tests;

/// <summary>
/// AllowedAppRuleMapper is internal — exercised via the InternalsVisibleTo
/// already in place for the rest of the Core types. These tests document
/// the deliberate behaviour around unknown wire values.
/// </summary>
public class AllowedAppRuleMapperTests
{
    [Fact]
    public void Empty_or_null_input_returns_empty()
    {
        Assert.Empty(AllowedAppRuleMapper.FromPayload(Array.Empty<AllowedAppDto>()));
        Assert.Empty(AllowedAppRuleMapper.FromPayload(null!));
    }

    [Fact]
    public void Known_match_kinds_round_trip_case_insensitively()
    {
        var rules = AllowedAppRuleMapper.FromPayload(new[]
        {
            new AllowedAppDto("ProcessName", "winword"),
            new AllowedAppDto("EXECUTABLEPATH", @"C:\App\app.exe"),
            new AllowedAppDto("publisher", "Acme"),
        });

        Assert.Collection(rules,
            r => { Assert.Equal(AllowedAppMatchKind.ProcessName, r.MatchKind); Assert.Equal("winword", r.Value); },
            r => { Assert.Equal(AllowedAppMatchKind.ExecutablePath, r.MatchKind); Assert.Equal(@"C:\App\app.exe", r.Value); },
            r => { Assert.Equal(AllowedAppMatchKind.Publisher, r.MatchKind); Assert.Equal("Acme", r.Value); });
    }

    [Fact]
    public void Unknown_match_kinds_are_dropped_not_guessed()
    {
        var rules = AllowedAppRuleMapper.FromPayload(new[]
        {
            new AllowedAppDto("ProcessName", "winword"),
            new AllowedAppDto("Bogus", "something"),
        });

        Assert.Single(rules);
        Assert.Equal("winword", rules[0].Value);
    }
}
