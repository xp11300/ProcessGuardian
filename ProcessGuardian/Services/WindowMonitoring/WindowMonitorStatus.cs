namespace ProcessGuardian.Services.WindowMonitoring;

public enum WindowMonitorStatus
{
    WaitingForWindow,
    Monitoring,
    WindowClosed,
    ProcessExited,
    NoReliableWindow,
    AmbiguousWindows,
    MonitoringFailed,
    Stopped,
}

public sealed class WindowMonitorStatusChangedEventArgs : EventArgs
{
    internal WindowMonitorStatusChangedEventArgs(WindowMonitorStatus status, IntPtr windowHandle, string? windowTitle)
    {
        Status = status;
        WindowHandle = windowHandle;
        WindowTitle = windowTitle;
    }

    public WindowMonitorStatus Status { get; }

    public IntPtr WindowHandle { get; }

    public string? WindowTitle { get; }
}
