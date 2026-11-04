using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ProcessGuardian.Services.ApplicationLifecycle;
using ProcessGuardian.Services.CommandLine;
using ProcessGuardian.ViewModels;
using ProcessGuardian.Services.Logging;
using System.Diagnostics;

namespace ProcessGuardian;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan FatalTrayDisposeTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CommandServerStopTimeout = TimeSpan.FromSeconds(3);

    private MainWindow? _mainWindow;
    private MainViewModel? _viewModel;
    private ITrayIconService? _trayIcon;
    private TrayCommandRouter? _trayRouter;
    private ApplicationExitCoordinator? _exitCoordinator;
    private FileLogService? _logger;
    private SingleInstanceMutex? _instanceMutex;
    private CommandPipeServer? _commandServer;
    private readonly AppUserModelIdResult _appUserModelId;

    public App()
    {
        // HANDOVER 7.18: give the process an explicit AppUserModelID before any window exists, so the taskbar shows
        // Process Guardian's own identity and icon instead of those of the .lnk that launched it (a generated desktop
        // shortcut carries the managed application's icon). The generated Main constructs App before anything else,
        // and an instance constructor (not a static one) keeps test code that touches App's statics unaffected.
        // Apply never throws; a failure is traced now and logged in OnStartup once the logger exists.
        _appUserModelId = AppUserModelIdentity.Apply();
        if (!_appUserModelId.Succeeded)
        {
            Trace.WriteLine("Process Guardian: " + _appUserModelId.Describe());
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Registered before anything else, so a failure before the logger exists is still reported (stage 12, F1).
        // Report-only: these handlers never mark an exception handled and never rethrow, so crash behavior is unchanged.
        DispatcherUnhandledException += (_, args) => ReportFatal("UI-thread", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportFatal(args.IsTerminating ? "terminating" : "non-terminating", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += LogUnobservedTaskException;

        try
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Command line (--launch) and single instance: parsed before anything touches a file. A usage error or a
            // forwarding second instance never creates the logger, the main window, the ViewModel or the configuration.
            var parsed = CommandLineParser.Parse(e.Args);
            if (!parsed.IsValid)
            {
                ShowCommandLineError(parsed.Error + "\n\n" + CommandLineParser.UsageText, ForwardResultPresenter.Title + " - invalid command line");
                Shutdown(CommandLineExitCodes.UsageError);
                return;
            }

            var options = parsed.Options!;
            var instanceNames = SingleInstanceNames.ForCurrentProcess();
            _instanceMutex = SingleInstanceMutex.Acquire(instanceNames.MutexName);
            if (!_instanceMutex.IsOwner)
            {
                if (options.IsAutoStart)
                {
                    // --autostart (Windows sign-in) while Process Guardian already runs: exit quietly. Nothing is
                    // forwarded (the pipe protocol is unchanged) and no window is brought up.
                    System.Diagnostics.Trace.WriteLine("Process Guardian --autostart: another instance is already running; exiting quietly.");
                    Shutdown(CommandLineExitCodes.Success);
                    return;
                }

                _ = ForwardToRunningInstanceAsync(options, instanceNames.PipeName);
                return;
            }
#if PG_FAULT_INJECTION
            StartupFaultInjection.ThrowIfRequested(StartupFaultInjection.BeforeLogger);
#endif

            _logger = new FileLogService();
            AppLog.Initialize(_logger);
            AppLog.Write(LogLevel.Information, "Application", "Process Guardian started");
            if (_instanceMutex.WasAbandoned)
            {
                AppLog.Write(LogLevel.Warning, CommandPipeServer.LogModule, "A previous Process Guardian instance for this data folder ended without releasing its single-instance mutex");
            }
            AppLog.Write(
                _appUserModelId.Succeeded ? LogLevel.Information : LogLevel.Warning,
                "Application",
                _appUserModelId.Describe(),
                exception: _appUserModelId.Exception);
#if PG_FAULT_INJECTION
            StartupFaultInjection.ThrowIfRequested(StartupFaultInjection.AfterLogger);
#endif

            _mainWindow = new MainWindow();
            _viewModel = _mainWindow.ViewModel;
            MainWindow = _mainWindow;

            var viewModel = _viewModel;
            _exitCoordinator = new ApplicationExitCoordinator(
                stopAcceptingOperations: () =>
                {
                    _commandServer?.BeginStop();
                    viewModel.BeginExit();
                },
                waitForOperations: _viewModel.WaitForOperationsAsync,
                cleanup: CleanupAsync,
                shutdown: Shutdown);
            var trayStartup = TrayStartup.Configure(
                _mainWindow,
                CreateTrayIcon,
                ShowTrayUnavailableMessage,
                _exitCoordinator.ExitAsync,
                openWindow: () => ShowMainWindow(),
                openApplications: () => ShowMainWindow("Applications"));
            _trayIcon = trayStartup.TrayIcon;
            _trayRouter = trayStartup.Router;
#if PG_FAULT_INJECTION
            StartupFaultInjection.ThrowIfRequested(StartupFaultInjection.BeforeShow);
#endif

            _commandServer = new CommandPipeServer(instanceNames.PipeName, HandleForwardedRequestAsync);
            _commandServer.Start();

            // A cold start with --launch or --autostart stays in the tray; without a tray the window is the only UI,
            // so it is shown.
            var startHidden = StartupWindowPolicy.ShouldStartHidden(options, trayStartup.TrayAvailable);
            if (!startHidden)
            {
                _mainWindow.Show();
            }

            if (options.IsAutoStart)
            {
                _ = RunStartupAutoStartAsync(startHidden);
            }

            if (options.HasLaunch)
            {
                _ = RunStartupLaunchAsync(options.LaunchSelector!);
            }
#if PG_FAULT_INJECTION
            StartupFaultInjection.SchedulePostStartupFaults(Dispatcher);
#endif
        }
        catch (Exception exception)
        {
            // Report (log or fallback file, tray removal, one message box), then rethrow so the process still crashes
            // as before. The handlers above see the same exception object and do not report it a second time.
            ReportFatal("startup", exception);
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            base.OnExit(e);
        }
        finally
        {
            // Released only now, after cleanup has completed, on the thread that acquired it.
            _instanceMutex?.Dispose();
            _instanceMutex = null;
        }
    }

    /// <summary>Second instance: send one request to the first instance, report the outcome, exit with its code.</summary>
    private async Task ForwardToRunningInstanceAsync(CommandLineOptions options, string pipeName)
    {
        int exitCode;
        try
        {
            var request = options.HasLaunch ? CommandRequest.Launch(options.LaunchSelector!) : CommandRequest.Show();
            var result = await CommandPipeClient.SendAsync(pipeName, request, CommandPipeClient.ConnectTimeout, CommandPipeClient.ReplyTimeout);
            exitCode = ForwardResultPresenter.Present(
                result,
                ShowCommandLineInformation,
                ShowCommandLineError,
                TrackedWindowActivator.TryBringToForeground);
        }
        catch (Exception exception)
        {
            ShowCommandLineError(
                $"Process Guardian could not forward the request: {exception.GetType().Name}: {exception.Message}",
                ForwardResultPresenter.Title);
            exitCode = CommandLineExitCodes.Unreachable;
        }

        Shutdown(exitCode);
    }

    /// <summary>Runs on a pipe-server thread; the request itself is handled on the UI thread.</summary>
    private async Task<CommandReply> HandleForwardedRequestAsync(CommandRequest request, CancellationToken stopToken)
    {
        var exiting = CommandReply.Create(CommandReplyCode.Exiting, "Process Guardian is exiting; the request was not processed.");
        var dispatcher = Dispatcher;
        if (stopToken.IsCancellationRequested || dispatcher.HasShutdownStarted)
        {
            return exiting;
        }

        try
        {
            var operation = dispatcher.InvokeAsync(() => HandleForwardedRequestOnUiThreadAsync(request));
            return await (await operation.Task.ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The dispatcher aborted the operation because shutdown began.
            return exiting;
        }
    }

    private async Task<CommandReply> HandleForwardedRequestOnUiThreadAsync(CommandRequest request)
    {
        if (_viewModel is null || _mainWindow is null || _exitCoordinator?.ExitRequested == true)
        {
            return CommandReply.Create(CommandReplyCode.Exiting, "Process Guardian is exiting; the request was not processed.");
        }

        if (request.Command == CommandRequest.ShowCommandName)
        {
            ShowMainWindow();
            var handle = new WindowInteropHelper(_mainWindow).Handle;
            return CommandReply.Create(CommandReplyCode.Shown, "The Process Guardian window was shown.", handle.ToInt64());
        }

        // Only "launch" passes the protocol parser besides "show". The window of this instance is left unchanged.
        var result = await _viewModel.RequestLaunchAsync(request.Selector!);
        return result.ToReply();
    }

    /// <summary>
    /// Cold start with --launch: launch in-process once the configuration is loaded. Any refusal (or a launch that
    /// began but failed) opens the main window; a refusal also shows its own message box.
    /// </summary>
    private async Task RunStartupLaunchAsync(string selector)
    {
        var viewModel = _viewModel!;
        LaunchRequestResult result;
        try
        {
            result = await viewModel.RequestLaunchAsync(selector);
        }
        catch (Exception exception)
        {
            AppLog.Write(LogLevel.Error, CommandPipeServer.LogModule, "The startup --launch request failed", exception: exception);
            result = new LaunchRequestResult(
                CommandReplyCode.NotLaunchable,
                $"Process Guardian could not handle --launch: {exception.GetType().Name}: {exception.Message}");
        }

        AppLog.Write(
            result.Code == CommandReplyCode.Started ? LogLevel.Information : LogLevel.Warning,
            CommandPipeServer.LogModule,
            $"Startup --launch selector \"{CommandProtocol.Echo(selector)}\": {result.Code} - {result.Message}");

        if (result.Code == CommandReplyCode.Started)
        {
            if (result.LaunchTask is not null)
            {
                await result.LaunchTask;
            }

            if (result.Item is { } item && (item.HasError || item.ManagedApplication is null))
            {
                AppLog.Write(LogLevel.Warning, CommandPipeServer.LogModule, $"Startup --launch of \"{item.SoftwareName}\" did not start cleanly; showing the main window");
                ShowMainWindow();
            }

            return;
        }

        ShowMainWindow();
        if (result.Code == CommandReplyCode.Exiting)
        {
            return;
        }

        if (result.Code == CommandReplyCode.AlreadyRunning)
        {
            TrackedWindowActivator.TryBringToForeground(result.WindowHandle);
            ShowCommandLineInformation(result.Message, ForwardResultPresenter.Title);
            TrackedWindowActivator.TryBringToForeground(result.WindowHandle);
            return;
        }

        ShowCommandLineError(result.Message, ForwardResultPresenter.Title + " - " + result.Code);
    }

    /// <summary>
    /// Cold start with --autostart: never launches an entry. Once the configuration has loaded, a failed load opens
    /// the main window so the error is visible; otherwise Process Guardian stays in the tray.
    /// </summary>
    private async Task RunStartupAutoStartAsync(bool startedHidden)
    {
        var viewModel = _viewModel!;
        try
        {
            await viewModel.InitializationTask;
        }
        catch (Exception exception)
        {
            AppLog.Write(LogLevel.Error, "Application", "Startup --autostart: waiting for the configuration failed", exception: exception);
        }

        if (viewModel.ConfigurationLoadFailed)
        {
            AppLog.Write(LogLevel.Warning, "Application", "Startup --autostart: the configuration could not be loaded; showing the main window");
            ShowMainWindow();
            return;
        }

        AppLog.Write(
            LogLevel.Information,
            "Application",
            startedHidden
                ? "Startup --autostart: configuration loaded; staying in the tray"
                : "Startup --autostart: configuration loaded; the main window is shown because the tray icon is unavailable");
    }

    private static void ShowCommandLineInformation(string text, string title) =>
        System.Windows.MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private static void ShowCommandLineError(string text, string title) =>
        System.Windows.MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private ITrayIconService CreateTrayIcon()
    {
#if PG_FAULT_INJECTION
        StartupFaultInjection.ThrowIfRequested(StartupFaultInjection.TrayCreate);
#endif
        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/ProcessGuardian.ico"))?.Stream
            ?? throw new InvalidOperationException("The Process Guardian tray icon resource could not be loaded.");
        using var appIcon = new System.Drawing.Icon(iconStream);
        return new TrayIconService(appIcon);
    }

    private static void ShowTrayUnavailableMessage(Exception exception) =>
        System.Windows.MessageBox.Show(
            TrayStartup.TrayUnavailableMessage + "\n\n" + exception.GetType().Name + ": " + exception.Message,
            "Process Guardian - tray icon unavailable",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

    private void ReportFatal(string kind, Exception? exception)
    {
        // Never sets Handled and never rethrows: reporting must not change how the exception is processed.
        try
        {
            FatalErrorReporter.Report(kind, exception, DisposeTrayForFatalError, ShowFatalMessage);
        }
        catch (Exception reportingException)
        {
            Trace.WriteLine("Process Guardian could not report a fatal exception: " + reportingException);
        }
    }

    private static void ShowFatalMessage(string text, string title) =>
        System.Windows.MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Error);

    /// <summary>
    /// F5: removes the tray icon so a crash does not leave a dead icon behind. NotifyIcon belongs to the UI thread,
    /// so another thread marshals the disposal there with a bounded wait. Only the tray is released: managed
    /// applications and their Job Objects are left to the operating system exactly as before.
    /// </summary>
    private void DisposeTrayForFatalError()
    {
        if (_trayIcon is null && _trayRouter is null)
        {
            return;
        }

        void DisposeTray()
        {
            _trayRouter?.Dispose();
            _trayIcon?.Dispose();
        }

        var dispatcher = Dispatcher;
        if (dispatcher.CheckAccess())
        {
            DisposeTray();
            return;
        }

        if (dispatcher.HasShutdownStarted)
        {
            throw new InvalidOperationException("The UI dispatcher is shutting down; the tray icon could not be removed.");
        }

        var operation = dispatcher.InvokeAsync(DisposeTray, DispatcherPriority.Send);
        if (operation.Wait(FatalTrayDisposeTimeout) != DispatcherOperationStatus.Completed)
        {
            operation.Abort();
            throw new TimeoutException($"The UI thread did not remove the tray icon within {FatalTrayDisposeTimeout}.");
        }

        if (operation.Task.Exception is { } failure)
        {
            throw failure;
        }
    }

    /// <summary>F4: log-only. SetObserved is not called: it would not change process behavior on .NET and the
    /// error should stay visible as unobserved.</summary>
    internal static void LogUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args) =>
        AppLog.Write(LogLevel.Error, "Application", "Unobserved task exception", exception: args.Exception);

    private void ShowMainWindow(string? page = null)
    {
        if (_mainWindow is null || _viewModel is null || _exitCoordinator?.ExitRequested == true)
        {
            return;
        }

        if (page is not null)
        {
            _viewModel.CurrentPage = page;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private async Task CleanupAsync()
    {
        await StopCommandServerAsync();
        try
        {
            if (_viewModel is not null)
            {
                await _viewModel.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            AppLog.Write(LogLevel.Critical, "Application", "Application cleanup failed", exception: exception);
            throw;
        }
        finally
        {
            _trayRouter?.Dispose();
            _trayIcon?.Dispose();
            _mainWindow?.AllowApplicationClose();
            AppLog.Write(LogLevel.Information, "Application", "Process Guardian shutdown cleanup completed");
            if (_logger is not null)
            {
                try
                {
                    await _logger.FlushAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (Exception exception)
                {
                    Trace.WriteLine("Process Guardian log flush did not complete before shutdown: " + exception);
                }

                try
                {
                    await _logger.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (Exception exception)
                {
                    Trace.WriteLine("Process Guardian logger disposal did not complete before shutdown: " + exception);
                }
            }
        }
    }

    /// <summary>Bounded: a pipe server that does not stop in time is logged and left to process exit.</summary>
    private async Task StopCommandServerAsync()
    {
        if (_commandServer is null)
        {
            return;
        }

        try
        {
            await _commandServer.StopAsync(CommandServerStopTimeout);
            AppLog.Write(LogLevel.Information, CommandPipeServer.LogModule, "Command pipe server stopped");
        }
        catch (TimeoutException exception)
        {
            AppLog.Write(LogLevel.Warning, CommandPipeServer.LogModule, $"The command pipe server did not stop within {CommandServerStopTimeout}; continuing shutdown", exception: exception);
        }
    }
}
