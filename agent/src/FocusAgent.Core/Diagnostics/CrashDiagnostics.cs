using System.Text;

namespace FocusAgent.Core.Diagnostics;

/// <summary>
/// Builds the human-readable text shown by the agent's last-resort crash dialog
/// (#248). This is deliberately pure string assembly with no WinUI / DI / config /
/// logging dependency — those are exactly the things that may have failed when a
/// fatal-error path runs — so the surfacing layer can call it from anywhere,
/// including a startup crash that fired before any host or window existed.
///
/// Two blocks, matching the two parts of the dialog:
/// <list type="bullet">
///   <item><see cref="BuildDetail"/> — the curated, friendly-but-useful default
///   field: the exception type + message, the inner-exception <em>message</em>
///   chain (messages only, so it stays readable), the agent version, and the path
///   to <c>startup-error.log</c> so the user can attach the full log if asked.</item>
///   <item><see cref="BuildFullStack"/> — the complete exception text (stack
///   frames and all) for the collapsed "show technical details" expander.</item>
/// </list>
/// </summary>
public static class CrashDiagnostics
{
    /// <summary>
    /// The curated default detail: agent version, the exception type + message and
    /// its inner-exception message chain, then the log-file path. No stack frames —
    /// those live behind the expander via <see cref="BuildFullStack"/>.
    /// </summary>
    public static string BuildDetail(Exception ex, string? agentVersion, string? logFilePath)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var sb = new StringBuilder();
        sb.Append("Anchor agent");
        if (!string.IsNullOrWhiteSpace(agentVersion))
            sb.Append(" v").Append(agentVersion.Trim());
        sb.AppendLine();
        sb.AppendLine();

        // Walk the inner-exception chain, indenting each level so the cause chain
        // reads top-down. Messages only — a multi-line message is flattened to one
        // line so a stray newline can't masquerade as a separate cause.
        var current = ex;
        var indent = string.Empty;
        while (current is not null)
        {
            sb.Append(indent)
              .Append(current.GetType().FullName)
              .Append(": ")
              .AppendLine(SingleLine(current.Message));
            indent += "  -> ";
            current = current.InnerException;
        }

        sb.AppendLine();
        sb.Append("Log file: ").Append(
            string.IsNullOrWhiteSpace(logFilePath) ? "(unavailable)" : logFilePath);

        return sb.ToString();
    }

    /// <summary>The full exception text — type, message and stack frames at every level.</summary>
    public static string BuildFullStack(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.ToString();
    }

    private static string SingleLine(string? message) =>
        string.IsNullOrEmpty(message)
            ? "(no message)"
            : message.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
}
