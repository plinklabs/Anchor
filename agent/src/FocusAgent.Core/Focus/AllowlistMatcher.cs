using System.Collections.Immutable;

namespace FocusAgent.Core.Focus;

/// <summary>
/// Decides whether a foregrounded app is allowed during a session. As of
/// #70 the rules come straight from the SessionStarted payload — the
/// backend's <c>SessionAllowlistExpander</c> merges the teacher-picked
/// bundles with the baseline (Edge, explorer, etc.) into a single list
/// before broadcasting. The agent's only local addition is its own
/// process so the watcher cannot lock itself out.
/// </summary>
public sealed class AllowlistMatcher
{
    private readonly ImmutableArray<AllowedAppRule> _rules;
    private readonly string? _ownProcessName;

    public AllowlistMatcher(IEnumerable<AllowedAppRule> rules, string? ownProcessName = null)
    {
        _rules = (rules ?? Array.Empty<AllowedAppRule>())
            .Where(r => !string.IsNullOrWhiteSpace(r.Value))
            .ToImmutableArray();
        _ownProcessName = string.IsNullOrWhiteSpace(ownProcessName)
            ? null
            : NormalizeProcessName(ownProcessName);
    }

    /// <summary>
    /// All rules carried by the session payload. The overlay's allowed-apps
    /// list reuses this, so anything baseline-ish that wouldn't be useful to
    /// show a student must be filtered server-side before the broadcast.
    /// </summary>
    public ImmutableArray<AllowedAppRule> UserRules => _rules;

    public bool IsAllowed(AppInfo app)
    {
        var processName = NormalizeProcessName(app.ProcessName);

        if (_ownProcessName is not null && processName.Equals(_ownProcessName, StringComparison.OrdinalIgnoreCase))
            return true;

        return _rules.Any(rule => Matches(rule, app, processName));
    }

    private static bool Matches(AllowedAppRule rule, AppInfo app, string normalizedProcessName) => rule.MatchKind switch
    {
        AllowedAppMatchKind.ProcessName =>
            normalizedProcessName.Equals(NormalizeProcessName(rule.Value), StringComparison.OrdinalIgnoreCase),

        AllowedAppMatchKind.ExecutablePath =>
            !string.IsNullOrEmpty(app.ExecutablePath) &&
            string.Equals(
                Path.GetFullPath(app.ExecutablePath),
                Path.GetFullPath(rule.Value),
                StringComparison.OrdinalIgnoreCase),

        AllowedAppMatchKind.Publisher =>
            !string.IsNullOrEmpty(app.SignedPublisher) &&
            string.Equals(app.SignedPublisher, rule.Value, StringComparison.OrdinalIgnoreCase),

        _ => false,
    };

    private static string NormalizeProcessName(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];
        return trimmed;
    }
}
