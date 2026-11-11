using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Diagnostics;

/// <summary>Where the logs the agent uploads from live (#446).</summary>
/// <param name="LogDirectory">The agent's own logs: the daily <c>focusagent-YYYYMMDD.log</c> files and the crash log.</param>
/// <param name="UpdaterLogPath">Velopack's log for this app.</param>
public sealed record LogUploadSources(string LogDirectory, string UpdaterLogPath)
{
    public const string CrashLogFileName = "startup-error.log";
}

/// <summary>What the uploads did in this agent process, for /status.</summary>
public sealed record LogUploadStatus(int Passes, int Uploaded, DateTimeOffset? LastPassAt, string? LastError);

/// <summary>
/// The test phase's log uploads (#446), run on every connect. Asks the backend
/// which session windows still need an upload from this student, cuts the log
/// lines inside each window out of the agent's own logs, the crash log and the
/// updater log, and uploads them gzipped, with the agent's version. Nothing from
/// outside a window leaves the laptop (see <see cref="AgentLogCutter"/> and
/// <see cref="UpdaterLogCutter"/>). Uploads are off unless the backend turns
/// them on: switched off, it reports no windows, and the agent sends nothing.
///
/// <para>
/// A pass runs in the background and never holds up startup or the
/// connection; it never throws. It uploads every window the backend lists
/// (oldest first: the first to go from the laptop's two weeks of logs), and a
/// window that fails doesn't hold up the ones after it. A window that failed is
/// still pending on the backend, so the next connect (a reconnect or the next
/// start) tries it again. Overlapping triggers wait their turn.
/// </para>
/// </summary>
public sealed class LogUploadService
{
    // Serilog's daily files: focusagent-20261021.log, and focusagent-20261021_001.log
    // when it had to roll to a new file the same day.
    private static readonly Regex AgentLogFileName = new(
        @"^focusagent-(?<date>\d{8})(_\d{3})?\.log$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>How much of the end of the updater and crash logs is read. Both are small.</summary>
    internal const int TailBytes = 1024 * 1024;

    private readonly ILogUploadClient _client;
    private readonly LogUploadSources _sources;
    private readonly string _agentVersion;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _timeZone;
    private readonly ILogger<LogUploadService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusLock = new();
    private LogUploadStatus _status = new(0, 0, null, null);

    public LogUploadService(
        ILogUploadClient client,
        LogUploadSources sources,
        string agentVersion,
        TimeProvider clock,
        ILogger<LogUploadService>? log = null,
        TimeZoneInfo? timeZone = null)
    {
        _client = client;
        _sources = sources;
        _agentVersion = agentVersion;
        _clock = clock;
        _timeZone = timeZone ?? clock.LocalTimeZone;
        _log = log ?? NullLogger<LogUploadService>.Instance;
    }

    public LogUploadStatus Status
    {
        get { lock (_statusLock) return _status; }
    }

    /// <summary>
    /// The App layer's hook for each hub Connected. Returns at once: the pass
    /// runs on the thread pool, so startup and the connection never wait on it.
    /// The returned task completes when the pass is done and never faults.
    /// </summary>
    public Task NotifyConnectedAsync(CancellationToken ct = default) => Task.Run(() => RunPassAsync(ct), CancellationToken.None);

    private async Task RunPassAsync(CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var uploaded = 0;
        string? error = null;
        try
        {
            var pending = await _client.GetPendingAsync(ct).ConfigureAwait(false);
            if (pending.Windows.Count == 0)
            {
                _log.LogDebug("Log upload: no session windows to upload.");
            }
            else
            {
                _log.LogInformation("Log upload: {Count} session window(s) to upload.", pending.Windows.Count);
                var logs = new LocalLogs(_sources, pending.Windows, _clock, _timeZone, _log);
                foreach (var window in pending.Windows)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        if (await UploadAsync(window, logs, pending.MaxBytes, ct).ConfigureAwait(false))
                            uploaded++;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Still pending on the backend: the next connect tries again.
                        error = ex.Message;
                        _log.LogWarning(ex,
                            "Log upload: the window of session {SessionId} failed to upload; retrying on the next connect.",
                            window.SessionId);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.LogWarning(ex, "Log upload: couldn't ask the backend for session windows; retrying on the next connect.");
        }
        finally
        {
            lock (_statusLock)
            {
                _status = new LogUploadStatus(
                    _status.Passes + 1, _status.Uploaded + uploaded, _clock.GetUtcNow(), error);
            }
            _gate.Release();
        }
    }

    private async Task<bool> UploadAsync(LogWindow window, LocalLogs logs, int maxBytes, CancellationToken ct)
    {
        var sections = new List<LogUploadDocument.Section>
        {
            new(LogUploadDocument.AgentLogSection, logs.AgentLog(window)),
            new(LogUploadDocument.CrashLogSection, logs.CrashLog(window)),
            new(LogUploadDocument.UpdaterLogSection, logs.UpdaterLog(window)),
        };
        var gzip = LogUploadDocument.Build(window, _agentVersion, sections, maxBytes);
        if (gzip is null)
        {
            _log.LogWarning("Log upload: the window of session {SessionId} doesn't fit in {MaxBytes} bytes.",
                window.SessionId, maxBytes);
            return false;
        }

        var outcome = await _client.UploadAsync(window, _agentVersion, gzip, ct).ConfigureAwait(false);
        _log.LogInformation(
            "Log upload: session {SessionId}, {Entries} agent log entries, {Bytes} bytes: {Outcome}.",
            window.SessionId, sections[0].Entries.Count, gzip.Length, outcome);
        return outcome == LogUploadOutcome.Stored;
    }

    /// <summary>
    /// The pass's view of the logs on disk, read and parsed once for all its
    /// windows (on the first connect after uploads are switched on, that is
    /// every session of the last two weeks): the agent's daily files that can
    /// hold lines of a window (the day before the earliest window through
    /// today), the end of the crash log and the end of the updater log. Each
    /// window is then only a filter over the parsed entries. Files the agent is
    /// still writing are read shared. A file that can't be read counts as empty.
    /// </summary>
    private sealed class LocalLogs
    {
        private readonly IReadOnlyList<LogEntry> _agentEntries;
        private readonly IReadOnlyList<LogEntry> _crashEntries;
        private readonly IReadOnlyList<string> _updaterLines;
        private readonly IReadOnlyList<AgentProcessStart> _starts;
        private readonly DateTimeOffset _now;
        private readonly TimeZoneInfo _timeZone;

        public LocalLogs(
            LogUploadSources sources,
            IReadOnlyList<LogWindow> windows,
            TimeProvider clock,
            TimeZoneInfo timeZone,
            ILogger log)
        {
            _timeZone = timeZone;
            var firstDay = TimeZoneInfo.ConvertTime(windows.Min(w => w.From), timeZone).Date.AddDays(-1);
            var agentEntries = new List<LogEntry>();
            var starts = new List<AgentProcessStart>();
            foreach (var path in AgentLogFiles(sources.LogDirectory, firstDay))
            {
                var lines = ReadLines(path, tailBytes: null, log);
                // File by file: an entry never runs on from one daily file into the next.
                agentEntries.AddRange(AgentLogCutter.ParseAgentLog(lines));
                starts.AddRange(AgentLogCutter.FindProcessStarts(lines));
            }
            _agentEntries = agentEntries;
            _starts = starts;
            _crashEntries = AgentLogCutter.ParseCrashLog(
                ReadLines(Path.Combine(sources.LogDirectory, LogUploadSources.CrashLogFileName), TailBytes, log));
            _updaterLines = ReadLines(sources.UpdaterLogPath, TailBytes, log);
            // After the reads: every line read was written before it.
            _now = clock.GetUtcNow();
        }

        public IReadOnlyList<string> AgentLog(LogWindow window) => AgentLogCutter.Cut(_agentEntries, window).ToList();

        public IReadOnlyList<string> CrashLog(LogWindow window) => AgentLogCutter.Cut(_crashEntries, window).ToList();

        public IReadOnlyList<string> UpdaterLog(LogWindow window) =>
            UpdaterLogCutter.Cut(_updaterLines, _starts, window, _now, _timeZone);

        private static IEnumerable<string> AgentLogFiles(string directory, DateTime firstDay)
        {
            if (!Directory.Exists(directory)) return [];
            return Directory.EnumerateFiles(directory, "focusagent-*.log")
                .Select(path => (Path: path, Match: AgentLogFileName.Match(Path.GetFileName(path))))
                .Where(f => f.Match.Success
                            && DateTime.TryParseExact(f.Match.Groups["date"].Value, "yyyyMMdd",
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                            && day >= firstDay)
                .Select(f => f.Path)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IReadOnlyList<string> ReadLines(string path, int? tailBytes, ILogger log)
        {
            try
            {
                if (!File.Exists(path)) return [];
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var cutStart = tailBytes is { } tail && stream.Length > tail;
                if (cutStart) stream.Seek(-tailBytes!.Value, SeekOrigin.End);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var lines = new List<string>();
                // Reading from the middle of the file: the first line is a partial one.
                if (cutStart) reader.ReadLine();
                while (reader.ReadLine() is { } line)
                    lines.Add(line);
                return lines;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogDebug(ex, "Log upload: couldn't read {Path}.", path);
                return [];
            }
        }
    }
}
