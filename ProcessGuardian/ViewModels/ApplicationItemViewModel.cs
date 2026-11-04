using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using ProcessGuardian.Models;
using ProcessGuardian.Services.ExternalInstances;
using ProcessGuardian.Services.ManagedApplications;
using ProcessGuardian.Services.Logging;
using ProcessGuardian.Services.WindowMonitoring;

namespace ProcessGuardian.ViewModels;

public sealed class ApplicationItemViewModel : INotifyPropertyChanged
{
    private readonly ManagedApplicationService _service;
    private readonly Action<ApplicationItemViewModel> _removeFromList;
    private readonly Action _configurationChanged;
    private readonly Func<string, bool> _confirmStop;
    private readonly Func<string, bool> _confirmRemove;
    private readonly ApplicationConfiguration _configuration;
    private ManagedApplication? _application;
    private bool _isBusy;
    private bool _exitStarted;
    private string? _operationError;
    private string? _recentOperationResult;
    private readonly IExternalInstanceDetector? _externalInstanceDetector;
    private readonly Func<IReadOnlyCollection<uint>> _managedProcessIds;
    private readonly Action<string> _showExternalInstanceWarning;
    private bool _externalInstanceCheckDone;

    internal ApplicationItemViewModel(
        ApplicationConfiguration configuration,
        ManagedApplicationService service,
        Action<ApplicationItemViewModel> removeFromList,
        Action configurationChanged,
        Func<string, bool>? confirmStop = null,
        Func<string, bool>? confirmRemove = null,
        IExternalInstanceDetector? externalInstanceDetector = null,
        Func<IReadOnlyCollection<uint>>? managedProcessIds = null,
        Action<string>? showExternalInstanceWarning = null)
    {
        _externalInstanceDetector = externalInstanceDetector;
        _managedProcessIds = managedProcessIds ?? (() => Array.Empty<uint>());
        _showExternalInstanceWarning = showExternalInstanceWarning ?? (_ => { });
        _configuration = configuration;
        _service = service;
        _removeFromList = removeFromList;
        _configurationChanged = configurationChanged;
        _confirmStop = confirmStop ?? (_ => true);
        _confirmRemove = confirmRemove ?? (_ => true);

        LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => CanLaunch);
        StopCommand = new AsyncRelayCommand(StopAsync, () => CanStop);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => CanRemove);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id => _configuration.Id;

    public DateTimeOffset AddedAt => _configuration.AddedAt;

    public string SoftwareName
    {
        get => _configuration.SoftwareName;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (_configuration.SoftwareName == normalized)
            {
                return;
            }

            _configuration.SoftwareName = normalized;
            OnPropertyChanged();
            RefreshProperties();
            _configurationChanged();
        }
    }

    public string ExecutablePath => _configuration.ExecutablePath;

    public string? Arguments
    {
        get => _configuration.Arguments;
        set => UpdateOptionalValue(_configuration.Arguments, value, next => _configuration.Arguments = next, nameof(Arguments));
    }

    public string? WorkingDirectory
    {
        get => _configuration.WorkingDirectory;
        set => UpdateOptionalValue(_configuration.WorkingDirectory, value, next => _configuration.WorkingDirectory = next, nameof(WorkingDirectory));
    }

    public bool TreatMainWindowHideAsClose
    {
        get => _configuration.TreatMainWindowHideAsClose;
        set
        {
            if (_configuration.TreatMainWindowHideAsClose == value)
            {
                return;
            }

            _configuration.TreatMainWindowHideAsClose = value;
            OnPropertyChanged();
            RefreshProperties();
            _configurationChanged();
        }
    }

    public bool IsExecutableMissing => !File.Exists(ExecutablePath);

    public ManagedApplication? ManagedApplication => _application;

    public uint ProcessId => _application?.ProcessId ?? 0;

    public string ProcessIdText => ProcessId == 0 ? "—" : ProcessId.ToString();

    public string JobStatusText
    {
        get
        {
            var job = _application?.LaunchResult?.JobObject;
            if (job is null) return _application?.Status == ManagedApplicationStatus.Failed ? "Not created" : "Not active";
            try { return $"Active; {job.ActiveProcessCount} live Job member(s)"; }
            catch (ObjectDisposedException) { return "Released"; }
            catch (Win32Exception exception) { return $"Status unavailable (Windows error {exception.NativeErrorCode})"; }
            catch (Exception exception) { return $"Status unavailable ({exception.GetType().Name})"; }
        }
    }

    public string RecentOperationResult => _recentOperationResult ?? "No operation performed this session.";

    public string CompatibilityWarningText
    {
        get
        {
            if (_application?.PrimaryProcessExited == true)
                return "Detected: the primary process exited. Any remaining Job members are being cleaned up by closing the Job Object.";

            var monitor = _application?.WindowMonitor;
            if (monitor?.Status == WindowMonitorStatus.NoReliableWindow)
                return "Detected: no reliable main window. Automatic window-close cleanup is disabled; use Stop to close this application's Job.";
            if (monitor?.Status == WindowMonitorStatus.AmbiguousWindows)
                return "Detected: multiple equally suitable windows. Automatic cleanup is disabled; use Stop to close this application's Job.";
            if (monitor?.Status == WindowMonitorStatus.MonitoringFailed)
                return $"Detected: window monitoring failed ({monitor.MonitoringException?.Message ?? "unknown error"}). The Job remains active; use Stop when ready.";
            if (_application?.Status == ManagedApplicationStatus.MonitoringUnavailable && _application.Error is Win32Exception hookError)
                return $"Detected: the window hook could not start (Windows error {hookError.NativeErrorCode}: {hookError.Message}). The Job remains active; use Stop when ready.";
            if (ExecutablePath.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
                return "Possible limitation: this path is under WindowsApps. Packaged apps may require package activation instead of direct EXE launch.";

            return "Not detected: services, brokers, elevated or breakaway processes may run outside this Job. They are not searched for or stopped.";
        }
    }

    public bool HasDetectedCompatibilityWarning => _application?.PrimaryProcessExited == true
        || _application?.WindowMonitor?.Status is WindowMonitorStatus.NoReliableWindow or WindowMonitorStatus.AmbiguousWindows or WindowMonitorStatus.MonitoringFailed
        || (_application?.Status == ManagedApplicationStatus.MonitoringUnavailable && _application.Error is Win32Exception)
        || ExecutablePath.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);

    public string WorkingDirectoryDisplay => string.IsNullOrWhiteSpace(WorkingDirectory)
        ? "Executable folder (default)"
        : WorkingDirectory;

    public bool HasInvalidWorkingDirectory => !string.IsNullOrWhiteSpace(WorkingDirectory) && !Directory.Exists(WorkingDirectory);

    public string StatusText
    {
        get
        {
            var windowStatus = _application?.WindowMonitor?.Status;
            if (windowStatus is WindowMonitorStatus.NoReliableWindow or WindowMonitorStatus.AmbiguousWindows)
            {
                return "Monitoring unavailable";
            }

            if (windowStatus == WindowMonitorStatus.MonitoringFailed
                || (_application?.Status == ManagedApplicationStatus.MonitoringUnavailable && _application.Error is Win32Exception))
            {
                return "Monitoring failed";
            }

            if (IsExecutableMissing && _application is null)
            {
                return "Missing executable";
            }

            if (_isBusy && _application is null)
            {
                return "Starting";
            }

            if (_isBusy && IsActive)
            {
                return "Stopping";
            }

            if (windowStatus == WindowMonitorStatus.Monitoring && _application?.Status == ManagedApplicationStatus.Starting)
                return "Running";

            return _application?.Status switch
            {
                ManagedApplicationStatus.Starting => "Starting",
                ManagedApplicationStatus.Running => "Running",
                ManagedApplicationStatus.Closing => "Stopping",
                ManagedApplicationStatus.Completed => "Stopped",
                ManagedApplicationStatus.Failed => "Failed",
                ManagedApplicationStatus.MonitoringUnavailable => "Monitoring unavailable",
                _ => "Not running",
            };
        }
    }

    public string MonitorStatusText
    {
        get
        {
            var monitor = _application?.WindowMonitor;
            if (_application?.PrimaryProcessExited == true)
            {
                return "Primary process exited; Job cleanup requested";
            }
            if (monitor is null)
            {
                return _application?.Status == ManagedApplicationStatus.MonitoringUnavailable
                    ? _application.Error is Win32Exception exception
                        ? $"Hook failed (Windows error {exception.NativeErrorCode})"
                        : "No reliable main window was identified"
                    : "Not started";
            }

            return monitor.Status switch
            {
                WindowMonitorStatus.WaitingForWindow => "Waiting for main window",
                WindowMonitorStatus.Monitoring => $"Monitoring: {monitor.MainWindowTitle}",
                WindowMonitorStatus.WindowClosed => "Main window closed",
                WindowMonitorStatus.ProcessExited => "Process exited",
                WindowMonitorStatus.NoReliableWindow => "No reliable main window",
                WindowMonitorStatus.AmbiguousWindows => "Multiple possible main windows",
                WindowMonitorStatus.MonitoringFailed => $"Monitoring failed: {monitor.MonitoringException?.Message ?? "unknown error"}",
                WindowMonitorStatus.Stopped => "Stopped",
                _ => "Unavailable",
            };
        }
    }

    public string? ErrorMessage => _operationError
        ?? (HasInvalidWorkingDirectory ? $"Working directory does not exist: {WorkingDirectory}. Leave it blank to use the EXE folder or choose an existing folder." : null)
        ?? (IsExecutableMissing ? $"Executable not found: {ExecutablePath}. Check the path or remove the saved entry." : _application?.Status switch
        {
            ManagedApplicationStatus.MonitoringUnavailable => $"Window monitoring is unavailable ({_application.Error?.Message ?? "no reliable main window"}). The process is still protected by its Job Object and will not be stopped automatically. Use Stop when you are ready.",
            ManagedApplicationStatus.Failed => $"Operation failed: {_application.Error?.GetType().Name}: {_application.Error?.Message}",
            _ => _application?.Error?.Message,
        });

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsActive => _application?.Status is
        ManagedApplicationStatus.Starting or
        ManagedApplicationStatus.Running or
        ManagedApplicationStatus.Closing or
        ManagedApplicationStatus.MonitoringUnavailable
        || (_isBusy && _application is null);

    public bool CanLaunch => !_exitStarted && !_isBusy && !IsActive && !IsExecutableMissing && !HasInvalidWorkingDirectory;

    public bool CanStop => !_exitStarted && !_isBusy && _application?.LaunchResult is not null
        && _application.Status is not (ManagedApplicationStatus.Closing or ManagedApplicationStatus.Completed);

    public bool CanRemove => !_exitStarted && !_isBusy
        && _application?.Status != ManagedApplicationStatus.Closing;

    public AsyncRelayCommand LaunchCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand RemoveCommand { get; }

    internal void ApplyApplication(ManagedApplication application)
    {
        _application = application;
        _operationError = null;
        RefreshProperties();
    }

    internal void ApplyState(ManagedApplication application)
    {
        if (_application?.Id != application.Id)
        {
            return;
        }

        if (application.PrimaryProcessExited && application.Status == ManagedApplicationStatus.Closing)
        {
            _recentOperationResult = "Primary process exited; closing its Job to clean any remaining Job members.";
        }
        else if (application.Status == ManagedApplicationStatus.Completed
            && _recentOperationResult?.StartsWith("Stop completed", StringComparison.Ordinal) != true)
        {
            _recentOperationResult = "Application lifecycle completed; its Job Object handle was released.";
        }
        RefreshProperties();
    }

    private async Task LaunchAsync()
    {
        if (HasInvalidWorkingDirectory)
        {
            _operationError = $"Working directory does not exist: {WorkingDirectory}. Choose an existing folder or leave it blank to use the executable's folder.";
            _recentOperationResult = "Launch rejected before creating a process.";
            RefreshProperties();
            return;
        }

        var externalCheckDone = _externalInstanceCheckDone;
        _externalInstanceCheckDone = false;
        if (!externalCheckDone)
        {
            var external = await FindExternalInstancesAsync();
            if (external.Found)
            {
                RefuseExternalInstance(external);
                _showExternalInstanceWarning(ExternalInstanceMessages.Format(SoftwareName, external.ProcessIds));
                return;
            }

            if (_exitStarted)
            {
                return;
            }
        }

        _application = null;
        _operationError = null;
        _recentOperationResult = "Launch in progress…";
        SetBusy(true);
        try
        {
            var application = await Task.Run(() =>
                _service.Start(SoftwareName, ExecutablePath, Arguments, WorkingDirectory, TreatMainWindowHideAsClose));
            ApplyApplication(application);
            _recentOperationResult = application.Status switch
            {
                ManagedApplicationStatus.Failed => $"Launch failed: {application.Error?.GetType().Name}: {application.Error?.Message}",
                ManagedApplicationStatus.MonitoringUnavailable => $"Launched (PID {application.ProcessId}); monitoring is unavailable. The Job remains active; use Stop to clean its members.",
                _ => $"Launched successfully (PID {application.ProcessId}).",
            };
        }
        catch (Exception exception)
        {
            _operationError = exception.Message;
            _recentOperationResult = $"Launch failed: {exception.GetType().Name}: {exception.Message}";
            AppLog.Write(LogLevel.Error, "ApplicationItem", "Launch command failed", SoftwareName, exception: exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Read-only check for an instance of this entry's executable that runs outside Process Guardian. The PIDs this
    /// Process Guardian manages are collected on the calling (UI) thread; the process scan runs on the thread pool.
    /// Without a detector (or on any failure) nothing is found and the launch proceeds as before.
    /// </summary>
    internal async Task<ExternalInstanceCheckResult> FindExternalInstancesAsync()
    {
        var detector = _externalInstanceDetector;
        if (detector is null)
        {
            return ExternalInstanceCheckResult.None;
        }

        try
        {
            var excluded = _managedProcessIds();
            var path = ExecutablePath;
            return await Task.Run(() => detector.Find(path, excluded));
        }
        catch (Exception exception)
        {
            AppLog.Write(LogLevel.Error, "ApplicationItem", "The external-instance check failed; the launch is allowed as before", SoftwareName, exception: exception);
            return ExternalInstanceCheckResult.None;
        }
    }

    /// <summary>Records a refused launch without touching the displayed lifecycle state.</summary>
    internal void RefuseExternalInstance(ExternalInstanceCheckResult external)
    {
        var processIds = string.Join(", ", external.ProcessIds);
        AppLog.Write(LogLevel.Warning, "ApplicationItem",
            $"Launch refused: {ExecutablePath} is already running outside Process Guardian (PID {processIds}); it cannot be managed",
            SoftwareName, checked((int)external.ProcessIds[0]));
        _recentOperationResult = $"Launch refused: {ExternalInstanceMessages.Headline} (PID {processIds}).";
        RefreshProperties();
    }

    /// <summary>The next Launch skips its own external-instance check because the caller has just performed it.</summary>
    internal void MarkExternalInstanceChecked() => _externalInstanceCheckDone = true;

    private async Task StopAsync()
    {
        var application = _application;
        if (application is null)
        {
            return;
        }

        if (!_confirmStop(SoftwareName))
        {
            _recentOperationResult = "Stop canceled; the managed Job is unchanged.";
            RefreshProperties();
            return;
        }

        _operationError = null;
        _recentOperationResult = "Stopping this application's Job…";
        SetBusy(true);
        try
        {
            await Task.Run(() => _service.Stop(application));
            if (application.Status == ManagedApplicationStatus.Failed)
            {
                _operationError = application.Error?.Message ?? "Cleanup reported a failure.";
                _recentOperationResult = $"Stop reported failure: {_operationError}";
                AppLog.Write(LogLevel.Error, "ApplicationItem", "Manual Stop reported a cleanup error", SoftwareName,
                    ProcessId == 0 ? null : checked((int)ProcessId), application.Error);
            }
            else
            {
                _recentOperationResult = "Stop completed: this application's Job Object handle was released. Processes outside that Job are not affected.";
            }
            RefreshProperties();
        }
        catch (Exception exception)
        {
            _operationError = exception.Message;
            _recentOperationResult = $"Stop failed: {exception.GetType().Name}: {exception.Message}";
            AppLog.Write(LogLevel.Error, "ApplicationItem", "Manual Stop failed", SoftwareName,
                ProcessId == 0 ? null : checked((int)ProcessId), exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RemoveAsync()
    {
        var application = _application;
        if (!_confirmRemove(SoftwareName))
        {
            _recentOperationResult = "Remove canceled; the application remains in the list.";
            RefreshProperties();
            return;
        }
        _operationError = null;
        _recentOperationResult = IsActive ? "Stopping this application's Job before removing it…" : "Removing application configuration…";
        SetBusy(true);
        try
        {
            if (application is not null)
            {
                await Task.Run(() => _service.Remove(application));
            }

            _removeFromList(this);
        }
        catch (Exception exception)
        {
            _operationError = exception.Message;
            _recentOperationResult = $"Remove failed: {exception.GetType().Name}: {exception.Message}";
            AppLog.Write(LogLevel.Error, "ApplicationItem", "Remove failed", SoftwareName, exception: exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        _isBusy = value;
        RefreshProperties();
    }

    internal ApplicationConfiguration ToConfiguration() => new()
    {
        Id = _configuration.Id,
        SoftwareName = _configuration.SoftwareName,
        ExecutablePath = _configuration.ExecutablePath,
        AddedAt = _configuration.AddedAt,
        Arguments = _configuration.Arguments,
        WorkingDirectory = _configuration.WorkingDirectory,
        TreatMainWindowHideAsClose = _configuration.TreatMainWindowHideAsClose,
    };

    internal void BeginExit()
    {
        _exitStarted = true;
        RefreshProperties();
    }

    internal IReadOnlyList<Task> GetPendingOperations() =>
    [
        LaunchCommand.ExecutionTask,
        StopCommand.ExecutionTask,
        RemoveCommand.ExecutionTask,
    ];

    internal void RefreshRuntimeInformation()
    {
        OnPropertyChanged(nameof(JobStatusText));
        OnPropertyChanged(nameof(MonitorStatusText));
        OnPropertyChanged(nameof(CompatibilityWarningText));
        OnPropertyChanged(nameof(HasDetectedCompatibilityWarning));
    }

    private void UpdateOptionalValue(string? current, string? value, Action<string?> update, string propertyName)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (current == normalized)
        {
            return;
        }

        update(normalized);
        OnPropertyChanged(propertyName);
        RefreshProperties();
        _configurationChanged();
    }

    private void RefreshProperties()
    {
        OnPropertyChanged(nameof(ManagedApplication));
        OnPropertyChanged(nameof(ProcessId));
        OnPropertyChanged(nameof(ProcessIdText));
        OnPropertyChanged(nameof(JobStatusText));
        OnPropertyChanged(nameof(RecentOperationResult));
        OnPropertyChanged(nameof(CompatibilityWarningText));
        OnPropertyChanged(nameof(HasDetectedCompatibilityWarning));
        OnPropertyChanged(nameof(WorkingDirectoryDisplay));
        OnPropertyChanged(nameof(HasInvalidWorkingDirectory));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(MonitorStatusText));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsExecutableMissing));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRemove));
        LaunchCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
