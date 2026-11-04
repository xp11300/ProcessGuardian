using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProcessGuardian.Interop;

/// <summary>
/// Native calls used by Job Object creation and suspended process startup. Production code always
/// uses <see cref="Win32JobProcessNativeApi.Instance"/>, which forwards to the existing P/Invoke
/// declarations unchanged. Tests can wrap it to make one call fail (stage-12 fault injection).
/// Implementations that report failure must leave the Win32 error in the last P/Invoke error
/// (Marshal.GetLastWin32Error), exactly as the real calls do.
/// </summary>
internal interface IJobProcessNativeApi
{
    SafeJobObjectHandle CreateJobObject(IntPtr jobAttributes, string? name);

    bool SetInformationJobObject(
        SafeJobObjectHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    bool AssignProcessToJobObject(SafeJobObjectHandle job, SafeProcessHandle process);

    bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    uint ResumeThread(SafeThreadObjectHandle thread);

    bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}

/// <summary>Default implementation: a direct forward to the P/Invoke declarations.</summary>
internal sealed class Win32JobProcessNativeApi : IJobProcessNativeApi
{
    internal static readonly Win32JobProcessNativeApi Instance = new();

    private Win32JobProcessNativeApi()
    {
    }

    public SafeJobObjectHandle CreateJobObject(IntPtr jobAttributes, string? name) =>
        JobObjectNativeMethods.CreateJobObject(jobAttributes, name);

    public bool SetInformationJobObject(
        SafeJobObjectHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength) =>
        JobObjectNativeMethods.SetInformationJobObject(job, informationClass, ref information, informationLength);

    public bool AssignProcessToJobObject(SafeJobObjectHandle job, SafeProcessHandle process) =>
        JobObjectNativeMethods.AssignProcessToJobObject(job, process);

    public bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation) =>
        ProcessNativeMethods.CreateProcess(
            applicationName,
            commandLine,
            processAttributes,
            threadAttributes,
            inheritHandles,
            creationFlags,
            environment,
            currentDirectory,
            ref startupInfo,
            out processInformation);

    public uint ResumeThread(SafeThreadObjectHandle thread) => ProcessNativeMethods.ResumeThread(thread);

    public bool TerminateProcess(SafeProcessHandle process, uint exitCode) =>
        ProcessNativeMethods.TerminateProcess(process, exitCode);

    public uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds) =>
        ProcessNativeMethods.WaitForSingleObject(handle, milliseconds);
}
