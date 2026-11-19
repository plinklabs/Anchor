using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace FocusAgent.Native;

/// <summary>
/// Finds the folder of a package registered for the signed-in user, by its
/// package family (#537): Edge registers one for every site installed as an app,
/// and its manifest names the app (<see cref="FocusAgent.Core.Tamper.EdgeWebAppPackages"/>).
/// The package API of <c>kernel32</c>, which needs no elevation and works on any
/// thread.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InstalledPackages
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>
    /// The install folder of the package of <paramref name="packageFamilyName"/>
    /// (<c>localhost-AB457204_ebq0fnkrwaj2c</c>) registered for the user, or null
    /// when none is.
    /// </summary>
    public static string? FolderOf(string packageFamilyName)
    {
        uint count = 0, length = 0;
        var result = GetPackagesByPackageFamily(packageFamilyName, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
        if (result != ErrorInsufficientBuffer || count == 0 || length == 0)
            return null;

        var names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
        var buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
        try
        {
            if (GetPackagesByPackageFamily(packageFamilyName, ref count, names, ref length, buffer) != ErrorSuccess)
                return null;
            for (var i = 0; i < count; i++)
            {
                if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size)) is { Length: > 0 } fullName
                    && PathOf(fullName) is { } folder)
                    return folder;
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(names);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? PathOf(string packageFullName)
    {
        uint length = 0;
        if (GetPackagePathByFullName(packageFullName, ref length, null) != ErrorInsufficientBuffer || length == 0)
            return null;
        var path = new StringBuilder((int)length);
        return GetPackagePathByFullName(packageFullName, ref length, path) == ErrorSuccess && path.Length > 0
            ? path.ToString()
            : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(
        string packageFamilyName, ref uint count, IntPtr packageFullNames, ref uint bufferLength, IntPtr buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, StringBuilder? path);
}
