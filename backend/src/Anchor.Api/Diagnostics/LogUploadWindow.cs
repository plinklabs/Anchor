namespace Anchor.Api.Diagnostics;

/// <summary>
/// The window around a session that an agent may upload its log lines from
/// (#446): from <see cref="LogUploadOptions.WindowBefore"/> before the session
/// started to <see cref="LogUploadOptions.WindowAfter"/> after it ended. Nothing
/// outside it may leave the laptop.
/// <para>
/// While the window is open (the session still running, or ended less than
/// <see cref="LogUploadOptions.WindowAfter"/> ago), <see cref="To"/> is now:
/// nothing after now exists yet, and whenever the session ends, its window ends
/// later than that. Once the window has closed, <see cref="To"/> is its end and
/// <see cref="IsClosed"/> is true; an upload cut to that end is complete.
/// </para>
/// </summary>
public readonly record struct LogUploadWindow(DateTimeOffset From, DateTimeOffset To, bool IsClosed)
{
    public static LogUploadWindow For(
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt,
        DateTimeOffset now,
        LogUploadOptions options)
    {
        var from = startedAt - options.WindowBefore;
        if (endedAt is { } ended && now >= ended + options.WindowAfter)
            return new LogUploadWindow(from, ended + options.WindowAfter, IsClosed: true);
        return new LogUploadWindow(from, now < from ? from : now, IsClosed: false);
    }
}
