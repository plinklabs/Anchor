using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FocusAgent.Core.Focus;
using FocusAgent.Core.Tamper;
using FocusAgent.Native.Win32;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Native;

/// <summary>
/// Reads the explicit AppUserModelID of a browser's top-level windows (#518):
/// <c>System.AppUserModel.ID</c> in each window's property store, which Edge sets
/// to its profile's ID so that Windows groups the taskbar buttons per profile.
/// Minimized windows count: a paused Edge is minimized, and its profile is still
/// open. Hidden helper windows carry no ID and drop out by themselves.
///
/// The property store is called through its vtable rather than a COM wrapper, so
/// it works on whatever thread the scan runs on.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrowserWindowAppIds : IBrowserWindowAppIds
{
    private const ushort VT_LPWSTR = 31;
    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid AppUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint AppUserModelIdProperty = 5;

    private readonly IAppIdentifier _identifier;
    private readonly ILogger<BrowserWindowAppIds> _log;

    public BrowserWindowAppIds(IAppIdentifier identifier, ILogger<BrowserWindowAppIds>? log = null)
    {
        _identifier = identifier;
        _log = log ?? NullLogger<BrowserWindowAppIds>.Instance;
    }

    public IReadOnlyList<string> GetWindowAppIds(string processName)
    {
        var wanted = Normalize(processName);
        var ids = new List<string>();
        // Never throw out of the callback (Windows handles it badly); skip a
        // window that can't be read and keep enumerating.
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                var app = _identifier.Identify(hwnd, (int)pid);
                if (app is null || !Normalize(app.ProcessName).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (ReadAppId(hwnd) is { Length: > 0 } id)
                    ids.Add(id);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "BrowserWindowAppIds: skipping hwnd 0x{Hwnd:X} after probe threw", hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return ids;
    }

    /// <summary>The window's explicit AppUserModelID, or null when it has none.</summary>
    internal static unsafe string? ReadAppId(nint hwnd)
    {
        var iid = IID_IPropertyStore;
        if (SHGetPropertyStoreForWindow(hwnd, &iid, out var store) != 0 || store == 0)
            return null;
        try
        {
            // IPropertyStore: IUnknown (3 slots), GetCount, GetAt, GetValue, ...
            var vtable = *(nint**)store;
            var getValue = (delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)vtable[5];
            var key = new PropertyKey { FormatId = AppUserModelFormat, PropertyId = AppUserModelIdProperty };
            var value = default(PropVariant);
            if (getValue(store, &key, &value) != 0)
                return null;
            try
            {
                return value.Type == VT_LPWSTR && value.Pointer != 0 ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                _ = PropVariantClear(&value);
            }
        }
        finally
        {
            var vtable = *(nint**)store;
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
            _ = release(store);
        }
    }

    private static string Normalize(string? value)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // PROPVARIANT: the type, then (after padding) the value; 24 bytes on x64.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
    }

    [DllImport("shell32.dll")]
    private static extern unsafe int SHGetPropertyStoreForWindow(nint hwnd, Guid* riid, out nint propertyStore);

    [DllImport("ole32.dll")]
    private static extern unsafe int PropVariantClear(PropVariant* value);
}
