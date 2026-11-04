using System.IO;
using System.Diagnostics;
using ProcessGuardian.Services.ProcessManagement;
using ProcessGuardian.Services.WindowMonitoring;
using ProcessGuardian.Services.Logging;

namespace ProcessGuardian.Services.ManagedApplications;

/// <summary>Launches and coordinates independently managed applications.</summary>
public sealed class ManagedApplicationService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ManagedApplication> _applications = new();
    private readonly Dictionary<int, int> _startThreads = new();
    private readonly ProcessLauncher _processLauncher;
    private readonly SynchronizationContext? _synchronizationContext;
    private volatile bool _disposed;
    private int _activeStarts;

    /// <summary>Opt-in hide-as-close timings for applications started later. Injectable for tests.</summary>
    internal HideAsCloseTimings HideAsCloseTimings { get; set; } = HideAsCloseTimings.Default;

    /// <summary>WinEvent hook API for window monitors started later. Null (the default) uses the real
    /// SetWinEventHook/UnhookWinEvent. Injectable for stage-12 fault-injection tests only.</summary>
    internal ProcessGuardian.Interop.IWinEventHookApi? WinEventHookApi { get; set; }

    public ManagedApplicationService(
        ProcessLauncher? processLauncher = null,
        SynchronizationContext? synchronizationContext = null)
    {
        _processLauncher = processLauncher ?? new ProcessLauncher();
        _synchronizationContext = synchronizationContext ?? SynchronizationContext.Current;
    }

    public event EventHandler<ManagedApplicationStateChangedEventArgs>? ApplicationStateChanged;

    public IReadOnlyCollection<ManagedApplication> Applications
    {
        get
        {
            lock (_gate)
            {
                return _applications.Values.ToArray();
            }
        }
    }

    /// <summary>Stops one tracked application by closing its Job Object.</summary>
    public void Stop(ManagedApplication application)
    {
        EnsureTracked(application);
        AppLog.Write(LogLevel.Information, "ManagedApplication", "Manual Stop requested", application.SoftwareName, checked((int)application.ProcessId));
        Cleanup(application, closingWindow: true);
    }

    /// <summary>Stops an application if needed, then removes it from this service.</summary>
    public void Remove(ManagedApplication application)
    {
        EnsureTracked(application);
        Cleanup(application, closingWindow: true);

        lock (_gate)
        {
            _applications.Remove(application.Id);
        }

        application.StateChanged -= OnApplicationStateChanged;
    }

    /// <summary>Starts one program in its own Job Object and begins monitoring its main window.</summary>
    public ManagedApplication Start(
        string softwareName,
        string executablePath,
        string? arguments = null,
        string? workingDirectory = null,
        bool treatMainWindowHideAsClose = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(softwareName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeStarts++;
            var threadId = Environment.CurrentManagedThreadId;
            _startThreads[threadId] = _startThreads.GetValueOrDefault(threadId) + 1;
        }

        try
        {
            return StartCore(softwareName.Trim(), executablePath, arguments, workingDirectory, treatMainWindowHideAsClose);
        }
        finally
        {
            lock (_gate)
            {
                _activeStarts--;
                var threadId = Environment.CurrentManagedThreadId;
                var threadStartCount = _startThreads[threadId] - 1;
                if (threadStartCount == 0)
                {
                    _startThreads.Remove(threadId);
                }
                else
                {
                    _startThreads[threadId] = threadStartCount;
                }

                Monitor.PulseAll(_gate);
            }
        }
    }

    public void Dispose()
    {
        ManagedApplication[] applications;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var startsOnCurrentThread = _startThreads.GetValueOrDefault(Environment.CurrentManagedThreadId);
            while (_activeStarts > startsOnCurrentThread)
            {
                Monitor.Wait(_gate);
                startsOnCurrentThread = _startThreads.GetValueOrDefault(Environment.CurrentManagedThreadId);
            }

            applications = _applications.Values.ToArray();
        }

        foreach (var application in applications)
        {
            Cleanup(application, closingWindow: false);
            application.StateChanged -= OnApplicationStateChanged;
        }
    }

    private ManagedApplication StartCore(
        string softwareName,
        string executablePath,
        string? arguments,
        string? workingDirectory,
        bool treatMainWindowHideAsClose)
    {
        var fullPath = executablePath;
        ProcessLaunchResult? launchResult;
        try
        {
            fullPath = Path.GetFullPath(executablePath);
            launchResult = _processLauncher.Launch(fullPath, arguments, workingDirectory);
        }
        catch (Exception exception)
        {
            AppLog.Write(LogLevel.Error, "ManagedApplication", "Application launch failed", softwareName, exception: exception);
            var failedApplication = new ManagedApplication(
                softwareName,
                fullPath,
                launchResult: null,
                ManagedApplicationStatus.Failed,
                exception);
            Register(failedApplication);
            return failedApplication;
        }

        var application = new ManagedApplication(
            softwareName,
            fullPath,
            launchResult,
            ManagedApplicationStatus.Starting);
        Register(application);
        AppLog.Write(LogLevel.Information, "ManagedApplication", "Managed application started", softwareName, checked((int)launchResult.ProcessId));

        try
        {
            var process = Process.GetProcessById(checked((int)launchResult.ProcessId));
            EventHandler processExitedHandler = (_, _) => HandlePrimaryProcessExited(application);
            if (!application.TryAttachProcessWatcher(process, processExitedHandler))
            {
                process.Dispose();
                return application;
            }

            process.EnableRaisingEvents = true;
            if (process.HasExited)
            {
                HandlePrimaryProcessExited(application);
                return application;
            }
        }
        catch (ArgumentException)
        {
            Cleanup(application, closingWindow: false);
            return application;
        }
        catch
        {
            // WindowMonitor still observes process exit if its own process watcher can start.
            application.DetachProcessWatcher()?.Dispose();
        }

        if (application.CleanupStarted)
        {
            return application;
        }

        try
        {
            var monitor = new WindowMonitor(
                launchResult.ProcessId,
                _synchronizationContext,
                treatMainWindowHideAsClose,
                HideAsCloseTimings,
                treatMainWindowHideAsClose ? launchResult.JobObject.GetProcessIds : null,
                WinEventHookApi);
            AppLog.Write(LogLevel.Information, "WindowMonitor", "Window monitor started", application.SoftwareName, checked((int)application.ProcessId));
            EventHandler<WindowMonitorStatusChangedEventArgs> handler = (_, eventArgs) =>
                HandleMonitorStatus(application, eventArgs);
            if (!application.TryAttachMonitor(monitor, handler))
            {
                monitor.Dispose();
                return application;
            }

            // The process can exit or create its first window before event subscription.
            // Read the current state after subscribing to close that race.
            HandleMonitorStatus(application, new WindowMonitorStatusChangedEventArgs(
                monitor.Status,
                monitor.MainWindowHandle,
                monitor.MainWindowTitle));
        }
        catch (Exception exception)
        {
            AppLog.Write(LogLevel.Error, "WindowMonitor", "Window monitor could not start; application remains managed", application.SoftwareName, checked((int)application.ProcessId), exception);
            application.UpdateStatus(ManagedApplicationStatus.MonitoringUnavailable, exception);
        }

        return application;
    }

    private void Register(ManagedApplication application)
    {
        lock (_gate)
        {
            _applications.Add(application.Id, application);
        }

        application.StateChanged += OnApplicationStateChanged;
    }

    private void EnsureTracked(ManagedApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_applications.TryGetValue(application.Id, out var tracked) || !ReferenceEquals(tracked, application))
            {
                throw new InvalidOperationException("The application is not managed by this service.");
            }
        }
    }

    private void HandleMonitorStatus(ManagedApplication application, WindowMonitorStatusChangedEventArgs eventArgs)
    {
        switch (eventArgs.Status)
        {
            case WindowMonitorStatus.Monitoring:
                AppLog.Write(LogLevel.Information, "WindowMonitor", "Reliable main window identified: " + eventArgs.WindowTitle, application.SoftwareName, checked((int)application.ProcessId));
                application.UpdateStatus(ManagedApplicationStatus.Running);
                break;

            case WindowMonitorStatus.NoReliableWindow:
                application.UpdateStatus(
                    ManagedApplicationStatus.MonitoringUnavailable,
                    new InvalidOperationException("The process has windows, but none can be selected reliably."));
                break;

            case WindowMonitorStatus.AmbiguousWindows:
                application.UpdateStatus(
                    ManagedApplicationStatus.MonitoringUnavailable,
                    new InvalidOperationException("More than one equally suitable top-level window was found."));
                break;

            case WindowMonitorStatus.MonitoringFailed:
                application.UpdateStatus(
                    ManagedApplicationStatus.MonitoringUnavailable,
                    application.WindowMonitor?.MonitoringException
                        ?? new InvalidOperationException("The window monitoring loop stopped unexpectedly."));
                break;

            case WindowMonitorStatus.WindowClosed:
                AppLog.Write(LogLevel.Information, "WindowMonitor", "Main window closed", application.SoftwareName, checked((int)application.ProcessId));
                QueueCleanup(application, closingWindow: true);
                break;

            case WindowMonitorStatus.ProcessExited:
                HandlePrimaryProcessExited(application);
                break;
        }
    }

    private void HandlePrimaryProcessExited(ManagedApplication application)
    {
        if (!application.MarkPrimaryProcessExited())
        {
            return;
        }

        AppLog.Write(LogLevel.Information, "ManagedApplication", "Primary process exited; closing its Job Object to clean remaining Job members",
            application.SoftwareName, checked((int)application.ProcessId));
        application.UpdateStatus(ManagedApplicationStatus.Closing);
        QueueCleanup(application, closingWindow: false);
    }

    private void QueueCleanup(ManagedApplication application, bool closingWindow)
    {
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var cleanup = (CleanupRequest)state!;
            cleanup.Service.Cleanup(cleanup.Application, cleanup.ClosingWindow);
        }, new CleanupRequest(this, application, closingWindow));
    }

    private void Cleanup(ManagedApplication application, bool closingWindow)
    {
        if (application.LaunchResult is null && application.Status == ManagedApplicationStatus.Failed)
        {
            return;
        }

        if (!application.TryBeginCleanup())
        {
            application.WaitForCleanup();
            return;
        }

        try
        {
            AppLog.Write(LogLevel.Information, "ManagedApplication", "Job Object cleanup started", application.SoftwareName,
                application.ProcessId == 0 ? null : checked((int)application.ProcessId));
            application.UpdateStatus(ManagedApplicationStatus.Closing);

            Exception? cleanupError = null;
            var processWatcher = application.DetachProcessWatcher();
            try
            {
                processWatcher?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupError = exception;
            }

            var (monitor, _) = application.DetachMonitor();
            try
            {
                monitor?.Dispose();
                if (monitor is not null)
                {
                    AppLog.Write(LogLevel.Information, "WindowMonitor", "Window monitor stopped and hook released", application.SoftwareName,
                        application.ProcessId == 0 ? null : checked((int)application.ProcessId));
                }
            }
            catch (Exception exception)
            {
                cleanupError = cleanupError is null
                    ? exception
                    : new AggregateException(cleanupError, exception);
            }

            try
            {
                // Closing the final Job Object handle terminates the complete process tree.
                application.LaunchResult?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupError = cleanupError is null
                    ? exception
                    : new AggregateException(cleanupError, exception);
            }

            application.UpdateStatus(
                cleanupError is null ? ManagedApplicationStatus.Completed : ManagedApplicationStatus.Failed,
                cleanupError);
            AppLog.Write(cleanupError is null ? LogLevel.Information : LogLevel.Error, "ManagedApplication",
                cleanupError is null ? "Managed application resources released; Job Object close requested process cleanup" : "Managed application cleanup reported an error",
                application.SoftwareName, checked((int)application.ProcessId), cleanupError);
        }
        finally
        {
            application.SignalCleanupComplete();
        }
    }

    private void OnApplicationStateChanged(object? sender, ManagedApplicationStateChangedEventArgs eventArgs)
    {
        AppLog.Write(eventArgs.Error is null ? LogLevel.Information : LogLevel.Warning, "ManagedApplication",
            $"State changed: {eventArgs.PreviousStatus} -> {eventArgs.Status}", eventArgs.Application.SoftwareName,
            eventArgs.Application.ProcessId == 0 ? null : checked((int)eventArgs.Application.ProcessId), eventArgs.Error);
        if (_disposed)
        {
            return;
        }

        if (_synchronizationContext is not null)
        {
            try
            {
                _synchronizationContext.Post(static state =>
                {
                    var notification = (StateNotification)state!;
                    notification.Service.RaiseApplicationStateChanged(notification.EventArgs);
                }, new StateNotification(this, eventArgs));
                return;
            }
            catch
            {
                // Fall back to notifying on the current thread if the context has shut down.
            }
        }

        RaiseApplicationStateChanged(eventArgs);
    }

    private void RaiseApplicationStateChanged(ManagedApplicationStateChangedEventArgs eventArgs)
    {
        if (_disposed)
        {
            return;
        }

        var handlers = ApplicationStateChanged;
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
                // A UI observer must not interrupt process-tree cleanup.
            }
        }
    }

    private sealed record CleanupRequest(
        ManagedApplicationService Service,
        ManagedApplication Application,
        bool ClosingWindow);

    private sealed record StateNotification(
        ManagedApplicationService Service,
        ManagedApplicationStateChangedEventArgs EventArgs);
}
