using System.Text.Json;

// The #449 e2e's stand-in for Velopack's Update.exe (see the .csproj). Appends the
// arguments it was started with, as one JSON array per line, to
// update-invocations.jsonl beside itself, then exits without applying anything.
var log = Path.Combine(AppContext.BaseDirectory, "update-invocations.jsonl");
var line = JsonSerializer.Serialize(args) + Environment.NewLine;

// Two launches could race on the file; the loser retries briefly.
for (var attempt = 0; ; attempt++)
{
    try
    {
        File.AppendAllText(log, line);
        return 0;
    }
    catch (IOException) when (attempt < 40)
    {
        Thread.Sleep(50);
    }
}
