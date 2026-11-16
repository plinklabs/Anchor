using System.Diagnostics;

namespace FocusAgent.App.Extension;

/// <summary>
/// Whether the browser the extension lives in is running for this student (#488):
/// a process by that name in the agent's own Windows session. Another user's Edge
/// on the same laptop can't load this student's extension, so it doesn't count.
/// </summary>
internal static class BrowserProcess
{
    /// <param name="processName">The browser as a process name (<c>msedge</c>, with or without <c>.exe</c>).</param>
    public static bool IsRunningInThisSession(string processName)
    {
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        // No browser configured: can't tell, so count it as running and let the
        // grace period run as before.
        if (name.Length == 0) return true;

        int session;
        using (var self = Process.GetCurrentProcess())
            session = self.SessionId;

        var matches = Process.GetProcessesByName(name);
        try
        {
            return matches.Any(p => InSession(p, session));
        }
        finally
        {
            foreach (var p in matches) p.Dispose();
        }
    }

    private static bool InSession(Process process, int session)
    {
        try
        {
            return process.SessionId == session;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Exited in the meantime.
            return false;
        }
    }
}
