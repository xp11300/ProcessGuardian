using ProcessGuardian.Services.JobObjects;

namespace ProcessGuardian.Services.ProcessManagement;

/// <summary>Contains the new process ID and keeps its Job Object alive.</summary>
public sealed class ProcessLaunchResult : IDisposable
{
    private int _disposed;

    internal ProcessLaunchResult(uint processId, uint threadId, JobObjectManager jobObject)
    {
        ProcessId = processId;
        ThreadId = threadId;
        JobObject = jobObject;
    }

    public uint ProcessId { get; }

    public uint ThreadId { get; }

    public JobObjectManager JobObject { get; }

    /// <summary>Disposes the Job Object. With KILL_ON_JOB_CLOSE this ends its processes.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        JobObject.Dispose();
    }
}
