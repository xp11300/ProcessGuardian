using System.Diagnostics;
using ProcessGuardian.Services.Logging;

namespace ProcessGuardian.Services.ApplicationLifecycle;

/// <summary>
/// Stage-12 fatal-error reporting (F1-F3, F5, F6). Reporting never changes how an exception is processed: callers
/// still crash (the startup path rethrows; the unhandled-exception handlers never set Handled). A report writes
/// the error to the normal log and confirms it reached disk, falls back to <see cref="FallbackLog"/> otherwise,
/// removes the tray icon, and shows at most one message box per process. The same exception object reaching a
/// second handler (startup rethrow -> DispatcherUnhandledException -> AppDomain.UnhandledException) is reported once.
/// </summary>
internal static class FatalErrorReporter
{
    internal const string MessageTitle = "Process Guardian - fatal error";
    internal const int MaxSummaryLength = 300;

    private static readonly object Gate = new();
    private static Exception? _firstReportedException;
    private static bool _messageShown;

    internal sealed record Outcome(bool Duplicate, bool Persisted, string? FallbackPath, bool MessageShown, string? Message);

    /// <summary>Reports a fatal error. Never throws; failures of the individual steps go to Trace.</summary>
    /// <param name="kind">"startup", "UI-thread", "terminating" or "non-terminating".</param>
    internal static Outcome Report(string kind, Exception? exception, Action? disposeTray, Action<string, string>? showMessage)
    {
        bool showThisTime;
        lock (Gate)
        {
            if (exception is not null && ReferenceEquals(exception, _firstReportedException))
            {
                Trace.WriteLine($"Process Guardian {kind} handler: exception already reported ({exception.GetType().Name}).");
                return new Outcome(true, false, null, false, null);
            }

            _firstReportedException ??= exception;
            showThisTime = !_messageShown && showMessage is not null;
            _messageShown |= showThisTime;
        }

        var (persisted, fallbackPath) = Record(kind == "startup" ? "Process Guardian startup failed" : $"Unhandled {kind} exception", exception);

        if (disposeTray is not null)
        {
            try
            {
                disposeTray();
            }
            catch (Exception trayException)
            {
                Trace.WriteLine("Process Guardian could not remove the tray icon during fatal error handling: " + trayException);
                AppLog.Write(LogLevel.Warning, "Application", "Tray icon could not be removed during fatal error handling; it may remain until the mouse passes over it", exception: trayException);
            }
        }

        string? message = null;
        if (showThisTime)
        {
            var location = persisted
                ? "log folder " + AppLog.Current?.LogDirectory
                : fallbackPath;
            message = BuildMessage(kind, exception, location);
            try
            {
                showMessage!(message, MessageTitle);
            }
            catch (Exception messageException)
            {
                Trace.WriteLine("Process Guardian could not show the fatal error message: " + messageException);
            }
        }

        return new Outcome(false, persisted, fallbackPath, showThisTime, message);
    }

    /// <summary>F6: records a non-fatal error that has no other observer (e.g. a failed tray exit). No message box.</summary>
    internal static void RecordError(string message, Exception exception)
    {
        Trace.WriteLine($"Process Guardian: {message}: {exception}");
        Record(message, exception);
    }

    /// <summary>
    /// Writes a Critical entry and confirms it reached disk (flush completed, no write failure since the entry was
    /// queued, logger not disposed). Otherwise writes a fallback file. Never throws.
    /// </summary>
    internal static (bool Persisted, string? FallbackPath) Record(string message, Exception? exception)
    {
        var persisted = false;
        try
        {
            if (AppLog.Current is { IsDisposed: false } logger)
            {
                var failuresBefore = logger.WriteFailureCount;
                logger.Write(LogLevel.Critical, "Application", message, exception: exception);
                persisted = TrayStartup.FlushLog()
                    && !logger.IsDisposed
                    && logger.WriteFailureCount == failuresBefore
                    && logger.LastWriteException is null;
            }
        }
        catch (Exception loggingException)
        {
            Trace.WriteLine("Process Guardian could not log a fatal error: " + loggingException);
        }

        return persisted ? (true, null) : (false, FallbackLog.Write(message, exception));
    }

    internal static string BuildMessage(string kind, Exception? exception, string? detailsLocation)
    {
        var headline = kind == "startup"
            ? "Process Guardian could not start and will close."
            : "Process Guardian encountered a fatal error and will close.";
        var summary = exception is null ? "(no exception details)" : exception.GetType().Name + ": " + exception.Message;
        if (summary.Length > MaxSummaryLength)
        {
            summary = summary[..(MaxSummaryLength - 3)] + "...";
        }

        var details = detailsLocation is null
            ? "No log could be written. Windows may have recorded the crash in the Application event log."
            : "Details: " + detailsLocation;
        return headline + "\n\n" + summary + "\n\n" + details;
    }

    /// <summary>Test seam: clears the once-per-process state.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _firstReportedException = null;
            _messageShown = false;
        }
    }
}
