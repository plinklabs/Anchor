using System.Text;
using System.Text.RegularExpressions;

namespace FocusAgent.Core.Logging;

/// <summary>
/// A path as a log line may carry it (#561). The agent's logs leave the laptop
/// in the log upload (#446), and a path under the student's own folders carries
/// their Windows user name, which the upload has no use for. So the part of the
/// path that names it is written as the variable it comes from:
/// <c>C:\Users\jan.peeters\AppData\Local\Anchor\…</c> becomes
/// <c>%LOCALAPPDATA%\Anchor\…</c>. Everything after it (the folder Anchor's files
/// are in, which says what is wrong) stays as it is.
/// </summary>
public static class LogPath
{
    private static readonly (string Name, string Folder)[] Roots =
    [
        // Longest first: %LOCALAPPDATA% and %APPDATA% are under %USERPROFILE%.
        ("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        ("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
        ("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
    ];

    private static readonly IReadOnlyList<(string Name, Regex Pattern)> RootPatterns = Patterns(Roots);

    /// <summary><paramref name="path"/> with the student's own folder written as its variable.</summary>
    public static string Redact(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path ?? "";
        foreach (var (name, folder) in Roots)
        {
            var root = folder.TrimEnd('\\', '/');
            if (root.Length == 0) continue;
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && (path.Length == root.Length || path[root.Length] is '\\' or '/'))
            {
                return name + path[root.Length..];
            }
        }
        return path;
    }

    /// <summary>
    /// <paramref name="text"/> with the student's own folders written as their
    /// variables wherever they are in it (#596), for the log upload as a whole:
    /// an exception's message or a line of Velopack's log names them too, not
    /// only the paths a log line passes through <see cref="Redact"/>.
    /// </summary>
    public static string RedactText(string text) => RedactText(text, RootPatterns);

    /// <summary><see cref="RedactText(string)"/> for the given roots.</summary>
    internal static string RedactText(string text, IReadOnlyList<(string Name, string Folder)> roots) =>
        RedactText(text, Patterns(roots));

    private static string RedactText(string text, IReadOnlyList<(string Name, Regex Pattern)> roots)
    {
        foreach (var (name, pattern) in roots)
            text = pattern.Replace(text, name);
        return text;
    }

    /// <summary>
    /// A root wherever it stands: in any case, and with each of its separators
    /// written as <c>\</c>, <c>/</c> or <c>\\</c> (Velopack's log quotes paths
    /// with doubled ones). It ends where its folder name ends: a folder that
    /// only starts with the same letters (<c>C:\Users\jan</c> in
    /// <c>C:\Users\janssens</c> or <c>C:\Users\jan.peeters</c>) is someone else's.
    /// </summary>
    private static IReadOnlyList<(string Name, Regex Pattern)> Patterns(IReadOnlyList<(string Name, string Folder)> roots)
    {
        var patterns = new List<(string, Regex)>();
        // Longest first: %LOCALAPPDATA% and %APPDATA% are under %USERPROFILE%.
        foreach (var (name, root) in roots
                     .Select(r => (r.Name, Root: r.Folder.TrimEnd('\\', '/')))
                     .Where(r => r.Root.Length > 0)
                     .OrderByDescending(r => r.Root.Length))
        {
            var pattern = new StringBuilder();
            for (var i = 0; i < root.Length; i++)
            {
                if (root[i] is '\\' or '/')
                {
                    pattern.Append(@"[\\/]+");
                    while (i + 1 < root.Length && root[i + 1] is '\\' or '/') i++;
                }
                else
                {
                    pattern.Append(Regex.Escape(root[i].ToString()));
                }
            }
            // Not followed by more of a folder name: a letter, digit, _ or -, or a
            // dot with one of those after it (a full stop ends the path).
            pattern.Append(@"(?![\w-]|\.[\w-])");
            patterns.Add((name, new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));
        }
        return patterns;
    }
}
