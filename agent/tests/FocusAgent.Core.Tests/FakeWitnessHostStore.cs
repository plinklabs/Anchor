using FocusAgent.Core.Tamper;
using Microsoft.Extensions.Logging;

namespace FocusAgent.Core.Tests;

/// <summary>
/// The witness host registration in memory (#288): the HKCU key's <c>(default)</c>
/// value, the files written, and the exes that exist (#561).
/// </summary>
internal sealed class FakeWitnessHostStore : IWitnessHostStore
{
    public string? ManifestPath { get; set; }
    public int KeyWriteCount { get; set; }
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int FileWriteCount { get; set; }

    /// <summary>Files that exist but aren't text the store reads: the host exes.</summary>
    public HashSet<string> Exes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files that exist but can't be read.</summary>
    public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? GetRegisteredManifestPath() => ManifestPath;

    public void SetRegisteredManifestPath(string manifestPath)
    {
        ManifestPath = manifestPath;
        KeyWriteCount++;
    }

    public void RemoveRegistration() => ManifestPath = null;

    public string? ReadFile(string path) => Files.TryGetValue(path, out var c) && !Unreadable.Contains(path) ? c : null;

    public void WriteFile(string path, string content)
    {
        Files[path] = content;
        FileWriteCount++;
    }

    public bool FileExists(string path) => Files.ContainsKey(path) || Exes.Contains(path) || Unreadable.Contains(path);
}

/// <summary>One line a <see cref="RecordingLogger{T}"/> kept.</summary>
internal sealed record RecordedLog(LogLevel Level, string Message, Exception? Exception);

/// <summary>Keeps every log line, formatted, for a test to look at.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<RecordedLog> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries) Entries.Add(new RecordedLog(logLevel, formatter(state, exception), exception));
    }

    /// <summary>Every line, one per entry, for an assertion's message.</summary>
    public string Text()
    {
        lock (Entries) return string.Join(Environment.NewLine, Entries.Select(e => $"[{e.Level}] {e.Message}"));
    }
}
