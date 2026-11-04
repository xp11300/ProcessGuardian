using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using ProcessGuardian.Interop;
using ProcessGuardian.Services.Logging;

namespace ProcessGuardian.Services.JobObjects;

/// <summary>
/// Owns a Windows Job Object that terminates its processes when its last handle closes.
/// </summary>
public sealed class JobObjectManager : IDisposable
{
    private readonly SafeJobObjectHandle _jobHandle;
    private readonly IJobProcessNativeApi _nativeApi;
    private int _disposed;

    public JobObjectManager()
        : this(Win32JobProcessNativeApi.Instance)
    {
    }

    /// <summary>Test seam: the native API is the real P/Invoke forwarder unless a test injects a fault.</summary>
    internal JobObjectManager(IJobProcessNativeApi nativeApi)
    {
        ArgumentNullException.ThrowIfNull(nativeApi);
        _nativeApi = nativeApi;
        _jobHandle = _nativeApi.CreateJobObject(IntPtr.Zero, null);
        if (_jobHandle.IsInvalid)
        {
            var exception = JobObjectNativeMethods.CreateLastErrorException("CreateJobObject");
            _jobHandle.Dispose();
            AppLog.Write(LogLevel.Error, "JobObject", "CreateJobObject failed", exception: exception);
            throw exception;
        }

        try
        {
            var information = new JobObjectExtendedLimitInformation();
            information.BasicLimitInformation.LimitFlags = JobObjectNativeMethods.JobObjectLimitKillOnJobClose;
            var size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<JobObjectExtendedLimitInformation>();

            if (!_nativeApi.SetInformationJobObject(
                    _jobHandle,
                    JobObjectNativeMethods.JobObjectExtendedLimitInformation,
                    ref information,
                    size))
            {
                throw JobObjectNativeMethods.CreateLastErrorException("SetInformationJobObject");
            }
            AppLog.Write(LogLevel.Debug, "JobObject", "Created Job Object with KILL_ON_JOB_CLOSE");
        }
        catch (Exception exception)
        {
            _jobHandle.Dispose();
            AppLog.Write(LogLevel.Error, "JobObject", "Could not configure KILL_ON_JOB_CLOSE", exception: exception);
            throw;
        }
    }

    /// <summary>Assigns a running process to this job. Windows may reject assignment, for example
    /// when the process is in an incompatible job or nested jobs are unavailable.</summary>
    public void AssignProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ThrowIfDisposed();

        if (process.HasExited)
        {
            throw new InvalidOperationException($"Process {process.Id} has already exited.");
        }

        AssignProcess(process.SafeHandle, (uint)process.Id);
    }

    internal void AssignProcess(SafeProcessHandle processHandle, uint processId)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        ThrowIfDisposed();

        if (!_nativeApi.AssignProcessToJobObject(_jobHandle, processHandle))
        {
            var exception = JobObjectNativeMethods.CreateLastErrorException(
                $"AssignProcessToJobObject for process {processId}. The process may already belong to an incompatible Job Object, or nested Job Objects may not be supported");
            AppLog.Write(LogLevel.Error, "JobObject", "AssignProcessToJobObject failed", processId: checked((int)processId), exception: exception);
            throw exception;
        }
        AppLog.Write(LogLevel.Information, "JobObject", "Assigned process to Job Object", processId: checked((int)processId));
    }

    /// <summary>Checks whether a process is currently assigned to this job.</summary>
    public bool ContainsProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ThrowIfDisposed();

        if (!JobObjectNativeMethods.IsProcessInJob(process.SafeHandle, _jobHandle, out var isInJob))
        {
            throw JobObjectNativeMethods.CreateLastErrorException(
                $"IsProcessInJob for process {process.Id}");
        }

        return isInJob;
    }

    /// <summary>Returns the current number of live processes assigned to this job.</summary>
    public uint ActiveProcessCount
    {
        get
        {
            ThrowIfDisposed();
            var size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<JobObjectBasicAccountingInformation>();
            if (!JobObjectNativeMethods.QueryInformationJobObject(
                    _jobHandle,
                    JobObjectNativeMethods.JobObjectBasicAccountingInformation,
                    out var information,
                    size,
                    out _))
            {
                throw JobObjectNativeMethods.CreateLastErrorException("QueryInformationJobObject");
            }

            return information.ActiveProcesses;
        }
    }

    /// <summary>Read-only: returns the IDs of the processes currently assigned to this job
    /// (JobObjectBasicProcessIdList). Used only by the opt-in hide-as-close block check.</summary>
    internal IReadOnlyList<uint> GetProcessIds()
    {
        ThrowIfDisposed();
        const int headerSize = 2 * sizeof(uint);
        var capacity = 16;
        while (true)
        {
            var size = headerSize + capacity * IntPtr.Size;
            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            try
            {
                var succeeded = JobObjectNativeMethods.QueryInformationJobObject(
                    _jobHandle,
                    JobObjectNativeMethods.JobObjectBasicProcessIdList,
                    buffer,
                    (uint)size,
                    out _);
                if (!succeeded)
                {
                    var errorCode = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    if (errorCode == JobObjectNativeMethods.ErrorMoreData && capacity < 65536)
                    {
                        capacity *= 4;
                        continue;
                    }

                    throw new System.ComponentModel.Win32Exception(errorCode, $"QueryInformationJobObject(JobObjectBasicProcessIdList) failed (Windows error {errorCode}).");
                }

                var assigned = System.Runtime.InteropServices.Marshal.ReadInt32(buffer, 0);
                var listed = System.Runtime.InteropServices.Marshal.ReadInt32(buffer, sizeof(uint));
                if (listed < assigned && capacity < 65536)
                {
                    capacity *= 4;
                    continue;
                }

                var processIds = new uint[Math.Min(listed, capacity)];
                for (var index = 0; index < processIds.Length; index++)
                {
                    processIds[index] = unchecked((uint)System.Runtime.InteropServices.Marshal.ReadIntPtr(buffer, headerSize + index * IntPtr.Size).ToInt64());
                }

                return processIds;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>Terminates every process currently assigned to this job.</summary>
    public void Terminate(uint exitCode = 1)
    {
        ThrowIfDisposed();
        if (!JobObjectNativeMethods.TerminateJobObject(_jobHandle, exitCode))
        {
            throw JobObjectNativeMethods.CreateLastErrorException("TerminateJobObject");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _jobHandle.Dispose();
        AppLog.Write(LogLevel.Information, "JobObject", "Closed Job Object handle; KILL_ON_JOB_CLOSE requested process cleanup");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
