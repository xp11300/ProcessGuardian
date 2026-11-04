namespace ProcessGuardian.Services.ManagedApplications;

public enum ManagedApplicationStatus
{
    Starting,
    Running,
    Closing,
    Completed,
    Failed,
    MonitoringUnavailable,
}

public sealed class ManagedApplicationStateChangedEventArgs : EventArgs
{
    internal ManagedApplicationStateChangedEventArgs(
        ManagedApplication application,
        ManagedApplicationStatus previousStatus,
        ManagedApplicationStatus status,
        Exception? error)
    {
        Application = application;
        PreviousStatus = previousStatus;
        Status = status;
        Error = error;
    }

    public ManagedApplication Application { get; }

    public ManagedApplicationStatus PreviousStatus { get; }

    public ManagedApplicationStatus Status { get; }

    public Exception? Error { get; }
}
