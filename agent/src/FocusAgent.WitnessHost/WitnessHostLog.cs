using System.Globalization;
using System.Text;
using FocusAgent.Core.Logging;

namespace FocusAgent.WitnessHost;

/// <summary>
/// The witness host's own small log (#561). Before it, a host that Edge started
/// but that couldn't reach the agent's pipe left nothing behind, and nobody can
/// look at a student's laptop. It writes to the agent's log folder
/// (<c>%LOCALAPPDATA%\Anchor\FocusAgent\logs\</c>), one file a day
/// (<c>witness-host-YYYYMMDD.log</c>, <see cref="KeptFiles"/> of them kept), in
/// the agent log's format, so the agent's log upload (#446) cuts it to the
/// session window as it does its own log and adds it as a section of its own.
///
/// <para>
/// Every Edge profile with the extension runs a host of its own, so several
/// hosts can append to the same file at once: each line is one append, and
/// names the host's process id. A line that can't be written is dropped: the
/// log may never stop the host. stdout is the native-messaging channel, so
/// nothing here goes near it.
/// </para>
/// </summary>
public sealed class WitnessHostLog
{
    /// <summary>Daily files kept: as many as the agent keeps of its own (two weeks), the span the upload reaches back.</summary>
    public const int KeptFiles = 14;

    private readonly string? _directory;
    private readonly TimeProvider _clock;
    private readonly int _processId;

    /// <param name="directory">The folder to log to; null logs nothing.</param>
    public WitnessHostLog(string? directory, TimeProvider? clock = null, int? processId = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? null : directory;
        _clock = clock ?? TimeProvider.System;
        _processId = processId ?? Environment.ProcessId;
    }

    /// <summary>A log that writes nothing.</summary>
    public static WitnessHostLog None { get; } = new(null);

    /// <summary>
    /// The folder the host logs to: <see cref="AgentLogPaths.WitnessHostLogDirectoryEnvVar"/>
    /// when set (tests), otherwise the agent's own log folder.
    /// </summary>
    public static string DefaultDirectory() =>
        Environment.GetEnvironmentVariable(AgentLogPaths.WitnessHostLogDirectoryEnvVar) is { Length: > 0 } folder
            ? folder
            : AgentLogPaths.LocalAppDataLogDirectory();

    public void Info(string message) => Write("INF", message);

    public void Warn(string message) => Write("WRN", message);

    /// <summary>An exception's type and message, on one line: what a log line about a failure says.</summary>
    public static string Describe(Exception ex) => $"{ex.GetType().Name}: {OneLine(ex.Message)}";

    /// <summary>
    /// Deletes the daily files beyond the newest <see cref="KeptFiles"/>. Best-effort:
    /// another host may be deleting the same ones.
    /// </summary>
    public void DeleteOldFiles()
    {
        if (_directory is null) return;
        try
        {
            if (!Directory.Exists(_directory)) return;
            var old = Directory.EnumerateFiles(_directory, "witness-host-*.log")
                .Where(path => IsDailyFile(Path.GetFileName(path)))
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Skip(KeptFiles);
            foreach (var path in old)
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Write(string level, string message)
    {
        if (_directory is null) return;
        var now = _clock.GetLocalNow();
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] WitnessHost[{_processId}]: {OneLine(message)}\n");
        try
        {
            Directory.CreateDirectory(_directory);
            using var stream = new FileStream(
                AgentLogPaths.WitnessHostLogFile(_directory, now.Date),
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Write(Encoding.UTF8.GetBytes(line));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Dropped: the log may never stop the host.
        }
    }

    private static bool IsDailyFile(string name) =>
        name.Length == "witness-host-yyyyMMdd.log".Length
        && DateTime.TryParseExact(name["witness-host-".Length..^".log".Length], "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();
}
