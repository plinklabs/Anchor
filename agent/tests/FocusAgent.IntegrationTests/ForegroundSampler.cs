using System.Diagnostics;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Every window that got the foreground while it runs, for failure messages:
/// where the foreground went after a block or a pause, and when (#441, #450).
/// Listens on the same foreground event the agent does, so a window that has it
/// only briefly shows too.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ForegroundSampler : IDisposable
{
    private readonly List<string> _changes = new();
    private readonly Dictionary<IntPtr, string> _names = new();
    private readonly Thread _thread;
    private readonly int _agentPid;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;

    public ForegroundSampler(int agentPid)
    {
        _agentPid = agentPid;
        _thread = new Thread(Run) { IsBackground = true, Name = "foreground-sampler" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
        Record(WindowCapture.ForegroundWindow(), "now");
    }

    /// <summary>Call <paramref name="hwnd"/> <paramref name="name"/> in the description.</summary>
    public void Name(IntPtr hwnd, string name)
    {
        lock (_changes) _names[hwnd] = name;
    }

    private void Run()
    {
        _threadId = WindowCapture.CurrentThreadId();
        using var hook = WindowCapture.HookForeground((hwnd, eventName) => Record(hwnd, eventName));
        _ready.Set();
        WindowCapture.PumpMessagesUntilQuit();
    }

    private void Record(IntPtr hwnd, string eventName)
    {
        string? name;
        lock (_changes) _names.TryGetValue(hwnd, out name);
        var who = name is not null ? $"{name}{(WindowCapture.IsIconic(hwnd) ? " (minimized)" : "")}"
            : WindowCapture.ProcessIdOf(hwnd) == _agentPid ? $"agent [{WindowCapture.GetClassName(hwnd)}]"
            : $"{WindowCapture.GetProcessName(hwnd)} [{WindowCapture.GetClassName(hwnd)}]";
        lock (_changes) _changes.Add($"{eventName} {who} @{_clock.ElapsedMilliseconds}ms");
    }

    /// <summary>Note where a step of the spec begins.</summary>
    public void Mark(string step)
    {
        lock (_changes) _changes.Add($"[{step}] @{_clock.ElapsedMilliseconds}ms");
    }

    public string Describe()
    {
        lock (_changes)
            return string.Join(" -> ", _changes);
    }

    public void Dispose()
    {
        WindowCapture.PostQuit(_threadId);
        _thread.Join(TimeSpan.FromSeconds(1));
        _ready.Dispose();
    }
}
