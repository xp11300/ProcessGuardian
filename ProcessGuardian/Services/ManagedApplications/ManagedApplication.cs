using System.Diagnostics;
using ProcessGuardian.Services.ProcessManagement;
using ProcessGuardian.Services.WindowMonitoring;

namespace ProcessGuardian.Services.ManagedApplications;

/// <summary>Tracks one launched application and the resources tied to its lifetime.</summary>
public sealed class ManagedApplication
{
    private readonly object _gate = new();
    private ManagedApplicationStatus _status;
    private Exception? _error;
    private WindowMonitor? _windowMonitor;
    private EventHandler<WindowMonitorStatusChangedEventArgs>? _monitorHandler;
    private Process? _processWatcher;
    private EventHandler? _processExitedHandler;
    private int _cleanupStarted;
    private int _primaryProcessExited;
    private int _cleanupThreadId;
    private readonly TaskCompletionSource _cleanupCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ManagedApplication(
        string softwareName,
        string executablePath,
        ProcessLaunchResult? launchResult,
        ManagedApplicationStatus initialStatus,
        Exception? error = null)
    {
        Id = Guid.NewGuid();
        SoftwareName = softwareName;
        ExecutablePath = executablePath;
        LaunchResult = launchResult;
        _status = initialStatus;
        _error = error;
    }

    public event EventHandler<ManagedApplicationStateChangedEventArgs>? StateChanged;

    public Guid Id { get; }

    public string SoftwareName { get; }

    public string ExecutablePath { get; }

    public uint ProcessId => LaunchResult?.ProcessId ?? 0;

    public ProcessLaunchResult? LaunchResult { get; }

    public WindowMonitor? WindowMonitor
    {
        get
        {
            lock (_gate)
            {
                return _windowMonitor;
            }
        }
    }

    public ManagedApplicationStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public Exception? Error
    {
        get
        {
            lock (_gate)
            {
                return _error;
            }
        }
    }

    /// <summary>True only after the retained primary-process watcher reported its exit.</summary>
    public bool PrimaryProcessExited => Volatile.Read(ref _primaryProcessExited) != 0;

    internal bool MarkPrimaryProcessExited() => Interlocked.Exchange(ref _primaryProcessExited, 1) == 0;

    internal bool TryBeginCleanup()
    {
        if (Interlocked.CompareExchange(ref _cleanupStarted, 1, 0) != 0)
        {
            return false;
        }

        Volatile.Write(ref _cleanupThreadId, Environment.CurrentManagedThreadId);
        return true;
    }

    internal bool CleanupStarted => Volatile.Read(ref _cleanupStarted) != 0;

    internal void WaitForCleanup()
    {
        if (Volatile.Read(ref _cleanupThreadId) != Environment.CurrentManagedThreadId)
        {
            _cleanupCompletion.Task.GetAwaiter().GetResult();
        }
    }

    internal void SignalCleanupComplete() => _cleanupCompletion.TrySetResult();

    internal bool TryAttachMonitor(WindowMonitor monitor, EventHandler<WindowMonitorStatusChangedEventArgs> handler)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _cleanupStarted) != 0)
            {
                return false;
            }

            _windowMonitor = monitor;
            _monitorHandler = handler;
            monitor.StatusChanged += handler;
            return true;
        }
    }

    internal bool TryAttachProcessWatcher(Process process, EventHandler handler)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _cleanupStarted) != 0)
            {
                return false;
            }

            _processWatcher = process;
            _processExitedHandler = handler;
            process.Exited += handler;
            return true;
        }
    }

    internal (WindowMonitor? Monitor, EventHandler<WindowMonitorStatusChangedEventArgs>? Handler) DetachMonitor()
    {
        lock (_gate)
        {
            var monitor = _windowMonitor;
            var handler = _monitorHandler;
            if (monitor is not null && handler is not null)
            {
                monitor.StatusChanged -= handler;
            }

            _monitorHandler = null;
            return (monitor, handler);
        }
    }

    internal Process? DetachProcessWatcher()
    {
        lock (_gate)
        {
            var process = _processWatcher;
            if (process is not null && _processExitedHandler is not null)
            {
                process.Exited -= _processExitedHandler;
            }

            _processWatcher = null;
            _processExitedHandler = null;
            return process;
        }
    }

    internal void UpdateStatus(ManagedApplicationStatus status, Exception? error = null)
    {
        ManagedApplicationStateChangedEventArgs eventArgs;
        lock (_gate)
        {
            if (_status == status && ReferenceEquals(_error, error))
            {
                return;
            }

            var previousStatus = _status;
            _status = status;
            _error = error;
            eventArgs = new ManagedApplicationStateChangedEventArgs(this, previousStatus, status, error);
        }

        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<ManagedApplicationStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch
            {
                // Observers must not interrupt lifecycle cleanup.
            }
        }
    }
}
