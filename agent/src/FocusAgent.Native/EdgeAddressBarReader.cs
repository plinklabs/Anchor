using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FocusAgent.Core.Focus;
using FocusAgent.Core.Tamper;
using FocusAgent.Native.Win32;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Native;

/// <summary>
/// Reads Edge's address bar through UI Automation (#526, test phase only, #515).
///
/// Chromium shows its address bar as an Edit element whose class is
/// <see cref="OmniboxClassName"/>; its Name is localized ("Address and search
/// bar", "Adres- en zoekbalk"), its class is not. Its Value is the text Edge shows
/// and HasKeyboardFocus says whether the student is in it (measured on Edge 157).
/// The element is looked up afresh on every read: a read every few seconds costs
/// tens of milliseconds, and nothing is held on to between reads.
///
/// UI Automation is called through its vtables rather than COM wrappers, like
/// <see cref="BrowserWindowAppIds"/>'s property store, from the free-threaded
/// client object (<c>CUIAutomation8</c>) created on the first read. Reads run on
/// timer threads, which this puts in the multithreaded apartment. A hung Edge holds
/// a read up for <see cref="Timeout"/> at most.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EdgeAddressBarReader : IAddressBarReader, IDisposable
{
    /// <summary>The UI Automation class of Chromium's address bar (the omnibox view).</summary>
    public const string OmniboxClassName = "OmniboxViewViews";

    /// <summary>How long one UI Automation call may wait on Edge.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly Guid CLSID_CUIAutomation8 = new("e22ad333-b25f-460c-83d0-0581107395c9");
    private static readonly Guid IID_IUIAutomation2 = new("34723aff-0c9d-49d0-9896-7ab52df8cd8a");

    private const int UIA_ClassNamePropertyId = 30012;
    private const int UIA_HasKeyboardFocusPropertyId = 30008;
    private const int UIA_ValueValuePropertyId = 30045;
    private const int TreeScope_Descendants = 0x4;

    private const ushort VT_BOOL = 11;
    private const ushort VT_BSTR = 8;

    // vtable slots: IUnknown takes 0-2.
    private const int SlotRelease = 2;
    private const int SlotElementFromHandle = 6;        // IUIAutomation
    private const int SlotCreatePropertyCondition = 23; // IUIAutomation
    private const int SlotPutConnectionTimeout = 61;    // IUIAutomation2 (after IUIAutomation's 55)
    private const int SlotPutTransactionTimeout = 63;   // IUIAutomation2
    private const int SlotFindFirst = 5;                // IUIAutomationElement
    private const int SlotGetCurrentPropertyValue = 10; // IUIAutomationElement

    private const int COINIT_MULTITHREADED = 0x0;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    private readonly IAppIdentifier _identifier;
    private readonly ILogger<EdgeAddressBarReader> _log;
    private readonly object _gate = new();
    private nint _automation;
    private nint _omniboxCondition;
    private bool _disposed;

    public EdgeAddressBarReader(IAppIdentifier identifier, ILogger<EdgeAddressBarReader>? log = null)
    {
        _identifier = identifier;
        _log = log ?? NullLogger<EdgeAddressBarReader>.Instance;
    }

    public nint ForegroundWindowOf(string processName)
    {
        try
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return IntPtr.Zero;
            NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
            var app = _identifier.Identify(foreground, (int)pid);
            return app is not null && Normalize(app.ProcessName).Equals(Normalize(processName), StringComparison.OrdinalIgnoreCase)
                ? foreground
                : IntPtr.Zero;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "EdgeAddressBarReader: reading the foreground window threw");
            return IntPtr.Zero;
        }
    }

    public AddressBarReading? Read(nint windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle)) return null;
        try
        {
            return ReadOmnibox(windowHandle);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "EdgeAddressBarReader: reading hwnd 0x{Hwnd:X} threw", windowHandle);
            return null;
        }
    }

    private unsafe AddressBarReading? ReadOmnibox(nint windowHandle)
    {
        // The timer threads reads run on are the multithreaded apartment's; an STA
        // thread can't use the client object created there. Balanced below: the
        // apartment itself outlives this thread's hold on it.
        var hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        if (hr == RPC_E_CHANGED_MODE) return null;
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        nint window = 0, omnibox = 0;
        try
        {
            if (!TryGetAutomation(out var automation, out var condition)) return null;

            var elementFromHandle = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(automation, SlotElementFromHandle);
            if (elementFromHandle(automation, windowHandle, &window) < 0 || window == 0) return null;

            var findFirst = (delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)Slot(window, SlotFindFirst);
            if (findFirst(window, TreeScope_Descendants, condition, &omnibox) < 0 || omnibox == 0) return null;

            var text = ReadString(omnibox, UIA_ValueValuePropertyId);
            if (text is null) return null;
            return new AddressBarReading(text, ReadBool(omnibox, UIA_HasKeyboardFocusPropertyId));
        }
        finally
        {
            Release(omnibox);
            Release(window);
            CoUninitialize();
        }
    }

    /// <summary>The client object and the "class is the omnibox" condition, made on the first read.</summary>
    private unsafe bool TryGetAutomation(out nint automation, out nint condition)
    {
        lock (_gate)
        {
            automation = 0;
            condition = 0;
            if (_disposed) return false;
            if (_automation == 0)
            {
                var clsid = CLSID_CUIAutomation8;
                var iid = IID_IUIAutomation2;
                nint created;
                var hr = CoCreateInstance(&clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, &iid, &created);
                if (hr < 0 || created == 0)
                {
                    _log.LogWarning("UI Automation is not available (0x{Hr:X8}); Edge's address bar can't be read", hr);
                    return false;
                }

                var ms = (int)Timeout.TotalMilliseconds;
                _ = ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(created, SlotPutConnectionTimeout))(created, ms);
                _ = ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(created, SlotPutTransactionTimeout))(created, ms);

                var value = new Variant { Type = VT_BSTR, Pointer = Marshal.StringToBSTR(OmniboxClassName) };
                nint made = 0;
                try
                {
                    var createCondition = (delegate* unmanaged[Stdcall]<nint, int, Variant, nint*, int>)Slot(created, SlotCreatePropertyCondition);
                    hr = createCondition(created, UIA_ClassNamePropertyId, value, &made);
                }
                finally
                {
                    Marshal.FreeBSTR(value.Pointer);
                }
                if (hr < 0 || made == 0)
                {
                    Release(created);
                    _log.LogWarning("Could not make the UI Automation condition for Edge's address bar (0x{Hr:X8})", hr);
                    return false;
                }

                _automation = created;
                _omniboxCondition = made;
            }
            automation = _automation;
            condition = _omniboxCondition;
            return true;
        }
    }

    private static unsafe string? ReadString(nint element, int propertyId)
    {
        var value = default(Variant);
        var get = (delegate* unmanaged[Stdcall]<nint, int, Variant*, int>)Slot(element, SlotGetCurrentPropertyValue);
        if (get(element, propertyId, &value) < 0) return null;
        try
        {
            if (value.Type != VT_BSTR) return null;
            return value.Pointer == 0 ? "" : Marshal.PtrToStringBSTR(value.Pointer);
        }
        finally
        {
            _ = VariantClear(&value);
        }
    }

    private static unsafe bool ReadBool(nint element, int propertyId)
    {
        var value = default(Variant);
        var get = (delegate* unmanaged[Stdcall]<nint, int, Variant*, int>)Slot(element, SlotGetCurrentPropertyValue);
        if (get(element, propertyId, &value) < 0) return false;
        try
        {
            return value.Type == VT_BOOL && value.Bool != 0;
        }
        finally
        {
            _ = VariantClear(&value);
        }
    }

    private static unsafe nint Slot(nint comObject, int slot) => (*(nint**)comObject)[slot];

    private static unsafe void Release(nint comObject)
    {
        if (comObject == 0) return;
        _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(comObject, SlotRelease))(comObject);
    }

    private static string Normalize(string? value)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Release(_omniboxCondition);
            Release(_automation);
            _omniboxCondition = 0;
            _automation = 0;
        }
    }

    // VARIANT: the type, then (after padding) the value; 24 bytes on 64-bit Windows.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
        [FieldOffset(8)] public short Bool;
    }

    private const uint CLSCTX_INPROC_SERVER = 0x1;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, int coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern unsafe int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);

    [DllImport("oleaut32.dll")]
    private static extern unsafe int VariantClear(Variant* value);
}
