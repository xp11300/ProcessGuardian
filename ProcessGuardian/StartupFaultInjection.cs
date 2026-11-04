#if PG_FAULT_INJECTION
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using ProcessGuardian.Services;

namespace ProcessGuardian;

/// <summary>
/// Stage-12 startup fault injection. TEST BUILDS ONLY: this whole file is compiled only when PG_FAULT_INJECTION is
/// defined (the FaultInjection configuration); Debug and Release builds contain no trace of it. A point fires only
/// when PROCESSGUARDIAN_FAULT_INJECTION lists it (comma-separated) AND PROCESSGUARDIAN_DATA_DIR is an absolute
/// path, so an injected failure can never touch the real %LOCALAPPDATA%\ProcessGuardian data.
/// </summary>
internal static class StartupFaultInjection
{
    internal const string VariableName = "PROCESSGUARDIAN_FAULT_INJECTION";
    internal const string MessagePrefix = "PG_FAULT_INJECTION: ";

    internal const string BeforeLogger = "BeforeLogger";                         // I1
    internal const string AfterLogger = "AfterLogger";                           // I2
    internal const string MainWindowInit = "MainWindowInit";                     // I3
    internal const string MainWindowAfterViewModel = "MainWindowAfterViewModel"; // I4
    internal const string TrayCreate = "TrayCreate";                             // I5
    internal const string TrayAfterVisible = "TrayAfterVisible";                 // I6
    internal const string BeforeShow = "BeforeShow";                             // I7
    internal const string PostStartupUi = "PostStartupUi";                       // I8
    internal const string BackgroundThread = "BackgroundThread";                 // I9
    internal const string UnobservedTask = "UnobservedTask";                     // I10

    private static readonly TimeSpan PostStartupDelay = TimeSpan.FromSeconds(2);

    internal static bool IsRequested(string point)
    {
        var value = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrWhiteSpace(value)
            || !value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(point, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var dataDirectory = Environment.GetEnvironmentVariable(AppDataPaths.OverrideVariable);
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory))
        {
            Trace.WriteLine($"Process Guardian fault injection '{point}' refused: {AppDataPaths.OverrideVariable} is not an absolute path.");
            return false;
        }

        return true;
    }

    internal static InvalidOperationException Create(string point) => new(MessagePrefix + point);

    internal static void ThrowIfRequested(string point)
    {
        if (IsRequested(point))
        {
            throw Create(point);
        }
    }

    /// <summary>I8-I10: faults that happen after OnStartup has returned (window shown, tray created).</summary>
    internal static void SchedulePostStartupFaults(Dispatcher dispatcher)
    {
        if (IsRequested(PostStartupUi))
        {
            StartOneShotTimer(dispatcher, () => throw Create(PostStartupUi));
        }

        if (IsRequested(BackgroundThread))
        {
            StartOneShotTimer(dispatcher, () => new Thread(() => throw Create(BackgroundThread))
            {
                IsBackground = true,
                Name = "Process Guardian fault injection",
            }.Start());
        }

        if (IsRequested(UnobservedTask))
        {
            StartOneShotTimer(dispatcher, () =>
            {
                StartFaultedTask();
                var collections = 0;
                var collector = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (sender, _) =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    if (++collections >= 3)
                    {
                        ((DispatcherTimer)sender!).Stop();
                    }
                }, dispatcher);
                collector.Start();
            });
        }
    }

    // Separate method so no local keeps the faulted task reachable.
    private static void StartFaultedTask() => _ = Task.Run(() => throw Create(UnobservedTask));

    private static void StartOneShotTimer(Dispatcher dispatcher, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = PostStartupDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }
}
#endif
