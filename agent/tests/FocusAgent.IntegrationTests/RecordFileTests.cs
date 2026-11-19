using System.Text;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// #544: the update specs read the recording Update.exe's record files while it may
/// still be appending to them, and a plain <see cref="File.ReadAllLines(string)"/>
/// then failed with "being used by another process". These pin
/// <see cref="RecordFile.ReadLines"/> against the stand-in's writer. No agent runs.
/// </summary>
public sealed class RecordFileTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"anchor-records-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public void Reads_the_complete_lines_while_the_recorder_is_appending()
    {
        File.AppendAllText(_path, "[\"apply\"]" + Environment.NewLine);

        // Open the file the way the recorder's File.AppendAllText does, and stop
        // part-way through its next line.
        using var writer = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        writer.Write(Encoding.UTF8.GetBytes("[\"--silent\"]" + Environment.NewLine + "[\"--wait"));
        writer.Flush();

        Assert.Equal(["[\"apply\"]", "[\"--silent\"]"], RecordFile.ReadLines(_path));
    }

    [Fact]
    public void Waits_out_a_brief_hold_that_shuts_readers_out()
    {
        File.AppendAllText(_path, "[\"apply\"]" + Environment.NewLine);
        var held = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        // A thread of its own, so a busy thread pool can't keep the file held.
        var release = new Thread(() =>
        {
            Thread.Sleep(100);
            held.Dispose();
        });
        release.Start();

        try
        {
            Assert.Equal(["[\"apply\"]"], RecordFile.ReadLines(_path));
        }
        finally
        {
            release.Join();
        }
    }

    [Fact]
    public void A_file_not_written_yet_has_no_lines() =>
        Assert.Empty(RecordFile.ReadLines(_path));

    public void Dispose()
    {
        try { File.Delete(_path); }
        catch { /* best-effort */ }
    }
}
