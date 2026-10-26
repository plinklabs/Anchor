using System.IO.Compression;
using System.Text;
using FocusAgent.Core.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace FocusAgent.Core.Tests;

/// <summary>
/// #446: on every connect the agent asks the backend which session windows still
/// need an upload, cuts its logs to each window and uploads them gzipped, with
/// its version. Nothing from outside a window goes; with uploads switched off
/// (no windows) nothing goes at all; a failed upload is tried again on the next
/// connect; and a pass never holds up the caller (startup, the connection).
/// Runs against real files in a folder of its own.
/// </summary>
public sealed class LogUploadServiceTests : IDisposable
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(2);
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("Anchor test +02", Offset, "Anchor test +02", "Anchor test +02");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anchor-log-upload-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, Offset));
    private readonly FakeClient _client = new();

    // A lesson on 2026-10-21 from 10:00 to 11:00: its window is 09:30-11:10.
    private readonly LogWindow _window = new(
        Guid.NewGuid(),
        new DateTimeOffset(2026, 10, 21, 9, 30, 0, Offset),
        new DateTimeOffset(2026, 10, 21, 11, 10, 0, Offset));

    public LogUploadServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _clock.SetLocalTimeZone(Zone);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string UpdaterLog => Path.Combine(_dir, "velopack_Anchor.Agent.log");

    private LogUploadService Service() =>
        new(_client, new LogUploadSources(_dir, UpdaterLog), "0.9.3", _clock, timeZone: Zone);

    private void Write(string name, params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, name), string.Join("\r\n", lines) + "\r\n");

    [Fact]
    public async Task Uploads_exactly_the_windows_lines_from_every_log_and_nothing_from_outside_it()
    {
        Write("focusagent-20261014.log",
            "2026-10-14 10:30:00.000 +02:00 [INF] Old.File: a week ago, same hours");
        Write("focusagent-20261020.log",
            "2026-10-20 21:00:00.000 +02:00 [INF] Yesterday: at home");
        Write("focusagent-20261021.log",
            "2026-10-21 07:10:00.000 +02:00 [INF] Today: laptop on at home",
            "2026-10-21 09:40:00.000 +02:00 [INF] FocusAgent.App.App: FocusAgent 0.9.3 starting (unpackaged WinUI 3), process 1234 started at 2026-10-21T09:39:59.5000000+02:00",
            "2026-10-21 10:15:00.000 +02:00 [ERR] Inside: failed",
            "System.Exception: boom",
            "   at Inside.Frame()",
            "2026-10-21 11:30:00.000 +02:00 [INF] After: after class");
        Write("startup-error.log",
            "2026-10-21T08:00:00.0000000+02:00",
            "System.Exception: crashed before class",
            "",
            "2026-10-21T10:45:00.0000000+02:00",
            "System.Exception: crashed in class",
            "");
        Write("velopack_Anchor.Agent.log",
            "[update:500] [20:00:00] [INFO] last night",
            "[lib-csharp:1234] [09:40:00] [Information] Initializing WindowsVelopackLocator",
            "[update:600] [10:20:00] [INFO] in class",
            "[update:600] [11:50:00] [INFO] after class");
        _client.Pending = new PendingLogWindows(1024 * 1024, [_window]);

        await Service().NotifyConnectedAsync();

        var upload = Assert.Single(_client.Uploads);
        Assert.Equal(_window, upload.Window);
        Assert.Equal("0.9.3", upload.AgentVersion);
        Assert.Equal(
            $"""
            Anchor agent log upload (#446)
            Agent version: 0.9.3
            Session: {_window.SessionId}
            Window: 2026-10-21T09:30:00.0000000+02:00 to 2026-10-21T11:10:00.0000000+02:00

            == Agent log ==
            2026-10-21 09:40:00.000 +02:00 [INF] FocusAgent.App.App: FocusAgent 0.9.3 starting (unpackaged WinUI 3), process 1234 started at 2026-10-21T09:39:59.5000000+02:00
            2026-10-21 10:15:00.000 +02:00 [ERR] Inside: failed
            System.Exception: boom
               at Inside.Frame()

            == Crash log ==
            2026-10-21T10:45:00.0000000+02:00
            System.Exception: crashed in class

            == Updater log ==
            [lib-csharp:1234] [09:40:00] [Information] Initializing WindowsVelopackLocator
            [update:600] [10:20:00] [INFO] in class

            """.ReplaceLineEndings("\n"),
            Gunzip(upload.Gzip));
    }

    [Fact]
    public async Task With_no_windows_pending_nothing_is_read_or_uploaded()
    {
        Write("focusagent-20261021.log", "2026-10-21 10:15:00.000 +02:00 [INF] Inside: a lesson");
        _client.Pending = new PendingLogWindows(1024 * 1024, []);
        var service = Service();

        await service.NotifyConnectedAsync();

        Assert.Empty(_client.Uploads);
        Assert.Equal(new LogUploadStatus(1, 0, _clock.GetUtcNow(), null), service.Status);
    }

    [Fact]
    public async Task A_failed_upload_is_tried_again_on_the_next_connect()
    {
        Write("focusagent-20261021.log", "2026-10-21 10:15:00.000 +02:00 [INF] Inside: a lesson");
        _client.Pending = new PendingLogWindows(1024 * 1024, [_window]);
        _client.FailUploads = true;
        var service = Service();

        await service.NotifyConnectedAsync();
        Assert.Empty(_client.Uploads);
        Assert.Equal(1, service.Status.Passes);
        Assert.Equal(0, service.Status.Uploaded);
        Assert.NotNull(service.Status.LastError);

        _client.FailUploads = false;
        await service.NotifyConnectedAsync();
        Assert.Single(_client.Uploads);
        Assert.Equal(new LogUploadStatus(2, 1, _clock.GetUtcNow(), null), service.Status);
    }

    [Fact]
    public async Task A_backend_that_cant_be_reached_is_asked_again_on_the_next_connect()
    {
        _client.FailPending = true;
        var service = Service();

        await service.NotifyConnectedAsync();
        Assert.NotNull(service.Status.LastError);

        _client.FailPending = false;
        _client.Pending = new PendingLogWindows(1024 * 1024, [_window]);
        await service.NotifyConnectedAsync();
        Assert.Single(_client.Uploads);
    }

    [Fact]
    public async Task A_pass_never_holds_up_the_caller()
    {
        // The backend hangs (and the client even blocks its thread): the connect
        // that triggered the pass carries on regardless.
        using var release = new ManualResetEventSlim();
        _client.BlockPending = release;
        var service = Service();

        var pass = service.NotifyConnectedAsync();

        Assert.False(pass.IsCompleted);
        release.Set();
        await pass.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, service.Status.Passes);
    }

    [Fact]
    public void A_window_over_the_size_limit_leaves_entries_out_of_its_middle_to_fit()
    {
        var random = new Random(446);
        var entries = Enumerable.Range(0, 400)
            .Select(i => $"2026-10-21 10:{i / 60 % 60:00}:{i % 60:00}.000 +02:00 [INF] Entry {i}: "
                         + Convert.ToHexString(Enumerable.Range(0, 64).Select(_ => (byte)random.Next(256)).ToArray()))
            .ToList();
        var sections = new[] { new LogUploadDocument.Section(LogUploadDocument.AgentLogSection, entries) };
        var whole = LogUploadDocument.Build(_window, "0.9.3", sections, int.MaxValue)!;
        var max = whole.Length / 3;

        var fitted = LogUploadDocument.Build(_window, "0.9.3", sections, max);

        Assert.NotNull(fitted);
        Assert.True(fitted!.Length <= max, $"{fitted.Length} > {max}");
        var text = Gunzip(fitted);
        Assert.Contains(entries[0], text);
        Assert.Contains(entries[^1], text);
        Assert.Contains("entries left out to fit the upload limit", text);
        Assert.DoesNotContain(entries[200], text);
        Assert.Null(LogUploadDocument.Build(_window, "0.9.3", sections, maxBytes: 10));
    }

    private static string Gunzip(byte[] gzip)
    {
        using var input = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
        using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed record Upload(LogWindow Window, string AgentVersion, byte[] Gzip);

    private sealed class FakeClient : ILogUploadClient
    {
        public PendingLogWindows Pending { get; set; } = new(1024 * 1024, []);
        public bool FailPending { get; set; }
        public bool FailUploads { get; set; }
        public ManualResetEventSlim? BlockPending { get; set; }
        public List<Upload> Uploads { get; } = new();

        public Task<PendingLogWindows> GetPendingAsync(CancellationToken ct = default)
        {
            BlockPending?.Wait(TimeSpan.FromSeconds(10));
            if (FailPending) throw new HttpRequestException("backend unreachable");
            return Task.FromResult(Pending);
        }

        public Task<LogUploadOutcome> UploadAsync(LogWindow window, string agentVersion, byte[] gzip, CancellationToken ct = default)
        {
            if (FailUploads) throw new HttpRequestException("connection dropped");
            lock (Uploads) Uploads.Add(new Upload(window, agentVersion, gzip));
            return Task.FromResult(LogUploadOutcome.Stored);
        }
    }
}
