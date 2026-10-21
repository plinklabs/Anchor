using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// A plain top-level app window shown by the test process itself, which no
/// session allows: an off-list app for the specs that need one next to an
/// allowed Notepad (#437). Owning the window keeps the spec independent of which
/// apps a Windows build ships. It runs on a dedicated thread with a message pump,
/// so the agent can minimize it and the spec can restore it, like any app.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class OffListWindow : IDisposable
{
    private const string ClassName = "AnchorE2EOffListWindow";
    private static readonly object RegisterGate = new();
    // The window class holds a function pointer to this delegate for the life of
    // the process, so it must never be collected.
    private static readonly WndProc Proc = WindowProc;
    private static bool _registered;

    private readonly string _title;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<IntPtr> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private OffListWindow(string title)
    {
        _title = title;
        _thread = new Thread(Run) { IsBackground = true, Name = "off-list-window" };
    }

    /// <summary>The window's handle.</summary>
    public IntPtr Handle { get; private set; }

    /// <summary>Show a new window titled <paramref name="title"/> and wait for it.</summary>
    public static async Task<OffListWindow> OpenAsync(string title, TimeSpan timeout)
    {
        var window = new OffListWindow(title);
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
            ShowWindow(hwnd, SwShowNormal);
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
        if (msg == WmDestroy)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        // WM_CLOSE → DestroyWindow → WM_DESTROY → the pump quits.
        if (Handle != IntPtr.Zero)
            PostMessageW(Handle, WmClose, IntPtr.Zero, IntPtr.Zero);
        if (_thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(5));
    }

    private const uint WmDestroy = 0x0002;
    private const uint WmClose = 0x0010;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const int SwShowNormal = 1;
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
}
