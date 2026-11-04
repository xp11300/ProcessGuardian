using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ProcessGuardian.Interop;
using ProcessGuardian.Services.JobObjects;
using ProcessGuardian.Services.Logging;

namespace ProcessGuardian.Services.ProcessManagement;

/// <summary>Starts a process suspended, assigns it to a Job Object, and only then resumes it.</summary>
public sealed class ProcessLauncher
{
    /// <summary>Upper bound for the startup-cleanup wait after TerminateProcess / Job close (failure paths only).</summary>
    internal static readonly TimeSpan StartupCleanupWaitTimeout = TimeSpan.FromSeconds(5);

    private readonly IJobProcessNativeApi _nativeApi;

    public ProcessLauncher()
        : this(Win32JobProcessNativeApi.Instance)
    {
    }

    /// <summary>Test seam: the native API is the real P/Invoke forwarder unless a test injects a fault.</summary>
    internal ProcessLauncher(IJobProcessNativeApi nativeApi)
    {
        ArgumentNullException.ThrowIfNull(nativeApi);
        _nativeApi = nativeApi;
    }

    /// <param name="arguments">
    /// Raw Windows command-line text excluding argv[0]. Quote individual values according
    /// to the target program's parser; this API does not accept an argument array or use a shell.
    /// </param>
    public ProcessLaunchResult Launch(string executablePath, string? arguments = null, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (arguments?.Contains('\0') == true)
        {
            throw new ArgumentException("Command-line arguments cannot contain a null character.", nameof(arguments));
        }

        var fullExecutablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullExecutablePath))
        {
            throw new FileNotFoundException("The target executable was not found.", fullExecutablePath);
        }

        var fullWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.GetDirectoryName(fullExecutablePath)
            : Path.GetFullPath(workingDirectory);

        var job = new JobObjectManager(_nativeApi);
        AppLog.Write(LogLevel.Information, "ProcessLauncher", "Created Job Object for launch", Path.GetFileNameWithoutExtension(fullExecutablePath));
        SafeProcessHandle? processHandle = null;
        SafeThreadObjectHandle? threadHandle = null;
        var assignedToJob = false;

        try
        {
            var commandLine = new StringBuilder(BuildCommandLine(fullExecutablePath, arguments));
            if (commandLine.Length >= 32767)
            {
                throw new ArgumentOutOfRangeException(nameof(arguments), "The Windows process command line exceeds the 32,766-character limit.");
            }
            var startupInfo = new StartupInfo
            {
                Size = (uint)Marshal.SizeOf<StartupInfo>(),
            };

            if (!_nativeApi.CreateProcess(
                    fullExecutablePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: false,
                    ProcessNativeMethods.CreateSuspended,
                    IntPtr.Zero,
                    fullWorkingDirectory,
                    ref startupInfo,
                    out var processInformation))
            {
                var exception = JobObjectNativeMethods.CreateLastErrorException("CreateProcessW");
                AppLog.Write(LogLevel.Error, "ProcessLauncher", "CreateProcessW failed", Path.GetFileNameWithoutExtension(fullExecutablePath), exception: exception);
                throw exception;
            }

            // Wrap immediately so every later failure path closes both native handles.
            processHandle = new SafeProcessHandle(processInformation.ProcessHandle, ownsHandle: true);
            threadHandle = new SafeThreadObjectHandle(processInformation.ThreadHandle, ownsHandle: true);

            if (processHandle.IsInvalid || threadHandle.IsInvalid)
            {
                throw new InvalidOperationException("CreateProcessW returned an invalid process or thread handle.");
            }

            AppLog.Write(LogLevel.Information, "ProcessLauncher", "CreateProcessW created a suspended process",
                Path.GetFileNameWithoutExtension(fullExecutablePath), checked((int)processInformation.ProcessId));

            job.AssignProcess(processHandle, processInformation.ProcessId);
            assignedToJob = true;
            AppLog.Write(LogLevel.Information, "ProcessLauncher", "Process assigned to Job Object", Path.GetFileNameWithoutExtension(fullExecutablePath), checked((int)processInformation.ProcessId));

            var previousSuspendCount = _nativeApi.ResumeThread(threadHandle);
            if (previousSuspendCount == uint.MaxValue)
            {
                var exception = JobObjectNativeMethods.CreateLastErrorException("ResumeThread");
                AppLog.Write(LogLevel.Error, "ProcessLauncher", "ResumeThread failed", Path.GetFileNameWithoutExtension(fullExecutablePath), checked((int)processInformation.ProcessId), exception);
                throw exception;
            }

            // The Job handle must outlive this method. The process and initial thread
            // handles are no longer needed once the primary thread has resumed.
            threadHandle.Dispose();
            threadHandle = null;
            processHandle.Dispose();
            processHandle = null;

            var result = new ProcessLaunchResult(processInformation.ProcessId, processInformation.ThreadId, job);
            AppLog.Write(LogLevel.Information, "ProcessLauncher", "Process resumed", Path.GetFileNameWithoutExtension(fullExecutablePath), checked((int)processInformation.ProcessId));
            job = null!;
            return result;
        }
        catch (Exception startupException)
        {
            AppLog.Write(LogLevel.Error, "ProcessLauncher", "Process startup failed; startup cleanup is running", Path.GetFileNameWithoutExtension(fullExecutablePath), exception: startupException);
            // When the suspended process is already in the Job (for example ResumeThread failed), the Job
            // is closed before the bounded wait so KILL_ON_JOB_CLOSE ends it even if TerminateProcess failed.
            var cleanupException = processHandle is { IsInvalid: false }
                ? TerminateAndWait(processHandle, assignedToJob ? job : null)
                : null;

            threadHandle?.Dispose();
            processHandle?.Dispose();
            job?.Dispose();

            if (cleanupException is not null)
            {
                throw new AggregateException(
                    "Process startup failed, and cleanup of the suspended process also reported an error.",
                    startupException,
                    cleanupException);
            }

            throw;
        }
    }

    /// <summary>Failure-path cleanup of the suspended process. The wait is bounded; a timeout or a failed
    /// TerminateProcess is reported (never swallowed). If the process is in <paramref name="jobContainingProcess"/>,
    /// that Job is closed before waiting so KILL_ON_JOB_CLOSE can end the process.</summary>
    private Exception? TerminateAndWait(SafeProcessHandle processHandle, JobObjectManager? jobContainingProcess)
    {
        Exception? terminationException = null;
        var terminated = _nativeApi.TerminateProcess(processHandle, exitCode: 1);
        if (!terminated)
        {
            terminationException = JobObjectNativeMethods.CreateLastErrorException("TerminateProcess during startup cleanup");
        }

        jobContainingProcess?.Dispose();

        if (!terminated && jobContainingProcess is null)
        {
            // Nothing else can end a suspended process that is outside any Job of ours; waiting could only time out.
            AppLog.Write(LogLevel.Error, "ProcessLauncher", "TerminateProcess failed during startup cleanup; the suspended process is not in the Job", exception: terminationException);
            return terminationException;
        }

        var waitResult = _nativeApi.WaitForSingleObject(processHandle, checked((uint)StartupCleanupWaitTimeout.TotalMilliseconds));
        if (waitResult == ProcessNativeMethods.WaitObject0)
        {
            if (terminationException is not null)
            {
                AppLog.Write(LogLevel.Error, "ProcessLauncher", "TerminateProcess failed during startup cleanup; the process ended when its Job closed", exception: terminationException);
            }

            return terminationException;
        }

        Exception waitException = waitResult switch
        {
            uint.MaxValue => JobObjectNativeMethods.CreateLastErrorException("WaitForSingleObject during startup cleanup"),
            ProcessNativeMethods.WaitTimeout => new TimeoutException(
                $"The suspended process did not exit within {StartupCleanupWaitTimeout.TotalSeconds:0} s of startup cleanup."),
            _ => new Win32Exception($"The process cleanup wait returned unexpected result 0x{waitResult:X8}."),
        };
        AppLog.Write(LogLevel.Error, "ProcessLauncher", "Startup cleanup wait did not confirm process exit", exception: waitException);

        return terminationException is null
            ? waitException
            : new AggregateException(terminationException, waitException);
    }

    private static string BuildCommandLine(string executablePath, string? arguments)
    {
        var commandLine = QuoteWindowsArgument(executablePath);
        return string.IsNullOrWhiteSpace(arguments) ? commandLine : $"{commandLine} {arguments}";
    }

    private static string QuoteWindowsArgument(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        var backslashCount = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashCount);
                builder.Append(character);
            }

            backslashCount = 0;
        }

        builder.Append('\\', backslashCount * 2);
        builder.Append('"');
        return builder.ToString();
    }
}
