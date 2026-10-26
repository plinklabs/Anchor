using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FocusAgent.Core.Startup;
using FocusAgent.Native;

namespace FocusAgent.Native.Tests;

/// <summary>
/// #461: <see cref="KeyHolderProcesses"/> against real processes. The case that
/// matters, a process that was ended but that Windows is still tearing down, is
/// made to last by attaching as a debugger: a debuggee's exit waits on the
/// debugger's say-so, while its exit code is already set.
/// </summary>
[SupportedOSPlatform("windows")]
public class KeyHolderProcessesTests
{
    private readonly KeyHolderProcesses _processes = new();

    [Fact]
    public void A_running_process_is_running_and_not_waited_out()
    {
        using var child = StartLongRunningChild();
        try
        {
            Assert.Equal(KeyHolderState.Running, _processes.StateOf((uint)child.Id));
            Assert.False(_processes.WaitUntilGone((uint)child.Id, TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public void This_process_is_running()
    {
        Assert.Equal(KeyHolderState.Running, _processes.StateOf((uint)Environment.ProcessId));
    }

    [Fact]
    public void A_process_that_has_exited_is_gone()
    {
        using var child = StartLongRunningChild();
        child.Kill();
        child.WaitForExit();

        // Its process object still exists (this test holds a handle to it), but every
        // thread of it has gone.
        Assert.Equal(KeyHolderState.Gone, _processes.StateOf((uint)child.Id));
        Assert.True(_processes.WaitUntilGone((uint)child.Id, TimeSpan.Zero));
    }

    [Fact]
    public void A_process_id_that_names_no_process_is_gone()
    {
        const uint NoSuchProcess = 0x7FFFFFF0;
        Assert.Equal(KeyHolderState.Gone, _processes.StateOf(NoSuchProcess));
        Assert.True(_processes.WaitUntilGone(NoSuchProcess, TimeSpan.Zero));
    }

    [Fact]
    public void An_ended_process_that_Windows_is_still_tearing_down_is_exiting_until_it_is_gone()
    {
        using var child = StartLongRunningChild();
        var pid = (uint)child.Id;
        using var teardown = HeldTeardown.Attach(pid);

        teardown.HoldExit();
        child.Kill();
        Assert.True(teardown.WaitUntilHeld(TimeSpan.FromSeconds(10)), "The child's exit never reached the debugger.");

        // Exit code set, process not gone: the state a just-killed agent is in while it
        // still holds the single-instance key.
        Assert.Equal(KeyHolderState.Exiting, _processes.StateOf(pid));
        Assert.False(_processes.WaitUntilGone(pid, TimeSpan.FromMilliseconds(200)));

        teardown.Release();
        Assert.True(_processes.WaitUntilGone(pid, TimeSpan.FromSeconds(10)), "The child was not gone after its exit was let through.");
        Assert.Equal(KeyHolderState.Gone, _processes.StateOf(pid));
    }

    private static Process StartLongRunningChild()
    {
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        var psi = new ProcessStartInfo(ping, "-n 120 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start ping.");
    }

    /// <summary>
    /// Debugs a process on a thread of its own (debug events go to the thread that
    /// attached), letting every event through, until <see cref="HoldExit"/>: then the
    /// first exit event is held until <see cref="Release"/>, which keeps the whole
    /// teardown waiting.
    /// </summary>
    private sealed class HeldTeardown : IDisposable
    {
        private const uint ExceptionDebugEvent = 1;
        private const uint CreateProcessDebugEvent = 3;
        private const uint ExitThreadDebugEvent = 4;
        private const uint ExitProcessDebugEvent = 5;
        private const uint LoadDllDebugEvent = 6;
        private const uint ExceptionBreakpoint = 0x80000003;
        private const uint DbgContinue = 0x00010002;
        private const uint DbgExceptionNotHandled = 0x80010001;

        private readonly uint _pid;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _attached = new();
        private readonly ManualResetEventSlim _held = new();
        private readonly ManualResetEventSlim _release = new();
        private volatile bool _holdExit;
        private volatile bool _stop;
        private Exception? _attachError;

        private HeldTeardown(uint pid)
        {
            _pid = pid;
            _thread = new Thread(Loop) { IsBackground = true, Name = "HeldTeardown debugger" };
        }

        public static HeldTeardown Attach(uint pid)
        {
            var teardown = new HeldTeardown(pid);
            teardown._thread.Start();
            if (!teardown._attached.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Attaching the debugger timed out.");
            if (teardown._attachError is { } error) throw error;
            return teardown;
        }

        public void HoldExit() => _holdExit = true;

        public bool WaitUntilHeld(TimeSpan timeout) => _held.Wait(timeout);

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _stop = true;
            _thread.Join(TimeSpan.FromSeconds(10));
        }

        private void Loop()
        {
            if (!DebugActiveProcess(_pid))
            {
                _attachError = new InvalidOperationException($"DebugActiveProcess failed: {Marshal.GetLastWin32Error()}");
                _attached.Set();
                return;
            }

            var buffer = Marshal.AllocHGlobal(512);
            try
            {
                while (!_stop)
                {
                    if (!WaitForDebugEvent(buffer, 100)) continue;
                    var code = (uint)Marshal.ReadInt32(buffer, 0);
                    var pid = (uint)Marshal.ReadInt32(buffer, 4);
                    var tid = (uint)Marshal.ReadInt32(buffer, 8);
                    // The event-specific union starts at offset 16 on x64 (8 on x86).
                    var union = IntPtr.Size == 8 ? 16 : 12;
                    var status = DbgContinue;

                    switch (code)
                    {
                        case ExceptionDebugEvent:
                            // The attach breakpoint: the debuggee is now fully attached.
                            if ((uint)Marshal.ReadInt32(buffer, union) == ExceptionBreakpoint)
                                _attached.Set();
                            else
                                status = DbgExceptionNotHandled;
                            break;
                        case CreateProcessDebugEvent:
                        case LoadDllDebugEvent:
                            CloseHandle(Marshal.ReadIntPtr(buffer, union));
                            break;
                        case ExitThreadDebugEvent:
                        case ExitProcessDebugEvent:
                            if (_holdExit && !_held.IsSet)
                            {
                                _held.Set();
                                _release.Wait();
                            }
                            break;
                    }

                    ContinueDebugEvent(pid, tid, status);
                    if (code == ExitProcessDebugEvent) break;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DebugActiveProcess(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WaitForDebugEvent(IntPtr lpDebugEvent, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ContinueDebugEvent(uint dwProcessId, uint dwThreadId, uint dwContinueStatus);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
