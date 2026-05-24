using FocusAgent.Core.Focus;

namespace FocusAgent.Core.Settings;

public sealed record SessionSettings
{
    public const string SectionName = "Session";

    public List<AllowedAppRule> AllowedApps { get; init; } = new();
    public TimeSpan DuplicateCoalesceWindow { get; init; } = TimeSpan.FromMilliseconds(500);
}
