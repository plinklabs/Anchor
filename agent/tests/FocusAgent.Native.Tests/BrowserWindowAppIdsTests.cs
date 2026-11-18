using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FocusAgent.Native;

namespace FocusAgent.Native.Tests;

/// <summary>
/// #518: reading a window's explicit AppUserModelID, which Edge sets to its
/// profile's on every browser window. The test's own window gets one the way an
/// app sets it, through the window's property store.
/// </summary>
[SupportedOSPlatform("windows")]
public class BrowserWindowAppIdsTests
{
    [Fact]
    public void Reads_the_app_id_a_window_was_given_also_while_it_is_minimized()
    {
        using var window = TestWindow.Open("Anchor app id test");
        using var current = Process.GetCurrentProcess();
        var reader = new BrowserWindowAppIds(new AppIdentifier());

        // No explicit ID yet: the window is left out.
        Assert.Null(BrowserWindowAppIds.ReadAppId(window.Handle));
        Assert.DoesNotContain("MSEdge.UserData.Profile1", reader.GetWindowAppIds(current.ProcessName));

        WindowAppId.Set(window.Handle, "MSEdge.UserData.Profile1");

        Assert.Equal("MSEdge.UserData.Profile1", BrowserWindowAppIds.ReadAppId(window.Handle));
        Assert.Contains("MSEdge.UserData.Profile1", reader.GetWindowAppIds(current.ProcessName + ".exe"));
        Assert.Empty(reader.GetWindowAppIds("no-such-process-" + Guid.NewGuid().ToString("N")));

        // A paused Edge is minimized, and its profile is still open.
        window.MinimizeWithoutActivating();
        Assert.Contains("MSEdge.UserData.Profile1", reader.GetWindowAppIds(current.ProcessName));
    }

    [Fact]
    public void Reads_each_browser_window_s_app_id_title_and_whether_it_is_minimized()
    {
        // #525: which profile a window belongs to (its ID), whether it is InPrivate
        // (its title), and whether it is down already.
        using var window = TestWindow.Open("about:blank - [InPrivate] - Microsoft Edge");
        using var other = TestWindow.Open("No app id");
        using var current = Process.GetCurrentProcess();
        var reader = new BrowserWindowAppIds(new AppIdentifier());
        WindowAppId.Set(window.Handle, "MSEdge.UserData.Profile1");

        var found = Assert.Single(reader.GetBrowserWindows(current.ProcessName), w => w.Handle == window.Handle);
        Assert.Equal("MSEdge.UserData.Profile1", found.AppId);
        Assert.Equal("about:blank - [InPrivate] - Microsoft Edge", found.Title);
        Assert.False(found.Minimized);
        // A window without an ID is no browser window.
        Assert.DoesNotContain(reader.GetBrowserWindows(current.ProcessName), w => w.Handle == other.Handle);
        Assert.Empty(reader.GetBrowserWindows("no-such-process-" + Guid.NewGuid().ToString("N")));

        window.MinimizeWithoutActivating();
        var described = reader.Describe(window.Handle);
        Assert.NotNull(described);
        Assert.Equal("MSEdge.UserData.Profile1", described.AppId);
        Assert.True(described.Minimized);
        Assert.Null(reader.Describe(0));
    }

    [Fact]
    public void The_launcher_closes_a_window_as_its_close_button_does_and_opens_only_edge_in_a_profile()
    {
        var window = TestWindow.Open("Anchor close test");
        try
        {
            using var current = Process.GetCurrentProcess();
            // Standing another process in for the browser, as the e2e does: nothing
            // is started, so a test never opens Edge on the developer's desktop.
            var launcher = new BrowserProfileLauncher(current.ProcessName);

            launcher.CloseWindow(window.Handle);
            launcher.OpenProfile("Profile 1");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (IsWindow(window.Handle) && DateTime.UtcNow < deadline)
                Thread.Sleep(50);
            Assert.False(IsWindow(window.Handle), "The window was not closed.");
        }
        finally
        {
            window.Dispose();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [Fact]
    public void Reads_it_from_a_thread_that_never_initialised_COM()
    {
        // The scan runs on a timer thread.
        using var window = TestWindow.Open("Anchor app id test (thread)");
        WindowAppId.Set(window.Handle, "MSEdge");
        string? read = null;

        var thread = new Thread(() => read = BrowserWindowAppIds.ReadAppId(window.Handle));
        thread.Start();
        thread.Join();

        Assert.Equal("MSEdge", read);
    }
}

/// <summary>Sets a window's <c>System.AppUserModel.ID</c>, as an app does for its own windows.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowAppId
{
    public static void Set(nint hwnd, string appId)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd, ref iid, out var store));
        try
        {
            var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
            var value = new PropVariant { Type = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(appId) };
            try
            {
                Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
                Marshal.ThrowExceptionForHR(store.Commit());
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.Pointer);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        nint hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
}
