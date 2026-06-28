namespace FocusAgent.Core.Startup;

/// <summary>
/// The identity + registry shape of the per-user "start at login" entry the agent
/// writes under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> (#225).
///
/// The agent ships <em>unpackaged</em> via Velopack, which has no app manifest, so
/// the packaged <c>windows.startupTask</c> mechanism isn't available. The
/// unpackaged-friendly equivalent is the classic per-user <c>Run</c> key: a named
/// value whose data is the command Windows runs at
/// each sign-in. It needs no admin (HKCU, not HKLM) and no MDM — exactly what an
/// unmanaged BYOD box needs.
///
/// Windows reads the <c>Run</c> key as a set of named values; each value's
/// <em>name</em> is an arbitrary label (we use a stable Anchor-specific one so we
/// can find/update/remove our own entry without touching anyone else's) and its
/// <em>data</em> is the command line to run. We point that command at the installed
/// <c>FocusAgent.App.exe</c>.
///
/// The pure shape lives here — separate from the Windows <c>Microsoft.Win32</c>
/// write — so the format/compare logic is unit-testable on any OS and the constants
/// can't drift between the writer, the remover, and the tests.
/// </summary>
public static class StartupRunKey
{
    /// <summary>
    /// The per-user Run key path under <c>HKEY_CURRENT_USER</c>. Writing here needs
    /// no admin rights, which is the whole point on an unmanaged BYOD box.
    /// </summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// The stable value name Anchor's Run entry lives under. Fixed across every
    /// build and machine so the agent can recognise (and overwrite/remove) its own
    /// entry — a Velopack update lands in a new versioned install dir, so the
    /// command's <em>data</em> changes between versions while this name stays put,
    /// letting the update just re-point the existing value instead of leaking a
    /// second one.
    /// </summary>
    public const string ValueName = "AnchorFocusAgent";

    /// <summary>
    /// The command line Windows runs at sign-in for a given installed exe path.
    /// Quoted so a path containing spaces (e.g. under <c>%LocalAppData%</c>) is
    /// passed as a single argument.
    /// </summary>
    public static string CommandFor(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("Exe path must be provided.", nameof(exePath));
        return $"\"{exePath.Trim()}\"";
    }

    /// <summary>
    /// True when <paramref name="command"/> is the Run-command for
    /// <paramref name="exePath"/> already — i.e. re-registering would be a no-op.
    /// Tolerant of surrounding whitespace and of the path being written with or
    /// without surrounding quotes, and case-insensitive (Windows paths are).
    /// </summary>
    public static bool CommandTargets(string? command, string exePath)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var actual = Unquote(command.Trim());
        var expected = Unquote(CommandFor(exePath));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            s = s[1..^1];
        return s.Trim();
    }
}
