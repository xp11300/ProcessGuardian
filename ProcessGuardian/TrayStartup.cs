using System.Diagnostics;
using ProcessGuardian.Services.ApplicationLifecycle;
using ProcessGuardian.Services.Logging;

namespace ProcessGuardian;

/// <summary>Outcome of tray startup. Both members are null when the tray is unavailable (no-tray mode).</summary>
internal sealed record TrayStartupResult(ITrayIconService? TrayIcon, TrayCommandRouter? Router)
{
    public bool TrayAvailable => TrayIcon is not null;
}

/// <summary>
/// Creates the tray icon at startup (stage-12 policy B). On success the result is exactly what OnStartup
/// built before: a tray icon plus its command router, and MainWindow keeps hiding to the tray. If creating
/// the tray fails (icon resource, Icon constructor, or TrayIconService constructor), the failure is logged as
/// Critical and the log is flushed, the user is told, and the main window switches to no-tray mode: close
/// performs the normal application exit and minimize stays in the taskbar.
/// </summary>
internal static class TrayStartup
{
    internal static readonly TimeSpan LogFlushTimeout = TimeSpan.FromSeconds(3);

    internal const string TrayUnavailableMessage =
        "The Process Guardian system tray icon could not be created, so Process Guardian will run without a tray icon.\n\n" +
        "Managed applications keep working. In this mode, closing the main window exits Process Guardian " +
        "(and stops the applications it manages, as tray Exit does), and minimizing keeps the window in the taskbar.\n\n" +
        "Details have been written to the Process Guardian log.";

    internal static TrayStartupResult Configure(
        MainWindow mainWindow,
        Func<ITrayIconService> createTrayIcon,
        Action<Exception> notifyTrayUnavailable,
        Func<Task> exit,
        Action openWindow,
        Action openApplications)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        ArgumentNullException.ThrowIfNull(createTrayIcon);
        ArgumentNullException.ThrowIfNull(notifyTrayUnavailable);
        ArgumentNullException.ThrowIfNull(exit);
        ArgumentNullException.ThrowIfNull(openWindow);
        ArgumentNullException.ThrowIfNull(openApplications);

        ITrayIconService trayIcon;
        try
        {
            trayIcon = createTrayIcon();
        }
        catch (Exception exception)
        {
            AppLog.Write(LogLevel.Critical, "Application", "System tray icon could not be created; continuing without a tray icon (close exits the application, minimize stays in the taskbar)", exception: exception);
            FlushLog();
            mainWindow.EnableNoTrayMode(exit);
            notifyTrayUnavailable(exception);
            return new TrayStartupResult(null, null);
        }

        var router = new TrayCommandRouter(trayIcon, openWindow, openApplications, exit);
        return new TrayStartupResult(trayIcon, router);
    }

    /// <summary>Waits (bounded) for the current logger to write queued entries. Runs the flush off the
    /// calling (UI) thread so the wait cannot deadlock on its synchronization context.</summary>
    internal static bool FlushLog()
    {
        if (AppLog.Current is not { } logger)
        {
            return false;
        }

        try
        {
            if (Task.Run(() => logger.FlushAsync()).Wait(LogFlushTimeout))
            {
                return true;
            }

            Trace.WriteLine("Process Guardian log flush did not complete within " + LogFlushTimeout);
            return false;
        }
        catch (Exception exception)
        {
            Trace.WriteLine("Process Guardian log flush failed: " + exception);
            return false;
        }
    }
}
