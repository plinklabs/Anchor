namespace FocusAgent.IntegrationTests;

/// <summary>
/// Reads a JSON-lines record file that a stand-in the e2e starts (the recording
/// Update.exe, FocusAgent.FakeUpdater) may still be appending to (#544).
///
/// <para>
/// The stand-in appends with <see cref="File.AppendAllText(string, string)"/>, which
/// holds the file for writing and lets others only read it.
/// <see cref="File.ReadAllLines(string)"/> also lets others only read, so a read that
/// overlaps an append fails with "being used by another process". This read lets the
/// writer in, retries briefly while anything else shuts readers out, and leaves out
/// a last line whose newline isn't written yet.
/// </para>
/// </summary>
internal static class RecordFile
{
    // As long as the stand-in's own append retries (40 x 50 ms).
    private static readonly TimeSpan RetryFor = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The file's complete, non-empty lines so far; none when it isn't there yet.
    /// </summary>
    public static IReadOnlyList<string> ReadLines(string path)
    {
        var deadline = DateTime.UtcNow + RetryFor;
        while (true)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return CompleteLines(reader.ReadToEnd());
            }
            catch (FileNotFoundException)
            {
                return [];
            }
            catch (DirectoryNotFoundException)
            {
                return [];
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(RetryEvery);
            }
        }
    }

    // A line is complete once its newline is written; whatever follows the last
    // newline is an append still under way.
    private static List<string> CompleteLines(string text)
    {
        var end = text.LastIndexOf('\n');
        if (end < 0) return [];
        return text[..end]
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToList();
    }
}
