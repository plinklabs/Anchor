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
}
