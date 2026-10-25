using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// A plain top-level app window shown by the test process itself, which no
/// session allows: an off-list app for the specs that need one next to an
/// allowed Notepad (#437). Owning the window keeps the spec independent of which
/// apps a Windows build ships. It runs on a dedicated thread with a message pump,
/// so the agent can minimize it and the spec can restore it, like any app. It
/// can stop handling messages for a while (<see cref="Freeze"/>,
/// <see cref="HangWhenMinimized"/>), as an app that is not responding (#440).
/// And it counts the keystrokes it receives (#441), so a spec can tell whether
/// what the student types reaches the blocked app, which is the only window a
/// spec may type into besides the agent's own.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class OffListWindow : IDisposable
{
    private const string ClassName = "AnchorE2EOffListWindow";
    private static readonly object RegisterGate = new();
    // The window class holds a function pointer to this delegate for the life of
    // the process, so it must never be collected.
    private static readonly WndProc Proc = WindowProc;
    private static readonly ConcurrentDictionary<IntPtr, OffListWindow> Open = new();
    private static bool _registered;

    private readonly string _title;
    private readonly bool _minimized;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<IntPtr> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _frozen = new();
    private readonly ManualResetEventSlim _thaw = new();
    private TimeSpan _hangFor;
    private int _hangWhenMinimized;
    private int _keystrokes;

    /// <summary>
    /// Key presses the window has received: <c>WM_KEYDOWN</c>, and
    /// <c>WM_SYSKEYDOWN</c>, which is how a minimized window that still has the
    /// foreground gets them.
    /// </summary>
    public int KeystrokesReceived => Volatile.Read(ref _keystrokes);

    private OffListWindow(string title, bool minimized)
    {
        _title = title;
        _minimized = minimized;
        _thread = new Thread(Run) { IsBackground = true, Name = "off-list-window" };
    }

    /// <summary>The window's handle.</summary>
    public IntPtr Handle { get; private set; }

    /// <summary>Whether the window's thread is not handling messages right now.</summary>
    public bool IsFrozen => _frozen.IsSet;

    /// <summary>
    /// Minimized and moved off the screen. Windows marks a window that the agent
    /// minimizes as minimized at once, but it stays on screen until its app has
    /// handled the request.
    /// </summary>
    public bool HasGoneDown => IsIconic(Handle) && GetWindowRect(Handle, out var rect) && rect.Left <= MinimizedPosition;

    /// <summary>
    /// Show a new window titled <paramref name="title"/> and wait for it. A
    /// <paramref name="minimized"/> window starts out on the taskbar without taking
    /// the foreground, as an app the student has not opened yet: it raises no event
    /// the agent judges until it is restored.
    /// </summary>
    public static async Task<OffListWindow> OpenAsync(string title, TimeSpan timeout, bool minimized = false)
    {
        var window = new OffListWindow(title, minimized);
        window._thread.Start();
        try
        {
            window.Handle = await window._created.Task.WaitAsync(timeout);
            return window;
        }
        catch
        {
            window.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Stop handling messages for <paramref name="duration"/>, or until
    /// <see cref="Thaw"/>, as an app that is not responding. Returns once the
    /// window's thread has stopped.
    /// </summary>
    public void Freeze(TimeSpan duration)
    {
        PrepareHang(duration);
        PostMessageW(Handle, WmAppHang, IntPtr.Zero, IntPtr.Zero);
        if (!_frozen.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("The off-list window did not freeze.");
    }

    /// <summary>
    /// The next time the window is minimized, stop handling messages in the middle
    /// of it, for <paramref name="duration"/> or until <see cref="Thaw"/>: an app
    /// that stops responding just as it is taken down. Whoever minimizes it waits
    /// for it, as for any app that is not responding.
    /// </summary>
    public void HangWhenMinimized(TimeSpan duration)
    {
        PrepareHang(duration);
        Volatile.Write(ref _hangWhenMinimized, 1);
    }

    /// <summary>Wait until the window's thread has stopped handling messages.</summary>
    public bool WaitUntilFrozen(TimeSpan timeout) => _frozen.Wait(timeout);

    /// <summary>Handle messages again.</summary>
    public void Thaw() => _thaw.Set();

    /// <summary>
    /// Bring the window back from minimized, as a click on its taskbar button does.
    /// Posted to the window's own thread, so the spec is not held up should the
    /// window stop responding on the way (<see cref="HangWhenMinimized"/>).
    /// </summary>
    public void Restore() => ShowWindowAsync(Handle, SwRestore);

    private void PrepareHang(TimeSpan duration)
    {
        // Windows replaces a window that has not responded for 5 s with a ghost
        // window of its own, which would be another window for the agent to judge.
        DisableProcessWindowsGhosting();
        _hangFor = duration;
        _thaw.Reset();
    }

    private void Hang()
    {
        _frozen.Set();
        _thaw.Wait(_hangFor);
        _frozen.Reset();
    }

    private void Run()
    {
        try
        {
            var instance = GetModuleHandleW(null);
            lock (RegisterGate)
            {
                if (!_registered)
                {
                    var windowClass = new WndClassEx
                    {
                        cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                        lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                        hInstance = instance,
                        hCursor = LoadCursorW(IntPtr.Zero, IdcArrow),
                        hbrBackground = new IntPtr(ColorWindow + 1),
                        lpszClassName = ClassName,
                    };
                    if (RegisterClassExW(ref windowClass) == 0)
                        throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()}).");
                    _registered = true;
                }
            }

            var hwnd = CreateWindowExW(
                0, ClassName, _title, WsOverlappedWindow,
                200, 200, 480, 320, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");
            Open[hwnd] = this;
            ShowWindow(hwnd, _minimized ? SwShowMinNoActive : SwShowNormal);
            _created.TrySetResult(hwnd);

            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            _created.TrySetException(ex);
        }
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg is WmKeyDown or WmSysKeyDown && Open.TryGetValue(hwnd, out var window))
            Interlocked.Increment(ref window._keystrokes);
        // Handled here rather than in the pump: a message loop Windows runs inside
        // the window procedure, while the window is minimized or restored, may be
        // the one that takes it from the queue.
        if (msg == WmAppHang && Open.TryGetValue(hwnd, out var hanging))
        {
            hanging.Hang();
            return IntPtr.Zero;
        }
        if (msg == WmSize && wParam == SizeMinimized && Open.TryGetValue(hwnd, out var minimized) &&
            Interlocked.Exchange(ref minimized._hangWhenMinimized, 0) == 1)
            minimized.Hang();
        if (msg == WmDestroy)
        {
            Open.TryRemove(hwnd, out _);
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        _thaw.Set();
        // WM_CLOSE → DestroyWindow → WM_DESTROY → the pump quits.
        if (Handle != IntPtr.Zero)
            PostMessageW(Handle, WmClose, IntPtr.Zero, IntPtr.Zero);
        if (_thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(5));
    }

    private const uint WmDestroy = 0x0002;
    private const uint WmSize = 0x0005;
    private const uint WmClose = 0x0010;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmAppHang = 0x8000 + 1;
    private static readonly IntPtr SizeMinimized = new(1);
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const int SwShowNormal = 1;
    private const int SwShowMinNoActive = 7;
    private const int SwRestore = 9;
    // Where Windows parks a minimized top-level window.
    private const int MinimizedPosition = -32000;
    private const int ColorWindow = 5;
    private static readonly IntPtr IdcArrow = new(32512);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref Msg lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern void DisableProcessWindowsGhosting();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);
}
