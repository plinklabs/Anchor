using FocusAgent.Core.Dtos;

namespace FocusAgent.Core.Focus;

/// <summary>
/// Maps wire-side <see cref="AllowedAppDto"/>s (string match kinds, as
/// sent by the backend) to the strongly-typed <see cref="AllowedAppRule"/>s
/// the matcher consumes. Unknown match kinds are dropped rather than
/// guessed — better to under-allow than to silently re-interpret an
/// unfamiliar value.
/// </summary>
internal static class AllowedAppRuleMapper
{
    public static IReadOnlyList<AllowedAppRule> FromPayload(IReadOnlyList<AllowedAppDto> apps)
    {
        if (apps is null || apps.Count == 0)
            return Array.Empty<AllowedAppRule>();

        var rules = new List<AllowedAppRule>(apps.Count);
        foreach (var app in apps)
        {
            if (!TryParseMatchKind(app.MatchKind, out var kind)) continue;
            rules.Add(new AllowedAppRule { MatchKind = kind, Value = app.Value });
        }
        return rules;
    }

    private static bool TryParseMatchKind(string raw, out AllowedAppMatchKind kind) =>
        Enum.TryParse(raw, ignoreCase: true, out kind);
}
