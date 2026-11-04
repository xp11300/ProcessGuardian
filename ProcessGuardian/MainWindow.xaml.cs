using System.Windows;
using System.Windows.Media;
using System.Diagnostics;
using ProcessGuardian.Services.ManagedApplications;
using ProcessGuardian.Services.Configuration;
using ProcessGuardian.ViewModels;
using ProcessGuardian.Services.Logging;

namespace ProcessGuardian;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;
    private Func<Task>? _noTrayExit;

    public MainWindow(ConfigurationService? configurationService = null, IExecutableFilePicker? filePicker = null)
    {
        InitializeComponent();
#if PG_FAULT_INJECTION
        StartupFaultInjection.ThrowIfRequested(StartupFaultInjection.MainWindowInit);
#endif

        // Keep the initial window inside the usable desktop area on high-DPI and
        // smaller displays while preserving the full-size layout on typical screens.
        var workArea = SystemParameters.WorkArea;
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var availableWidth = workArea.Width / dpiScale;
        var availableHeight = workArea.Height / dpiScale;
        Width = Math.Min(820, availableWidth * 0.94);
        Height = Math.Min(540, availableHeight * 0.92);
        MinWidth = Math.Min(700, availableWidth * 0.84);
        MinHeight = Math.Min(480, availableHeight * 0.84);
        Left = Math.Max(0, (availableWidth - Width) / 2);
        Top = Math.Max(0, (availableHeight - Height) / 2);

        var service = new ManagedApplicationService(synchronizationContext: SynchronizationContext.Current);
        _viewModel = new MainViewModel(
            service,
            filePicker ?? new WpfExecutableFilePicker(this),
            configurationService,
            confirmRestoreDefaults: () => System.Windows.MessageBox.Show(
                this,
                "Restore default settings? This removes all saved applications and stops any that are running.",
                "Restore default settings",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes,
            openFolder: folder => Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }),
            confirmStop: name => System.Windows.MessageBox.Show(
                this,
                $"Stop {name}? Process Guardian will close only this application's Job Object. Processes outside that Job are not affected.",
                "Stop managed application",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes,
            confirmRemove: name => System.Windows.MessageBox.Show(
                this,
                $"Remove {name} from the list? If it is running, its own Job Object will be closed first.",
                "Remove application",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes,
            showShortcutResult: (text, isError) => System.Windows.MessageBox.Show(
                this,
                text,
                ProcessGuardian.Services.DesktopShortcuts.DesktopShortcutMessages.Title,
                MessageBoxButton.OK,
                isError ? MessageBoxImage.Warning : MessageBoxImage.Information),
            externalInstanceDetector: new ProcessGuardian.Services.ExternalInstances.ExternalInstanceDetector(),
            showExternalInstanceWarning: text => System.Windows.MessageBox.Show(
                this,
                text,
                ProcessGuardian.Services.ExternalInstances.ExternalInstanceMessages.Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning),
            // Per-user Run key (HKCU only); written only when the user toggles the Settings checkbox.
            autoStartService: new ProcessGuardian.Services.Startup.AutoStartService(),
            showAutoStartError: text => System.Windows.MessageBox.Show(
                this,
                text,
                "开机自动启动",
                MessageBoxButton.OK,
                MessageBoxImage.Warning));
        // Showing the window again (e.g. from the tray) while Settings is open re-reads the real registry state.
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true && _viewModel.IsSettingsPage)
            {
                _viewModel.RefreshAutoStartStatus();
            }
        };
        if (AppLog.Current is { } logger)
        {
            _viewModel.Diagnostics = new DiagnosticsViewModel(
                logger,
                folder => Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }),
                () => System.Windows.MessageBox.Show(this, "Clear all saved log files?", "Clear logs", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
        }
        DataContext = _viewModel;
#if PG_FAULT_INJECTION
        StartupFaultInjection.ThrowIfRequested(StartupFaultInjection.MainWindowAfterViewModel);
#endif
    }

    public MainViewModel ViewModel => _viewModel;

    public void AllowApplicationClose() => _allowClose = true;

    /// <summary>True only after the tray icon could not be created (stage-12 policy B).</summary>
    internal bool IsNoTrayMode => _noTrayExit is not null;

    /// <summary>
    /// No-tray mode (tray creation failed): the close button runs <paramref name="exit"/> (the existing
    /// ApplicationExitCoordinator.ExitAsync, the same path as tray Exit) instead of hiding, and minimize keeps the
    /// window in the taskbar. When the tray exists this is never called and behavior is unchanged.
    /// </summary>
    internal void EnableNoTrayMode(Func<Task> exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        _noTrayExit = exit;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_noTrayExit is { } exit)
        {
            // Run after this Closing handler returns (as a tray Exit click would), so Shutdown never closes the
            // window from inside its own Closing event. Cleanup calls AllowApplicationClose, then Shutdown closes
            // the window for real.
            _ = Dispatcher.InvokeAsync(() => RunNoTrayExitAsync(exit));
            return;
        }

        Hide();
    }

    private static async Task RunNoTrayExitAsync(Func<Task> exit)
    {
        try
        {
            await exit();
        }
        catch (Exception exception)
        {
            // The coordinator has already run shutdown and aggregated the failures; record them here because
            // there is no tray router to observe this task.
            AppLog.Write(LogLevel.Critical, "Application", "Application exit from the main window (no-tray mode) reported an error", exception: exception);
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _noTrayExit is null)
        {
            Hide();
        }
    }
}



