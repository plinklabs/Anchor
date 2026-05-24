using FocusAgent.Core.Focus;

namespace FocusAgent.Core.Tests;

public class AllowlistMatcherTests
{
    // Post-#70 the agent's matcher carries no built-in baseline — Edge,
    // explorer, the bundle expansion, all come from the SessionStarted
    // payload's Apps list. The matcher's only local guarantee is that
    // its own process is allowed (so the watcher can't lock itself out).

    [Fact]
    public void Notepad_is_blocked_when_not_in_rules()
    {
        var matcher = new AllowlistMatcher(Array.Empty<AllowedAppRule>());

        Assert.False(matcher.IsAllowed(new AppInfo("notepad", @"C:\Windows\System32\notepad.exe", "Microsoft Windows")));
    }

    [Fact]
    public void Edge_is_blocked_when_payload_does_not_carry_it()
    {
        // Inverse of the pre-#70 behaviour: msedge is no longer
        // baseline-allowed locally. If the backend forgets to ship it in
        // the payload (misconfiguration), the matcher honours the wire,
        // not its old baseline.
        var matcher = new AllowlistMatcher(Array.Empty<AllowedAppRule>());

        Assert.False(matcher.IsAllowed(new AppInfo("msedge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "Microsoft Corporation")));
    }

    [Fact]
    public void Edge_is_allowed_when_payload_carries_it()
    {
        var matcher = new AllowlistMatcher(new[]
        {
            new AllowedAppRule { MatchKind = AllowedAppMatchKind.ProcessName, Value = "msedge" },
        });

        Assert.True(matcher.IsAllowed(new AppInfo("msedge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "Microsoft Corporation")));
        Assert.True(matcher.IsAllowed(new AppInfo("MSEDGE.EXE", null, null)));
    }

    [Fact]
    public void ProcessName_rule_ignores_exe_suffix_and_casing()
    {
        var matcher = new AllowlistMatcher(new[]
        {
            new AllowedAppRule { MatchKind = AllowedAppMatchKind.ProcessName, Value = "Notepad.EXE" },
        });

        Assert.True(matcher.IsAllowed(new AppInfo("notepad", null, null)));
        Assert.True(matcher.IsAllowed(new AppInfo("NOTEPAD.exe", null, null)));
    }

    [Fact]
    public void ExecutablePath_rule_matches_only_exact_normalized_path()
    {
        var matcher = new AllowlistMatcher(new[]
        {
            new AllowedAppRule { MatchKind = AllowedAppMatchKind.ExecutablePath, Value = @"C:\Program Files\GeoGebra\GeoGebra.exe" },
        });

        Assert.True(matcher.IsAllowed(new AppInfo("GeoGebra", @"C:\Program Files\GeoGebra\GeoGebra.exe", null)));
        Assert.True(matcher.IsAllowed(new AppInfo("GeoGebra", @"c:\program files\geogebra\geogebra.exe", null)));
        Assert.False(matcher.IsAllowed(new AppInfo("GeoGebra", @"C:\Other\GeoGebra.exe", null)));
        Assert.False(matcher.IsAllowed(new AppInfo("GeoGebra", null, null)));
    }

    [Fact]
    public void Publisher_rule_matches_signed_publisher_case_insensitively()
    {
        var matcher = new AllowlistMatcher(new[]
        {
            new AllowedAppRule { MatchKind = AllowedAppMatchKind.Publisher, Value = "International GeoGebra Institute" },
        });

        Assert.True(matcher.IsAllowed(new AppInfo("GeoGebra", null, "International GeoGebra Institute")));
        Assert.True(matcher.IsAllowed(new AppInfo("GeoGebra", null, "international geogebra institute")));
        Assert.False(matcher.IsAllowed(new AppInfo("Fake", null, "Acme Corp")));
        Assert.False(matcher.IsAllowed(new AppInfo("Unsigned", null, null)));
    }

    [Fact]
    public void Empty_value_rules_are_ignored()
    {
        var matcher = new AllowlistMatcher(new[]
        {
            new AllowedAppRule { MatchKind = AllowedAppMatchKind.ProcessName, Value = "" },
            new AllowedAppRule { MatchKind = AllowedAppMatchKind.Publisher, Value = "   " },
        });

        Assert.False(matcher.IsAllowed(new AppInfo("", null, null)));
        Assert.False(matcher.IsAllowed(new AppInfo("notepad", null, null)));
    }

    [Fact]
    public void Own_process_name_is_always_allowed()
    {
        var matcher = new AllowlistMatcher(Array.Empty<AllowedAppRule>(), ownProcessName: "FocusAgent.App");

        Assert.True(matcher.IsAllowed(new AppInfo("FocusAgent.App", @"C:\agent\FocusAgent.App.exe", null)));
        Assert.True(matcher.IsAllowed(new AppInfo("focusagent.app", null, null)));
    }

    [Fact]
    public void Multiple_rule_kinds_compose()
    {
        var matcher = new AllowlistMatcher(new[]
        {
            new AllowedAppRule { MatchKind = AllowedAppMatchKind.ProcessName, Value = "winword" },
            new AllowedAppRule { MatchKind = AllowedAppMatchKind.Publisher, Value = "International GeoGebra Institute" },
        });

        Assert.True(matcher.IsAllowed(new AppInfo("WINWORD", null, "Microsoft Corporation")));
        Assert.True(matcher.IsAllowed(new AppInfo("GeoGebra", null, "International GeoGebra Institute")));
        Assert.False(matcher.IsAllowed(new AppInfo("notepad", null, "Microsoft Corporation")));
    }

    [Fact]
    public void UserRules_exposes_whatever_was_passed_in()
    {
        // Pre-#70 this property excluded the built-in baseline; the matcher
        // no longer has one, so callers see exactly what they gave it. The
        // overlay relies on this to render the allowed-apps list.
        var rules = new[]
        {
            new AllowedAppRule { MatchKind = AllowedAppMatchKind.ProcessName, Value = "winword" },
            new AllowedAppRule { MatchKind = AllowedAppMatchKind.ProcessName, Value = "msedge" },
        };
        var matcher = new AllowlistMatcher(rules);

        Assert.Equal(new[] { "winword", "msedge" }, matcher.UserRules.Select(r => r.Value).ToArray());
    }
}
