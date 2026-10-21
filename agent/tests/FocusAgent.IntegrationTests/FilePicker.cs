using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Storage.Pickers;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// The real Windows file picker, opened for another app's window (#428). A WinRT
/// <see cref="FileOpenPicker"/> initialized with that window is shown by Windows
/// in <c>PickerHost</c>, as a window owned by the app's window from another
/// process: the same shape as the dialog Edge opens for an upload, which the
/// agent used to block. Where a Windows build shows the picker in the calling
/// process instead, it is still a dialog owned from another process, so the spec
/// keeps exercising the same path.
///
/// The picker runs on a dedicated STA thread with a message pump, so its
/// completion can be delivered when <see cref="Dispose"/> cancels it.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class FilePicker : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<uint> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private FilePicker(IntPtr owner)
    {
        Owner = owner;
        _thread = new Thread(Run) { IsBackground = true, Name = "file-picker" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>The app window the picker was opened for.</summary>
    public IntPtr Owner { get; }

    /// <summary>The picker's own window.</summary>
    public IntPtr Window { get; private set; }

    /// <summary>The process showing the picker — <c>PickerHost</c> on Windows 11.</summary>
    public string HostProcessName { get; private set; } = "";

    /// <summary>True while the picker is on screen and not minimized: usable.</summary>
    public bool IsUsable => WindowCapture.IsVisible(Window) && !WindowCapture.IsIconic(Window);

    /// <summary>Open the picker for <paramref name="owner"/> and wait for its window.</summary>
    public static async Task<FilePicker> OpenForAsync(IntPtr owner, TimeSpan timeout)
    {
        var picker = new FilePicker(owner);
        picker._thread.Start();
        try
        {
            await picker._started.Task.WaitAsync(timeout);
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var window = WindowCapture.FindWindowOwnedFromAnotherProcess(owner);
                if (window != IntPtr.Zero)
                {
                    picker.Window = window;
                    picker.HostProcessName = WindowCapture.GetProcessName(window);
                    return picker;
                }
                await Task.Delay(100);
            }
            throw new TimeoutException(
                $"The file picker did not open a window owned by 0x{owner.ToInt64():X} within {timeout.TotalSeconds:N0}s.");
        }
        catch
        {
            picker.Dispose();
            throw;
        }
    }

    private void Run()
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Owner);
            // The pick itself is never answered: Dispose cancels the dialog.
            _ = picker.PickSingleFileAsync();
            _started.TrySetResult(GetCurrentThreadId());
        }
        catch (Exception ex)
        {
            _started.TrySetException(new InvalidOperationException("Could not open the Windows file picker.", ex));
            return;
        }

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    public void Dispose()
    {
        // Cancel the dialog the way its close button does, then stop the pump.
        if (Window != IntPtr.Zero && WindowCapture.IsWindow(Window))
        {
            WindowCapture.PostClose(Window);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (WindowCapture.IsWindow(Window) && DateTime.UtcNow < deadline)
                Thread.Sleep(100);
        }
        if (_started.Task.IsCompletedSuccessfully)
            PostThreadMessageW(_started.Task.Result, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private const uint WM_QUIT = 0x0012;

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

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Msg lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
}
