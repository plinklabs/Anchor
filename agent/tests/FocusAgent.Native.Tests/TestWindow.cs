using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FocusAgent.Native.Tests;

/// <summary>
/// A top-level window on a thread of its own with a message pump, standing in for
/// another app's window. <see cref="Freeze"/> stops the pump until <see cref="Thaw"/>,
/// as an app that hangs, and <see cref="RestoreWhenMinimized"/> brings the window
/// back in the middle of its next minimize (#476).
/// <see cref="MinimizeInTheMiddleOfTheNextRestore"/> has a minimize asked of it land
/// in the middle of a restore from another thread (#563). It counts the times it lands in
/// the topmost band (<see cref="TimesInTheTopmostBand"/>, #477), however briefly.
/// Shown without activating, so a test never takes the foreground from whoever
/// works at the desktop.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TestWindow : IDisposable
{
    private const uint WM_SIZE = 0x0005;
    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint PM_REMOVE = 0x0001;
    private const uint WM_WINDOWPOSCHANGED = 0x0047;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_APP_FREEZE = 0x8001;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_SHOWMINNOACTIVE = 7;
    private const int GWLP_WNDPROC = -4;
    private static readonly nint SIZE_MINIMIZED = 1;
    // A frozen window thaws by itself after this, so a test that never thaws it
    // (because the code under test waited for it) still ends.
    private static readonly TimeSpan MaxFreeze = TimeSpan.FromSeconds(10);

    private readonly string _title;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _created = new();
    private readonly ManualResetEventSlim _frozen = new();
    private readonly ManualResetEventSlim _thaw = new();
    private readonly ManualResetEventSlim _heldWhileMinimizing = new();
    private readonly ManualResetEventSlim _inTheMiddleOfARestore = new();
    // Held for as long as the window may call it.
    private readonly WndProc _proc;
    private nint _staticProc;
    private int _restoreWhenMinimized;
    private int _minimizeInTheMiddleOfTheNextRestore;
    private int _minimizedInTheMiddleOfARestore;
    private int _timesInTheTopmostBand;
    // 1 when the last minimize ran on the window's own thread, 0 when another
    // thread carried it out on the window, -1 before any minimize.
    private int _minimizedItself = -1;
    private uint _threadId;

    private TestWindow(string title)
    {
        _title = title;
        _proc = WindowProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "test-window" };
    }

    public nint Handle { get; private set; }

    /// <summary>
    /// Minimized and moved off the screen. Windows marks a window that the agent
    /// minimizes as minimized at once, but it stays where it was until its app has
    /// handled the request.
    /// </summary>
    public bool HasGoneDown => IsIconic(Handle) && GetWindowRect(Handle, out var rect) && rect.Left <= MinimizedPosition;

    /// <summary>
    /// Restored and back where it was: not minimized, and no longer parked off the
    /// screen where Windows keeps a minimized window.
    /// </summary>
    public bool IsBackOnScreen => !IsIconic(Handle) && GetWindowRect(Handle, out var rect) && rect.Left > MinimizedPosition;

    /// <summary>
    /// How many times a move of the window has left it in the topmost band. The
    /// window learns of every move it makes (<c>WM_WINDOWPOSCHANGED</c>), so a move
    /// into the band and straight out again counts too.
    /// </summary>
    public int TimesInTheTopmostBand => Volatile.Read(ref _timesInTheTopmostBand);

    /// <summary>
    /// Whether the window's last minimize ran on its own thread, as one it carries
    /// out itself (<c>ShowWindowAsync</c>), which hands the foreground on (#438,
    /// #560); false when another thread carried it out on the window (a
    /// <c>ShowWindow</c> from the agent), which Windows sends to the window's thread
    /// and which leaves it the foreground window; null before any minimize.
    /// </summary>
    public bool? MinimizedItself => Volatile.Read(ref _minimizedItself) switch
    {
        1 => true,
        0 => false,
        _ => null,
    };

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

    /// <summary>
    /// The next time the window is minimized, bring it back, without activating it,
    /// in the middle of that, as a student's click on its taskbar button that lands
    /// while its app is still going down; then hold the window's thread right there
    /// until <see cref="Thaw"/>. Windows marks the window minimized as its app
    /// starts on the minimize, and says it started to minimize only once the app is
    /// done (#476).
    /// </summary>
    public void RestoreWhenMinimized()
    {
        _thaw.Reset();
        Volatile.Write(ref _restoreWhenMinimized, 1);
    }

    /// <summary>
    /// Waits until the window was brought back in the middle of a minimize and its
    /// thread is held there (<see cref="RestoreWhenMinimized"/>).
    /// </summary>
    public bool WaitUntilHeldWhileMinimizing(TimeSpan timeout) => _heldWhileMinimizing.Wait(timeout);

    /// <summary>
    /// The next time another thread restores the window, hold the restore in the
    /// middle, once Windows marked the window restored and before it moved it back
    /// on screen, until a minimize asked of the window (<c>ShowWindowAsync</c>) has
    /// been carried out there (#563). Windows carries a restore from another thread
    /// out in steps it sends to the window's thread, which handles a minimize posted
    /// to it between them, as it waits for the next one. The rest of the restore then
    /// puts the window back on screen, while it counts as minimized.
    /// <see cref="WaitUntilInTheMiddleOfARestore"/> says when the restore is held.
    /// </summary>
    public void MinimizeInTheMiddleOfTheNextRestore()
    {
        _inTheMiddleOfARestore.Reset();
        Volatile.Write(ref _minimizedInTheMiddleOfARestore, 0);
        Volatile.Write(ref _minimizeInTheMiddleOfTheNextRestore, 1);
    }

    /// <summary>
    /// Waits until a restore from another thread is held in the middle
    /// (<see cref="MinimizeInTheMiddleOfTheNextRestore"/>), for a minimize to land
    /// there.
    /// </summary>
    public bool WaitUntilInTheMiddleOfARestore(TimeSpan timeout) => _inTheMiddleOfARestore.Wait(timeout);

    /// <summary>
    /// Whether a minimize was carried out in the middle of the restore held by
    /// <see cref="MinimizeInTheMiddleOfTheNextRestore"/>.
    /// </summary>
    public bool MinimizedInTheMiddleOfARestore => Volatile.Read(ref _minimizedInTheMiddleOfARestore) == 1;

    /// <summary>
    /// Counts as minimized, yet is on screen rather than parked off it: a restore
    /// undid the minimize that landed in the middle of it (#563).
    /// </summary>
    public bool CountsAsMinimizedOnScreen =>
        IsIconic(Handle) && GetWindowRect(Handle, out var rect) && rect.Left > MinimizedPosition;

    /// <summary>Where the window is, on screen or off it.</summary>
    public (int Left, int Top, int Right, int Bottom) Bounds =>
        GetWindowRect(Handle, out var rect) ? (rect.Left, rect.Top, rect.Right, rect.Bottom) : default;

    /// <summary>
    /// Restores the window from the calling thread without activating it, as another
    /// app restores a window, and returns once the restore is done.
    /// </summary>
    public void RestoreWithoutActivating() => ShowWindow(Handle, SW_SHOWNOACTIVATE);

    /// <summary>
    /// Minimizes the window without activating another, as the student does when
    /// they put an app away, and returns once it is down.
    /// </summary>
    public void MinimizeWithoutActivating()
    {
        ShowWindow(Handle, SW_SHOWMINNOACTIVE);
        if (!HasGoneDown)
            throw new InvalidOperationException("Test window did not minimize.");
    }

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
        {
            _staticProc = SetWindowLongPtrW(Handle, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_proc));
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
        }
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

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_WINDOWPOSCHANGED && (GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0)
            Interlocked.Increment(ref _timesInTheTopmostBand);
        // A minimize another thread carries out on the window reaches it as a
        // message sent from that thread.
        if (msg == WM_SIZE && wParam == SIZE_MINIMIZED)
            Volatile.Write(ref _minimizedItself, InSendMessage() ? 0 : 1);
        // A restore from another thread sends WM_NCCALCSIZE as it moves the window
        // back on screen: marked restored, the window is still parked off it.
        if (msg == WM_NCCALCSIZE && InSendMessage() && !IsIconic(hwnd) &&
            GetWindowRect(hwnd, out var parkedAt) && parkedAt.Left <= MinimizedPosition &&
            Interlocked.Exchange(ref _minimizeInTheMiddleOfTheNextRestore, 0) == 1)
        {
            _inTheMiddleOfARestore.Set();
            // Handles its messages, as its thread does while it waits for the
            // restore's next step, until a minimize posted to it has been carried
            // out. Only a message loop that takes what is posted from the queue gets
            // to a ShowWindowAsync behind it.
            var held = System.Diagnostics.Stopwatch.StartNew();
            while (!IsIconic(hwnd) && held.Elapsed < MaxFreeze)
            {
                if (PeekMessageW(out var pending, 0, 0, 0, PM_REMOVE))
                {
                    if (pending.message == WM_QUIT)
                    {
                        PostQuitMessage((int)pending.wParam);
                        break;
                    }
                    TranslateMessage(ref pending);
                    DispatchMessageW(ref pending);
                    continue;
                }
                Thread.Sleep(1);
            }
            Volatile.Write(ref _minimizedInTheMiddleOfARestore, IsIconic(hwnd) ? 1 : 0);
        }
        // WM_SIZE comes in the middle of the minimize, once the window is marked
        // minimized and before Windows says it started to minimize.
        if (msg == WM_SIZE && wParam == SIZE_MINIMIZED && Interlocked.Exchange(ref _restoreWhenMinimized, 0) == 1)
        {
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            _heldWhileMinimizing.Set();
            _thaw.Wait(MaxFreeze);
        }
        return CallWindowProcW(_staticProc, hwnd, msg, wParam, lParam);
    }

    // Where Windows parks a minimized top-level window.
    private const int MinimizedPosition = -32000;

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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
    private static extern nint SetWindowLongPtrW(nint hWnd, int index, nint newLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProcW(nint prevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, nint hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out MSG msg, nint hWnd, uint filterMin, uint filterMax, uint removeMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(nint hWnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InSendMessage();
}
