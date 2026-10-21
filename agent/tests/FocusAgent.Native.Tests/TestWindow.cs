using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FocusAgent.Native.Tests;

/// <summary>
/// A top-level window on a thread of its own with a message pump, standing in for
/// another app's window. <see cref="Freeze"/> stops the pump until <see cref="Thaw"/>,
/// as an app that hangs. Shown without activating, so a test never takes the
/// foreground from whoever works at the desktop.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TestWindow : IDisposable
{
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_APP_FREEZE = 0x8001;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int SW_SHOWNOACTIVATE = 4;
    // A frozen window thaws by itself after this, so a test that never thaws it
    // (because the code under test waited for it) still ends.
    private static readonly TimeSpan MaxFreeze = TimeSpan.FromSeconds(10);

    private readonly string _title;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _created = new();
    private readonly ManualResetEventSlim _frozen = new();
    private readonly ManualResetEventSlim _thaw = new();
    private uint _threadId;

    private TestWindow(string title)
    {
        _title = title;
        _thread = new Thread(Run) { IsBackground = true, Name = "test-window" };
    }

    public nint Handle { get; private set; }

    public static TestWindow Open(string title)
    {
        var window = new TestWindow(title);
        window._thread.Start();
        if (!window._created.Wait(TimeSpan.FromSeconds(5)) || window.Handle == IntPtr.Zero)
            throw new InvalidOperationException($"Test window '{title}' was not created.");
        return window;
    }

    /// <summary>Stops handling messages, as an app that hangs, until <see cref="Thaw"/>.</summary>
    public void Freeze()
    {
        PostThreadMessageW(_threadId, WM_APP_FREEZE, 0, 0);
        if (!_frozen.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("Test window did not freeze.");
    }

    public void Thaw() => _thaw.Set();

    public void Dispose()
    {
        _thaw.Set();
        PostMessageW(Handle, WM_CLOSE, 0, 0);
        PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        Handle = CreateWindowExW(0, "STATIC", _title, WS_OVERLAPPEDWINDOW, 200, 200, 400, 250, 0, 0, 0, 0);
        if (Handle != IntPtr.Zero)
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
        _created.Set();
        if (Handle == IntPtr.Zero)
            return;

        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_APP_FREEZE)
            {
                _frozen.Set();
                _thaw.Wait(MaxFreeze);
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(int exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, nint hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
