using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using FocusAgent.Native.Win32;

namespace FocusAgent.Native;

/// <summary>
/// What Windows knows about another process that .NET's <c>Process</c> doesn't expose
/// (#487): which process started it, and the command line it was started with. The
/// Velopack install and update hooks read both of their parent (Update.exe or
/// Setup.exe): whether it starts the agent again itself, and what to wait for.
///
/// <para>
/// Both need only <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, which a process of the
/// same user at the same or a lower integrity level grants, and neither reads the
/// other process's memory. Either answers null when the process can't be opened or
/// queried.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessQueries
{
    private const int ProcessBasicInformation = 0;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    private const int StatusBufferOverflow = unchecked((int)0x80000005);

    /// <summary>
    /// The id of the process that started process <paramref name="processId"/>, or null.
    /// Windows keeps the id after that parent exits, and may have given it to a newer
    /// process by then; check the parent started before the child before trusting it.
    /// </summary>
    public static int? ParentIdOf(int processId)
    {
        using var process = Open(processId);
        if (process.IsInvalid) return null;

        var status = NtQueryInformationProcess(
            process, ProcessBasicInformation, out var info, Marshal.SizeOf<ProcessBasicInformationData>(), out _);
        if (status != 0) return null;
        return (int)info.InheritedFromUniqueProcessId;
    }

    /// <summary>The command line process <paramref name="processId"/> was started with, or null.</summary>
    public static string? CommandLineOf(int processId)
    {
        using var process = Open(processId);
        if (process.IsInvalid) return null;

        // The answer is a UNICODE_STRING header followed by the characters it points at.
        var size = 1024;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, size, out var needed);
                if (status == 0)
                {
                    var text = Marshal.PtrToStructure<UnicodeString>(buffer);
                    return text.Buffer == 0 ? string.Empty : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
                }
                if (status is not (StatusInfoLengthMismatch or StatusBufferTooSmall or StatusBufferOverflow))
                    return null;
                size = Math.Max(needed, size * 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    private static SafeProcessHandle Open(int processId) =>
        NativeMethods.OpenProcessHandle(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformationData
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle processHandle,
        int processInformationClass,
        out ProcessBasicInformationData processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle processHandle,
        int processInformationClass,
        nint processInformation,
        int processInformationLength,
        out int returnLength);
}
