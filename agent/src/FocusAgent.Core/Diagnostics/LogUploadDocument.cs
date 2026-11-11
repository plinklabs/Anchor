using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace FocusAgent.Core.Diagnostics;

/// <summary>
/// The text the agent uploads for one session window (#446), gzipped: a short
/// header (agent version, session, window) and the entries cut from each log,
/// one section per log. Nothing else goes in: no time of the upload, no line
/// from outside the window.
/// </summary>
public static class LogUploadDocument
{
    public const string AgentLogSection = "Agent log";
    public const string CrashLogSection = "Crash log";
    public const string UpdaterLogSection = "Updater log";

    /// <summary>One log's entries inside the window, oldest first.</summary>
    public sealed record Section(string Title, IReadOnlyList<string> Entries);

    /// <summary>
    /// The gzipped document, at most <paramref name="maxBytes"/> long. When the
    /// whole of it would be larger, entries are left out of the middle of each
    /// section (the start of the window and its end are what explain most), in
    /// rounds, with a line saying how many, until it fits; the header always
    /// goes. Null when not even the header fits.
    /// </summary>
    public static byte[]? Build(
        LogWindow window, string agentVersion, IReadOnlyList<Section> sections, int maxBytes)
    {
        for (var keep = 1.0; ; keep /= 2)
        {
            var gzip = Gzip(Text(window, agentVersion, sections, keep));
            if (gzip.Length <= maxBytes) return gzip;
            if (sections.All(s => Kept(s.Entries.Count, keep) == 0)) return null;
            if (keep < 1e-9) keep = 0;
        }
    }

    /// <summary>The document's text, every entry kept.</summary>
    public static string Text(LogWindow window, string agentVersion, IReadOnlyList<Section> sections) =>
        Text(window, agentVersion, sections, keep: 1.0);

    private static string Text(LogWindow window, string agentVersion, IReadOnlyList<Section> sections, double keep)
    {
        var text = new StringBuilder();
        text.Append("Anchor agent log upload (#446)\n");
        text.Append("Agent version: ").Append(agentVersion).Append('\n');
        text.Append("Session: ").Append(window.SessionId.ToString()).Append('\n');
        text.Append("Window: ")
            .Append(window.From.ToString("O", CultureInfo.InvariantCulture))
            .Append(" to ")
            .Append(window.To.ToString("O", CultureInfo.InvariantCulture))
            .Append('\n');
        foreach (var section in sections)
        {
            text.Append("\n== ").Append(section.Title).Append(" ==\n");
            var count = section.Entries.Count;
            var kept = Kept(count, keep);
            var head = (kept + 1) / 2;
            var tail = kept - head;
            for (var i = 0; i < head; i++)
                text.Append(section.Entries[i]).Append('\n');
            if (kept < count)
                text.Append("[... ").Append(count - kept).Append(" entries left out to fit the upload limit ...]\n");
            for (var i = count - tail; i < count; i++)
                text.Append(section.Entries[i]).Append('\n');
        }
        return text.ToString();
    }

    private static int Kept(int count, double keep) => keep >= 1.0 ? count : (int)Math.Floor(count * keep);

    private static byte[] Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }
        return buffer.ToArray();
    }
}
