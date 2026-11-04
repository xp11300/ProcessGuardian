using System.Runtime.InteropServices;
using System.Text;

namespace ProcessGuardian.JobObjectTests;

internal static class TestProcessNativeMethods
{
    internal const uint CreateSuspended = 0x00000004;
    internal const uint Infinite = 0xFFFFFFFF;
    internal const uint WaitObject0 = 0;

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref TestStartupInfo startupInfo,
        out TestProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(IntPtr threadHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(IntPtr processHandle, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    internal static string? GetProcessCommandLine(IntPtr processHandle)
    {
        const int processCommandLineInformation = 60;
        NtQueryInformationProcess(processHandle, processCommandLineInformation, IntPtr.Zero, 0, out var requiredSize);
        if (requiredSize < IntPtr.Size + 4 || requiredSize > 1024 * 1024)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)requiredSize));
        try
        {
            if (NtQueryInformationProcess(processHandle, processCommandLineInformation, buffer, requiredSize, out _) != 0)
            {
                return null;
            }

            var length = unchecked((ushort)Marshal.ReadInt16(buffer));
            var stringPointer = Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4);
            return length == 0 || stringPointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(stringPointer, length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int informationClass, IntPtr information, uint informationLength, out uint returnLength);

}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct TestStartupInfo
{
    internal uint Size;
    internal IntPtr Reserved;
    internal IntPtr Desktop;
    internal IntPtr Title;
    internal uint X;
    internal uint Y;
    internal uint XSize;
    internal uint YSize;
    internal uint XCountChars;
    internal uint YCountChars;
    internal uint FillAttribute;
    internal uint Flags;
    internal ushort ShowWindow;
    internal ushort Reserved2Size;
    internal IntPtr Reserved2;
    internal IntPtr StandardInput;
    internal IntPtr StandardOutput;
    internal IntPtr StandardError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TestProcessInformation
{
    internal IntPtr ProcessHandle;
    internal IntPtr ThreadHandle;
    internal uint ProcessId;
    internal uint ThreadId;
}
