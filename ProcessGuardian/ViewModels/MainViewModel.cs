using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using ProcessGuardian.Models;
using ProcessGuardian.Services.CommandLine;
using ProcessGuardian.Services;
using ProcessGuardian.Services.Configuration;
using ProcessGuardian.Services.DesktopShortcuts;
using ProcessGuardian.Services.ExternalInstances;
using ProcessGuardian.Services.ManagedApplications;
using ProcessGuardian.Services.Logging;
using ProcessGuardian.Services.Startup;

namespace ProcessGuardian.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ManagedApplicationService _service;
    private readonly IExecutableFilePicker _filePicker;
    private readonly ConfigurationService _configurationService;
    private readonly Func<bool> _confirmRestoreDefaults;
    private readonly Action<string> _openFolder;
    private readonly Func<string, bool> _confirmStop;
    private readonly Func<string, bool> _confirmRemove;
    private readonly DesktopShortcutService _desktopShortcutService;
    private readonly Action<string, bool> _showShortcutResult;
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private readonly DispatcherTimer _statisticsTimer;
    private readonly IExternalInstanceDetector? _externalInstanceDetector;
    private readonly Action<string> _showExternalInstanceWarning;
    private readonly AutoStartService? _autoStartService;
    private readonly Action<string> _showAutoStartError;
    private bool _isAutoStartEnabled;
    private string? _autoStartHint;
    private string _currentPage = "Applications";
    private int _runningSoftwareCount;
    private int _managedProcessCount;
    private string? _configurationError;
    private string? _applicationMessage;
    private bool _isLoadingConfiguration;
    private readonly object _saveGate = new();
    private Task _pendingSave = Task.CompletedTask;
    private bool _disposed;
    private bool _isConfigurationLoaded;
    private bool _configurationLoadFailed;
    private bool _exitStarted;

    public MainViewModel(
        ManagedApplicationService service,
        IExecutableFilePicker filePicker,
        ConfigurationService? configurationService = null,
        Func<bool>? confirmRestoreDefaults = null,
        Action<string>? openFolder = null,
        Func<string, bool>? confirmStop = null,
        Func<string, bool>? confirmRemove = null,
        Action<string, bool>? showShortcutResult = null,
        DesktopShortcutService? desktopShortcutService = null,
        IExternalInstanceDetector? externalInstanceDetector = null,
        Action<string>? showExternalInstanceWarning = null,
        AutoStartService? autoStartService = null,
        Action<string>? showAutoStartError = null)
    {
        // Without an auto-start service (the default) the setting is unavailable and the registry is never touched;
        // MainWindow passes the real per-user one.
        _autoStartService = autoStartService;
        _showAutoStartError = showAutoStartError ?? (_ => { });
        // Without a detector (the default) no external-instance check is made; MainWindow passes the real one.
        _externalInstanceDetector = externalInstanceDetector;
        _showExternalInstanceWarning = showExternalInstanceWarning ?? (_ => { });
        _service = service;
        _filePicker = filePicker;
        _uiSynchronizationContext = SynchronizationContext.Current;
        _configurationService = configurationService ?? new ConfigurationService();
        _confirmRestoreDefaults = confirmRestoreDefaults ?? (() => false);
        _openFolder = openFolder ?? (_ => { });
        _confirmStop = confirmStop ?? (_ => true);
        _confirmRemove = confirmRemove ?? (_ => true);
        _showShortcutResult = showShortcutResult ?? ((_, _) => { });
        _desktopShortcutService = desktopShortcutService ?? new DesktopShortcutService();
        ApplicationItems.CollectionChanged += OnApplicationItemsChanged;
        _service.ApplicationStateChanged += OnApplicationStateChanged;

        AddApplicationCommand = new RelayCommand(_ => AddApplication(), _ => CanEditConfiguration);
        SelectPageCommand = new RelayCommand(page => CurrentPage = page as string ?? "Applications");
        RestoreDefaultSettingsCommand = new AsyncRelayCommand(
            RestoreDefaultsAsync,
            () => _isConfigurationLoaded && !_exitStarted);
        OpenConfigurationFolderCommand = new RelayCommand(_ => OpenConfigurationFolder());
        CreateShortcutCommand = new RelayCommand(
            parameter => CreateDesktopShortcut(parameter as ApplicationItemViewModel),
            parameter => parameter is ApplicationItemViewModel && !_exitStarted);
        SetAutoStartCommand = new RelayCommand(
            parameter => SetAutoStart(parameter is true),
            _ => IsAutoStartAvailable && !_exitStarted);
        RefreshAutoStartStatus();

        _statisticsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statisticsTimer.Tick += (_, _) => RefreshStatistics();
        _statisticsTimer.Start();
        RefreshStatistics();
        InitializationTask = LoadConfigurationAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ApplicationItemViewModel> ApplicationItems { get; } = new();

    public RelayCommand AddApplicationCommand { get; }

    public RelayCommand SelectPageCommand { get; }

    public AsyncRelayCommand RestoreDefaultSettingsCommand { get; }

    public RelayCommand OpenConfigurationFolderCommand { get; }

    /// <summary>Per-item "创建桌面快捷方式" button; the CommandParameter is the item. Never launches anything.</summary>
    public RelayCommand CreateShortcutCommand { get; }

    /// <summary>
    /// Settings "开机时自动启动": the CheckBox passes its new IsChecked as the parameter. The checkbox shows the real
    /// registry state afterwards (reverted on failure); nothing is stored in config.json.
    /// </summary>
    public RelayCommand SetAutoStartCommand { get; }

    public bool IsAutoStartAvailable => _autoStartService is not null;

    /// <summary>True when a "ProcessGuardian" Run value exists (for this or another path), as last read.</summary>
    public bool IsAutoStartEnabled => _isAutoStartEnabled;

    public string? AutoStartHint
    {
        get => _autoStartHint;
        private set
        {
            if (_autoStartHint == value) return;
            _autoStartHint = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAutoStartHint));
        }
    }

    public bool HasAutoStartHint => !string.IsNullOrWhiteSpace(AutoStartHint);

    public Task InitializationTask { get; }

    internal bool ConfigurationLoadFailed => _configurationLoadFailed;

    public Task PendingSaveTask
    {
        get
        {
            lock (_saveGate)
            {
                return _pendingSave;
            }
        }
    }

    public string ConfigurationPath => _configurationService.ConfigurationPath;

    public string? ConfigurationError
    {
        get => _configurationError;
        private set
        {
            if (_configurationError != value)
            {
                _configurationError = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasConfigurationError));
            }
        }
    }

    public string? ApplicationMessage
    {
        get => _applicationMessage;
        private set
        {
            if (_applicationMessage == value) return;
            _applicationMessage = value;
            OnPropertyChanged();
        }
    }

    public bool HasConfigurationError => !string.IsNullOrWhiteSpace(ConfigurationError);

    public bool CanEditConfiguration => _isConfigurationLoaded && !_configurationLoadFailed && !_exitStarted;

    public bool IsExiting => _exitStarted;

    public string CurrentPage
    {
        get => _currentPage;
        set
        {
            if (_currentPage == value)
            {
                return;
            }

            _currentPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsApplicationsPage));
            OnPropertyChanged(nameof(IsSettingsPage));
            OnPropertyChanged(nameof(IsAboutPage));
            OnPropertyChanged(nameof(IsLogsPage));
            OnPropertyChanged(nameof(IsCompatibilityPage));
            if (IsLogsPage && Diagnostics is { } diagnostics)
            {
                _ = diagnostics.RefreshAsync();
            }

            if (IsSettingsPage)
            {
                RefreshAutoStartStatus();
            }
        }
    }

    /// <summary>
    /// Re-reads the Run value and updates the checkbox and hint. Always raises <see cref="IsAutoStartEnabled"/> so a
    /// checkbox the user just toggled snaps back to the real state.
    /// </summary>
    public void RefreshAutoStartStatus()
    {
        if (_autoStartService is null)
        {
            _isAutoStartEnabled = false;
            OnPropertyChanged(nameof(IsAutoStartEnabled));
            AutoStartHint = null;
            return;
        }

        var status = _autoStartService.GetStatus();
        _isAutoStartEnabled = status.IsRegistered;
        OnPropertyChanged(nameof(IsAutoStartEnabled));
        AutoStartHint = DescribeAutoStartHint(status);
    }

    /// <summary>Enables or disables start with Windows through the service, then shows the real state.</summary>
    internal AutoStartOperationResult? SetAutoStart(bool enable)
    {
        if (_autoStartService is null || _exitStarted)
        {
            RefreshAutoStartStatus();
            return null;
        }

        var result = enable ? _autoStartService.Enable() : _autoStartService.Disable();
        RefreshAutoStartStatus();
        if (!result.Succeeded)
        {
            _showAutoStartError(
                (enable ? "无法开启开机自动启动。" : "无法关闭开机自动启动。") +
                "\n\n原因：" + (result.Error ?? "未知错误") +
                "\n\n复选框已恢复为当前实际状态。");
        }

        return result;
    }

    private string? DescribeAutoStartHint(AutoStartStatus status)
    {
        if (status.Error is not null)
        {
            return "无法读取开机自动启动状态：" + status.Error;
        }

        var hints = new List<string>();
        if (status.State == AutoStartState.EnabledDifferentPath)
        {
            // Show the registered path; when only the arguments differ (same path), show the whole command instead.
            var shown = string.IsNullOrWhiteSpace(status.RegisteredExecutablePath) ||
                string.Equals(status.RegisteredExecutablePath, _autoStartService?.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                    ? status.RegisteredCommand
                    : status.RegisteredExecutablePath;
            hints.Add($"启动项路径与当前程序不一致：{shown}，重新勾选以更新");
        }

        if (status.IsRegistered && status.DisabledInTaskManager)
        {
            hints.Add("该启动项已在任务管理器的“启动”页中被禁用，登录 Windows 时不会运行；请在任务管理器中重新启用。");
        }

        return hints.Count == 0 ? null : string.Join("\n", hints);
    }

    public bool IsApplicationsPage => CurrentPage == "Applications";

    public bool IsSettingsPage => CurrentPage == "Settings";

    public bool IsAboutPage => CurrentPage == "About";

    public bool IsLogsPage => CurrentPage == "Logs";

    public bool IsCompatibilityPage => CurrentPage == "Compatibility";

    public DiagnosticsViewModel? Diagnostics { get; set; }

    public int RunningSoftwareCount
    {
        get => _runningSoftwareCount;
        private set
        {
            if (_runningSoftwareCount != value)
            {
                _runningSoftwareCount = value;
                OnPropertyChanged();
            }
        }
    }

    public int ManagedProcessCount
    {
        get => _managedProcessCount;
        private set
        {
            if (_managedProcessCount != value)
            {
                _managedProcessCount = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsApplicationListEmpty => ApplicationItems.Count == 0;

    public void Dispose()
    {
        StopObserving();
        _service.Dispose();
    }

    public async Task DisposeAsync()
    {
        StopObserving();
        await InitializationTask;
        await PendingSaveTask;
        await Task.Run(_service.Dispose);
    }

    public void BeginExit()
    {
        if (_exitStarted)
        {
            return;
        }

        _exitStarted = true;
        foreach (var item in ApplicationItems)
        {
            item.BeginExit();
        }

        OnPropertyChanged(nameof(IsExiting));
        OnPropertyChanged(nameof(CanEditConfiguration));
        AddApplicationCommand.RaiseCanExecuteChanged();
        RestoreDefaultSettingsCommand.RaiseCanExecuteChanged();
        CreateShortcutCommand.RaiseCanExecuteChanged();
        SetAutoStartCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Command-line launch (--launch): selects one configured entry by id or exact name and starts it through the
    /// same LaunchCommand as the Launch button. Never launches anything that is not already a configured entry,
    /// never picks one of several matches, and never starts a second instance of a running entry.
    /// </summary>
    internal async Task<LaunchRequestResult> RequestLaunchAsync(string selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        await InitializationTask;
        var shown = CommandProtocol.Echo(selector.Trim());
        if (_configurationLoadFailed)
        {
            return new LaunchRequestResult(
                CommandReplyCode.ConfigUnavailable,
                $"The configuration could not be loaded, so \"{shown}\" cannot be launched. {ConfigurationError}".TrimEnd());
        }

        if (_exitStarted)
        {
            return new LaunchRequestResult(CommandReplyCode.Exiting, "Process Guardian is exiting; the request was not processed.");
        }

        var selection = ApplicationSelector.Select(ApplicationItems, item => item.Id, item => item.SoftwareName, selector);
        switch (selection.Status)
        {
            case ApplicationSelectionStatus.NotFound:
                return new LaunchRequestResult(
                    CommandReplyCode.NotFound,
                    selection.SelectedById
                        ? $"No configured application has the id {shown}."
                        : $"No configured application is named \"{shown}\". The name must match exactly (case-insensitive).");
            case ApplicationSelectionStatus.Ambiguous:
                return new LaunchRequestResult(
                    CommandReplyCode.Ambiguous,
                    $"{selection.MatchCount} configured applications are named \"{shown}\"; none was launched. Rename them or use the entry id.");
        }

        var target = selection.Item!;
        var refusal = GetLaunchRefusal(target);
        if (refusal is not null)
        {
            return refusal;
        }

        if (_externalInstanceDetector is not null)
        {
            var external = await target.FindExternalInstancesAsync();
            if (external.Found)
            {
                target.RefuseExternalInstance(external);
                return new LaunchRequestResult(
                    CommandReplyCode.ExternalInstanceRunning,
                    ExternalInstanceMessages.Format(target.SoftwareName, external.ProcessIds),
                    external.ProcessIds[0],
                    Item: target);
            }

            // The scan was asynchronous: re-check the entry before starting it.
            if (_exitStarted)
            {
                return new LaunchRequestResult(CommandReplyCode.Exiting, "Process Guardian is exiting; the request was not processed.");
            }

            refusal = GetLaunchRefusal(target);
            if (refusal is not null)
            {
                return refusal;
            }

            target.MarkExternalInstanceChecked();
        }

        var launchTask = target.LaunchCommand.ExecuteAsync();
        return new LaunchRequestResult(
            CommandReplyCode.Started,
            $"Launching \"{target.SoftwareName}\".",
            LaunchTask: launchTask,
            Item: target);
    }

    private LaunchRequestResult? GetLaunchRefusal(ApplicationItemViewModel target)
    {
        if (!target.CanLaunch || !target.LaunchCommand.CanExecute(null))
        {
            if (target.IsActive)
            {
                var processId = target.ProcessId;
                var windowHandle = target.ManagedApplication?.WindowMonitor?.MainWindowHandle ?? IntPtr.Zero;
                return new LaunchRequestResult(
                    CommandReplyCode.AlreadyRunning,
                    processId == 0
                        ? $"\"{target.SoftwareName}\" is already starting."
                        : $"\"{target.SoftwareName}\" is already running (PID {processId}).",
                    processId,
                    windowHandle.ToInt64());
            }

            var reason = target.IsExecutableMissing
                ? $"the executable was not found: {target.ExecutablePath}"
                : target.HasInvalidWorkingDirectory
                    ? $"the working directory does not exist: {target.WorkingDirectory}"
                    : _exitStarted
                        ? "Process Guardian is exiting"
                        : "another operation on this entry is in progress";
            return new LaunchRequestResult(CommandReplyCode.NotLaunchable, $"\"{target.SoftwareName}\" cannot be launched: {reason}.");
        }

        return null;
    }

    public async Task WaitForOperationsAsync()
    {
        while (true)
        {
            var pending = ApplicationItems
                .SelectMany(item => item.GetPendingOperations())
                .Append(RestoreDefaultSettingsCommand.ExecutionTask)
                .Where(task => !task.IsCompleted)
                .Distinct()
                .ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending);
        }
    }

    /// <summary>
    /// Creates "&lt;name&gt;.lnk" (target: this exe, arguments: --launch "&lt;name&gt;") through
    /// <see cref="DesktopShortcutService"/> and shows the outcome. The shortcut goes through the existing --launch
    /// path when double-clicked; nothing is launched here and an existing file is never overwritten.
    /// </summary>
    internal DesktopShortcutResult? CreateDesktopShortcut(ApplicationItemViewModel? item)
    {
        if (item is null || _exitStarted)
        {
            return null;
        }

        var result = _desktopShortcutService.Create(
            item.SoftwareName,
            item.ExecutablePath,
            ApplicationItems.Select(candidate => candidate.SoftwareName).ToArray());
        var (text, isError) = DesktopShortcutMessages.Format(
            result,
            item.SoftwareName,
            Environment.GetEnvironmentVariable(AppDataPaths.OverrideVariable));
        _showShortcutResult(text, isError);
        return result;
    }

    private void AddApplication()
    {
        var executablePath = _filePicker.SelectExecutable();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(executablePath);
        var configuration = new ApplicationConfiguration
        {
            Id = Guid.NewGuid(),
            SoftwareName = Path.GetFileNameWithoutExtension(fullPath),
            ExecutablePath = fullPath,
            AddedAt = DateTimeOffset.UtcNow,
        };
        var item = CreateItem(configuration);
        ApplicationItems.Add(item);
        AppLog.Write(LogLevel.Information, "Applications", "Application configuration added", configuration.SoftwareName);
        ApplicationMessage = $"Added {configuration.SoftwareName}. It has not been launched.";
        RefreshStatistics();
    }

    private void RemoveItem(ApplicationItemViewModel item)
    {
        AppLog.Write(LogLevel.Information, "Applications", "Application configuration removed", item.SoftwareName);
        ApplicationMessage = $"Removed {item.SoftwareName} from the list. Its managed Job was cleaned first if it was running.";
        ApplicationItems.Remove(item);
    }

    private void OnApplicationItemsChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.OldItems is not null)
        {
            foreach (ApplicationItemViewModel item in eventArgs.OldItems)
            {
                item.PropertyChanged -= OnItemPropertyChanged;
            }
        }

        if (eventArgs.NewItems is not null)
        {
            foreach (ApplicationItemViewModel item in eventArgs.NewItems)
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }

        OnPropertyChanged(nameof(IsApplicationListEmpty));
        RefreshStatistics();
        if (!_isLoadingConfiguration)
        {
            _ = SaveConfigurationAsync();
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(ApplicationItemViewModel.IsActive)
            or nameof(ApplicationItemViewModel.ManagedApplication)
            or nameof(ApplicationItemViewModel.ProcessId))
        {
            RefreshStatistics();
        }

        if (sender is ApplicationItemViewModel item && eventArgs.PropertyName is nameof(ApplicationItemViewModel.SoftwareName)
            or nameof(ApplicationItemViewModel.Arguments) or nameof(ApplicationItemViewModel.WorkingDirectory))
        {
            AppLog.Write(LogLevel.Information, "Applications", $"Application configuration changed: {eventArgs.PropertyName}", item.SoftwareName);
        }
    }

    private void OnApplicationStateChanged(object? sender, ManagedApplicationStateChangedEventArgs eventArgs)
    {
        var item = ApplicationItems.FirstOrDefault(candidate => candidate.ManagedApplication?.Id == eventArgs.Application.Id);
        item?.ApplyState(eventArgs.Application);
    }

    private void RefreshStatistics()
    {
        foreach (var item in ApplicationItems)
        {
            item.RefreshRuntimeInformation();
        }

        var activeItems = ApplicationItems.Where(item => item.IsActive).ToArray();
        RunningSoftwareCount = activeItems.Length;

        var processCount = 0;
        foreach (var item in activeItems)
        {
            var launchResult = item.ManagedApplication?.LaunchResult;
            if (launchResult is null)
            {
                continue;
            }

            try
            {
                processCount += checked((int)launchResult.JobObject.ActiveProcessCount);
            }
            catch (ObjectDisposedException)
            {
                // A completed app can race with this periodic count refresh.
            }
        }

        ManagedProcessCount = processCount;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void StopObserving()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statisticsTimer.Stop();
        _service.ApplicationStateChanged -= OnApplicationStateChanged;
        ApplicationItems.CollectionChanged -= OnApplicationItemsChanged;
        foreach (var item in ApplicationItems)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
    }

    private async Task LoadConfigurationAsync()
    {
        var loadFailed = false;
        _isLoadingConfiguration = true;
        try
        {
            var result = await _configurationService.LoadAsync();
            if (result.Error is not null)
            {
                loadFailed = true;
                AppLog.Write(LogLevel.Error, "Configuration", "Configuration could not be loaded", exception: result.Error);
                ConfigurationError = result.FailureStage == ConfigurationLoadFailureStage.CreateDefault
                    ? $"Could not create the default configuration at {ConfigurationPath}: {result.Error.GetType().Name}: {result.Error.Message}. Check folder permissions and free space. Process Guardian did not request administrator access."
                    : $"Could not read configuration at {ConfigurationPath}: {result.Error.GetType().Name}: {result.Error.Message}. The existing file was not overwritten; check its permissions or repair it before editing the list.";
                return;
            }

            foreach (var configuration in result.Applications)
            {
                ApplicationItems.Add(CreateItem(configuration));
            }
            AppLog.Write(LogLevel.Information, "Configuration", $"Configuration loaded into UI ({result.Applications.Count} entries)");
        }
        catch (Exception exception)
        {
            loadFailed = true;
            AppLog.Write(LogLevel.Error, "Configuration", "Configuration initialization failed", exception: exception);
            ConfigurationError = $"Could not initialize configuration at {ConfigurationPath}: {exception.GetType().Name}: {exception.Message}. Check folder permissions and free space.";
        }
        finally
        {
            _isLoadingConfiguration = false;
            _isConfigurationLoaded = true;
            _configurationLoadFailed = loadFailed;
            OnPropertyChanged(nameof(CanEditConfiguration));
            AddApplicationCommand.RaiseCanExecuteChanged();
            RestoreDefaultSettingsCommand.RaiseCanExecuteChanged();
            RefreshStatistics();
        }
    }

    private ApplicationItemViewModel CreateItem(ApplicationConfiguration configuration) =>
        new(configuration, _service, RemoveItem, OnItemConfigurationChanged, _confirmStop, _confirmRemove,
            _externalInstanceDetector, GetManagedProcessIds, _showExternalInstanceWarning);

    /// <summary>
    /// PIDs that belong to this Process Guardian: the primary process and every live Job member of each entry that is
    /// not finished. They are never reported as external instances (a running entry stays AlreadyRunning).
    /// </summary>
    internal IReadOnlyCollection<uint> GetManagedProcessIds()
    {
        var result = new HashSet<uint>();
        foreach (var item in ApplicationItems)
        {
            var application = item.ManagedApplication;
            if (application is null || application.Status is ManagedApplicationStatus.Completed or ManagedApplicationStatus.Failed)
            {
                continue;
            }

            if (application.ProcessId != 0)
            {
                result.Add(application.ProcessId);
            }

            var job = application.LaunchResult?.JobObject;
            if (job is null)
            {
                continue;
            }

            try
            {
                foreach (var processId in job.GetProcessIds())
                {
                    result.Add(processId);
                }
            }
            catch (Exception exception)
            {
                AppLog.Write(LogLevel.Debug, "ExternalInstance", $"Job members could not be listed ({exception.GetType().Name}); only the primary PID is excluded", item.SoftwareName);
            }
        }

        return result;
    }

    private void OnItemConfigurationChanged() => _ = SaveConfigurationAsync();

    private Task SaveConfigurationAsync()
    {
        var snapshot = ApplicationItems.Select(item => item.ToConfiguration()).ToArray();
        lock (_saveGate)
        {
            _pendingSave = SaveAfterPreviousAsync(_pendingSave, snapshot);
            return _pendingSave;
        }
    }

    private async Task SaveAfterPreviousAsync(Task previousSave, IReadOnlyList<ApplicationConfiguration> snapshot)
    {
        try
        {
            await previousSave;
            await _configurationService.SaveAsync(snapshot).ConfigureAwait(false);
            SetConfigurationError(null);
        }
        catch (Exception exception)
        {
            AppLog.Write(LogLevel.Error, "Configuration", "Configuration save failed", exception: exception);
            SetConfigurationError($"Could not save configuration at {ConfigurationPath}: {exception.GetType().Name}: {exception.Message}. The existing file is replaced only after a temporary file is written successfully; check folder permissions and free space.");
        }
    }

    private void SetConfigurationError(string? error)
    {
        if (_uiSynchronizationContext is null || SynchronizationContext.Current == _uiSynchronizationContext)
        {
            ConfigurationError = error;
            return;
        }

        _uiSynchronizationContext?.Post(_ => ConfigurationError = error, null);
    }

    private void OpenConfigurationFolder()
    {
        var folder = Path.GetDirectoryName(ConfigurationPath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            try
            {
                Directory.CreateDirectory(folder);
                _openFolder(folder);
                ConfigurationError = null;
            }
            catch (Exception exception)
            {
                ConfigurationError = $"Could not open configuration folder for {ConfigurationPath}: {exception.GetType().Name}: {exception.Message}. Check folder permissions.";
            }
        }
    }

    private async Task RestoreDefaultsAsync()
    {
        if (!_confirmRestoreDefaults())
        {
            return;
        }

        _configurationLoadFailed = false;
        OnPropertyChanged(nameof(CanEditConfiguration));
        AddApplicationCommand.RaiseCanExecuteChanged();

        foreach (var item in ApplicationItems.ToArray())
        {
            await item.RemoveCommand.ExecuteAsync();
        }

        await SaveConfigurationAsync();
    }
}
