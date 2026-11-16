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
