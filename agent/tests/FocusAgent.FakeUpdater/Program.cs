using System.Diagnostics;
using System.Text.Json;

// The update e2e's stand-in for Velopack's Update.exe (see the .csproj). Appends the
// arguments it was started with, as one JSON array per line, to
// update-invocations.jsonl beside itself. Then, like the real updater, it waits for
// the --waitPid process to exit, for at most the real updater's 60 s, and appends
// whether that process exited on its own (#456) to update-waits.jsonl. Where the
// real updater would kill the process after 60 s, this one only records it. It
// applies nothing.
AppendLine("update-invocations.jsonl", JsonSerializer.Serialize(args));

var waitPid = ValueAfter(args, "--waitPid");
if (waitPid is not null && int.TryParse(waitPid, out var pid))
{
    var waited = Stopwatch.StartNew();
    var exitedOnItsOwn = WaitForExit(pid, TimeSpan.FromSeconds(60));
    AppendLine("update-waits.jsonl", JsonSerializer.Serialize(new
    {
        waitPid = pid,
        exitedOnItsOwn,
        waitedMs = (long)waited.Elapsed.TotalMilliseconds,
    }));
}
return 0;

static bool WaitForExit(int pid, TimeSpan timeout)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        return process.WaitForExit(timeout);
    }
    catch (ArgumentException)
    {
        // Already gone.
        return true;
    }
}

static string? ValueAfter(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}

static void AppendLine(string fileName, string line)
{
    var path = Path.Combine(AppContext.BaseDirectory, fileName);
    // Two launches could race on the file; the loser retries briefly.
    for (var attempt = 0; ; attempt++)
    {
        try
        {
            File.AppendAllText(path, line + Environment.NewLine);
            return;
        }
        catch (IOException) when (attempt < 40)
        {
            Thread.Sleep(50);
        }
    }
}
