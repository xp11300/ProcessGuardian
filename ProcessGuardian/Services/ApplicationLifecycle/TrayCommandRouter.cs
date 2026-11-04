using System.Diagnostics;

namespace ProcessGuardian.Services.ApplicationLifecycle;

/// <summary>Maps tray icon actions to window presentation and application exit operations.</summary>
public sealed class TrayCommandRouter : IDisposable
{
    private readonly ITrayIconService _trayIcon;
    private readonly Action _openWindow;
    private readonly Action _openApplications;
    private readonly Func<Task> _exit;
    private bool _disposed;

    public TrayCommandRouter(
        ITrayIconService trayIcon,
        Action openWindow,
        Action openApplications,
        Func<Task> exit)
    {
        _trayIcon = trayIcon;
        _openWindow = openWindow;
        _openApplications = openApplications;
        _exit = exit;
        _trayIcon.OpenRequested += OnOpenRequested;
        _trayIcon.ApplicationsRequested += OnApplicationsRequested;
        _trayIcon.ExitRequested += OnExitRequested;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trayIcon.OpenRequested -= OnOpenRequested;
        _trayIcon.ApplicationsRequested -= OnApplicationsRequested;
        _trayIcon.ExitRequested -= OnExitRequested;
    }

    private void OnOpenRequested(object? sender, EventArgs eventArgs) => _openWindow();

    private void OnApplicationsRequested(object? sender, EventArgs eventArgs) => _openApplications();

    private async void OnExitRequested(object? sender, EventArgs eventArgs)
    {
        try
        {
            await _exit();
        }
        catch (Exception exception)
        {
            // Nothing else observes this async void handler, and Debug output is compiled out of Release builds,
            // so the failure goes to the log (or the fallback file when the logger is already disposed) and Trace.
            FatalErrorReporter.RecordError("Application exit from the tray failed", exception);
        }
    }
}
