using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using ProcessGuardian.Interop;
using ProcessGuardian.Models;
using ProcessGuardian.Services.ApplicationLifecycle;
using ProcessGuardian.Services.Configuration;
using ProcessGuardian.Services.JobObjects;
using ProcessGuardian.Services.Logging;
using ProcessGuardian.Services.ManagedApplications;
using ProcessGuardian.Services.ProcessManagement;
using ProcessGuardian.Services.WindowMonitoring;
using ProcessGuardian.ViewModels;

namespace ProcessGuardian.JobObjectTests;

/// <summary>
/// Stage-12 abnormal-scenario fault injection. Every fault is injected through the internal native-API seams
/// (IJobProcessNativeApi / IWinEventHookApi) or the tray factory; only dedicated test children of this
/// executable are started, and every one of them is accounted for and cleaned up by the test itself.
/// </summary>
internal static class FaultInjectionTests
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotEnoughMemory = 8;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorInvalidHandle = 6;
    private const int Iterations = 5;
    private const int HandleGrowthTolerance = 12;

    internal static void RunLauncherFaultTests(string testExecutable)
    {
        RunLauncherScenario(
            "CreateJobObject fails",
            testExecutable,
            api => api.CreateJobError = ErrorNotEnoughMemory,
            expectProcessCreated: false,
            expectChildStillSuspended: false,
            assertException: exception => AssertWin32(exception, ErrorNotEnoughMemory, "CreateJobObject failed (Windows error 8)"),
            expectedLogMessages: ["CreateJobObject failed"]);

        RunLauncherScenario(
            "SetInformationJobObject (KILL_ON_JOB_CLOSE) fails",
            testExecutable,
            api => api.SetInformationError = ErrorAccessDenied,
            expectProcessCreated: false,
            expectChildStillSuspended: false,
            assertException: exception => AssertWin32(exception, ErrorAccessDenied, "SetInformationJobObject failed (Windows error 5)"),
            expectedLogMessages: ["Could not configure KILL_ON_JOB_CLOSE"]);

        RunLauncherScenario(
            "AssignProcessToJobObject fails",
            testExecutable,
            api => api.AssignError = ErrorAccessDenied,
            expectProcessCreated: true,
            expectChildStillSuspended: false,
            assertException: exception => AssertWin32(exception, ErrorAccessDenied, "AssignProcessToJobObject for process"),
            expectedLogMessages: ["AssignProcessToJobObject failed", "Process startup failed; startup cleanup is running", "Closed Job Object handle"]);

        RunLauncherScenario(
            "ResumeThread fails (process already in the Job)",
            testExecutable,
            api => api.ResumeError = ErrorInvalidHandle,
            expectProcessCreated: true,
            expectChildStillSuspended: false,
            assertException: exception => AssertWin32(exception, ErrorInvalidHandle, "ResumeThread failed (Windows error 6)"),
            expectedLogMessages: ["Assigned process to Job Object", "ResumeThread failed", "Process startup failed; startup cleanup is running", "Closed Job Object handle"]);

        RunLauncherScenario(
            "ResumeThread fails and TerminateProcess also fails (Job close ends the process)",
            testExecutable,
            api =>
            {
                api.ResumeError = ErrorInvalidHandle;
                api.TerminateError = ErrorAccessDenied;
            },
            expectProcessCreated: true,
            expectChildStillSuspended: false,
            assertException: exception =>
            {
                var aggregate = exception as AggregateException
                    ?? throw new InvalidOperationException($"Expected AggregateException, got {exception.GetType().Name}: {exception.Message}");
                AssertWin32(aggregate.InnerExceptions[0], ErrorInvalidHandle, "ResumeThread failed (Windows error 6)");
                AssertWin32(aggregate.InnerExceptions[1], ErrorAccessDenied, "TerminateProcess during startup cleanup failed (Windows error 5)");
            },
            expectedLogMessages: ["ResumeThread failed", "TerminateProcess failed during startup cleanup; the process ended when its Job closed", "Closed Job Object handle"]);

        RunLauncherScenario(
            "ResumeThread fails and the cleanup wait times out (bounded wait reports TimeoutException)",
            testExecutable,
            api =>
            {
                api.ResumeError = ErrorInvalidHandle;
                api.WaitResultOverride = ProcessNativeMethods.WaitTimeout;
            },
            expectProcessCreated: true,
            expectChildStillSuspended: false,
            assertException: exception =>
            {
                var aggregate = exception as AggregateException
                    ?? throw new InvalidOperationException($"Expected AggregateException, got {exception.GetType().Name}: {exception.Message}");
                AssertWin32(aggregate.InnerExceptions[0], ErrorInvalidHandle, "ResumeThread failed");
                if (aggregate.InnerExceptions[1] is not TimeoutException)
                {
                    throw new InvalidOperationException($"Expected a TimeoutException cleanup error, got {aggregate.InnerExceptions[1]}");
                }
            },
            expectedLogMessages: ["ResumeThread failed", "Startup cleanup wait did not confirm process exit", "Closed Job Object handle"]);

        RunLauncherScenario(
            "AssignProcessToJobObject fails and TerminateProcess also fails (no wait; error reported)",
            testExecutable,
            api =>
            {
                api.AssignError = ErrorAccessDenied;
                api.TerminateError = ErrorAccessDenied;
            },
            expectProcessCreated: true,
            expectChildStillSuspended: true,
            assertException: exception =>
            {
                var aggregate = exception as AggregateException
                    ?? throw new InvalidOperationException($"Expected AggregateException, got {exception.GetType().Name}: {exception.Message}");
                AssertWin32(aggregate.InnerExceptions[0], ErrorAccessDenied, "AssignProcessToJobObject for process");
                AssertWin32(aggregate.InnerExceptions[1], ErrorAccessDenied, "TerminateProcess during startup cleanup failed (Windows error 5)");
            },
            expectedLogMessages: ["AssignProcessToJobObject failed", "TerminateProcess failed during startup cleanup; the suspended process is not in the Job"]);
    }

    private static void RunLauncherScenario(
        string name,
        string testExecutable,
        Action<FaultInjectingJobProcessNativeApi> configure,
        bool expectProcessCreated,
        bool expectChildStillSuspended,
        Action<Exception> assertException,
        string[] expectedLogMessages)
    {
        var testDirectory = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", $"stage12-launcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        using var self = Process.GetCurrentProcess();
        var handleCounts = new List<int>();
        var maximumElapsed = TimeSpan.Zero;
        try
        {
            for (var iteration = 1; iteration <= Iterations; iteration++)
            {
                var logDirectory = Path.Combine(testDirectory, $"logs-{iteration}");
                var logger = new FileLogService(logDirectory);
                var previousLogger = AppLog.Replace(logger);
                var unrelated = StartUnrelatedChild();
                var readyName = $"ProcessGuardianStage12_{Guid.NewGuid():N}";
                using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
                var api = new FaultInjectingJobProcessNativeApi();
                configure(api);
                try
                {
                    var launcher = new ProcessLauncher(api);
                    Exception? caught = null;
                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        var unexpected = launcher.Launch(testExecutable, $"--launcher-child {readyName}", testDirectory);
                        unexpected.Dispose();
                    }
                    catch (Exception exception)
                    {
                        caught = exception;
                    }

                    stopwatch.Stop();
                    maximumElapsed = stopwatch.Elapsed > maximumElapsed ? stopwatch.Elapsed : maximumElapsed;
                    if (caught is null)
                    {
                        throw new InvalidOperationException($"{name}: Launch did not report the injected failure.");
                    }

                    assertException(caught);
                    if (stopwatch.Elapsed > TimeSpan.FromSeconds(4))
                    {
                        throw new InvalidOperationException($"{name}: Launch failure path took {stopwatch.Elapsed.TotalMilliseconds:0} ms (expected bounded, < 4000 ms).");
                    }

                    if (api.CreatedJobHandle is null || !api.CreatedJobHandle.IsClosed)
                    {
                        throw new InvalidOperationException($"{name}: the Job handle was not closed after the failure.");
                    }

                    if (expectProcessCreated != api.CreatedProcessId.HasValue)
                    {
                        throw new InvalidOperationException($"{name}: process creation expectation {expectProcessCreated} did not match (pid={api.CreatedProcessId}).");
                    }

                    if (api.CreatedProcessId is { } childPid)
                    {
                        if (expectChildStillSuspended)
                        {
                            // Documented residual case: the suspended child is outside any Job and TerminateProcess
                            // failed, so the launcher reports the error instead of waiting forever. The test
                            // cleans up its own dedicated child here.
                            using var child = Process.GetProcessById(checked((int)childPid));
                            child.Kill();
                            if (!child.WaitForExit(5000))
                            {
                                throw new TimeoutException($"{name}: the test could not clean up its own suspended child {childPid}.");
                            }
                        }

                        if (!WaitForExit(childPid, TimeSpan.FromSeconds(5)))
                        {
                            throw new InvalidOperationException($"{name}: the recorded child process {childPid} is still running.");
                        }
                    }

                    if (ready.WaitOne(0))
                    {
                        throw new InvalidOperationException($"{name}: the test child ran (ready event was set) although startup failed.");
                    }

                    if (unrelated.HasExited)
                    {
                        throw new InvalidOperationException($"{name}: an unrelated test child was affected.");
                    }

                    logger.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    var entries = logger.ReadRecentAsync().GetAwaiter().GetResult();
                    foreach (var message in expectedLogMessages)
                    {
                        if (!entries.Any(entry => entry.Message.Contains(message, StringComparison.Ordinal)))
                        {
                            throw new InvalidOperationException($"{name}: log entry '{message}' is missing. Entries: {string.Join(" | ", entries.Select(entry => entry.Message))}");
                        }
                    }
                }
                finally
                {
                    if (api.CreatedProcessId is { } pid && !IsExited(pid))
                    {
                        try
                        {
                            using var leftover = Process.GetProcessById(checked((int)pid));
                            leftover.Kill();
                            leftover.WaitForExit(5000);
                        }
                        catch (ArgumentException)
                        {
                            // Already gone.
                        }
                    }

                    StopChild(unrelated);
                    AppLog.Replace(previousLogger);
                    logger.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                self.Refresh();
                handleCounts.Add(self.HandleCount);
            }

            var growth = handleCounts[^1] - handleCounts[0];
            if (growth > HandleGrowthTolerance)
            {
                throw new InvalidOperationException($"{name}: process handle count grew over {Iterations} iterations ({string.Join("->", handleCounts)}).");
            }

            Console.WriteLine($"PASS: stage-12 fault: {name}: error + code reported, child never ran, child PID exited, Job handle closed, unrelated child alive, log entries present; {Iterations} iterations, max {maximumElapsed.TotalMilliseconds:0} ms, handles {string.Join("->", handleCounts)}");
        }
        finally
        {
            DeleteDirectory(testDirectory);
        }
    }

    internal static void RunServiceAssignFailureTest(string testExecutable)
    {
        var testDirectory = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", $"stage12-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var readyName = $"ProcessGuardianStage12Svc_{Guid.NewGuid():N}";
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        var api = new FaultInjectingJobProcessNativeApi { AssignError = ErrorAccessDenied };
        var unrelated = StartUnrelatedChild();
        var service = new ManagedApplicationService(new ProcessLauncher(api));
        try
        {
            var application = service.Start("Stage 12 assign failure", testExecutable, $"--launcher-child {readyName}", testDirectory);
            if (application.Status != ManagedApplicationStatus.Failed
                || application.LaunchResult is not null
                || application.Error is not Win32Exception { NativeErrorCode: ErrorAccessDenied })
            {
                throw new InvalidOperationException($"Service-level Assign failure was not reported as Failed with Windows error 5 (status={application.Status}; error={application.Error}).");
            }

            if (api.CreatedProcessId is not { } pid || !WaitForExit(pid, TimeSpan.FromSeconds(5)) || ready.WaitOne(0)
                || api.CreatedJobHandle is not { IsClosed: true } || unrelated.HasExited)
            {
                throw new InvalidOperationException("Service-level Assign failure left the child running, ran it, kept the Job open, or affected an unrelated process.");
            }

            Console.WriteLine("PASS: stage-12 fault: service-level AssignProcessToJobObject failure shows Failed (Windows error 5), child never ran and exited, Job closed, unrelated child alive");
        }
        finally
        {
            service.Dispose();
            StopChild(unrelated);
            DeleteDirectory(testDirectory);
        }
    }

    internal static void RunWindowMonitorHookFailureTest()
    {
        using var self = Process.GetCurrentProcess();
        var handleCounts = new List<int>();
        var threadCounts = new List<int>();
        var hookApi = new FailingWinEventHookApi(ErrorInvalidParameter);
        var child = StartUnrelatedChild();
        try
        {
            for (var iteration = 1; iteration <= Iterations; iteration++)
            {
                Exception? caught = null;
                try
                {
                    using var monitor = new WindowMonitor(
                        checked((uint)child.Id),
                        null,
                        false,
                        HideAsCloseTimings.Default,
                        null,
                        hookApi);
                }
                catch (Exception exception)
                {
                    caught = exception;
                }

                if (caught is not Win32Exception { NativeErrorCode: ErrorInvalidParameter } win32
                    || !win32.Message.Contains("SetWinEventHook failed", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"WindowMonitor did not report the injected SetWinEventHook failure with Windows error 87: {caught}");
                }

                if (child.HasExited)
                {
                    throw new InvalidOperationException("A failed hook affected the monitored test child.");
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                self.Refresh();
                handleCounts.Add(self.HandleCount);
                threadCounts.Add(CountThreads(self));
            }

            if (hookApi.SetCalls != Iterations || hookApi.UnhookCalls != 0)
            {
                throw new InvalidOperationException($"Unexpected hook call counts (set={hookApi.SetCalls}, unhook={hookApi.UnhookCalls}).");
            }

            if (handleCounts[^1] - handleCounts[0] > HandleGrowthTolerance || threadCounts[^1] - threadCounts[0] > 4)
            {
                throw new InvalidOperationException($"Failed hooks leaked handles or monitor threads (handles {string.Join("->", handleCounts)}; threads {string.Join("->", threadCounts)}).");
            }

            Console.WriteLine($"PASS: stage-12 fault: WindowMonitor SetWinEventHook failure throws Win32Exception (Windows error 87), no unhook of a null hook, monitored child unaffected; {Iterations} iterations, handles {string.Join("->", handleCounts)}, threads {string.Join("->", threadCounts)}");
        }
        finally
        {
            StopChild(child);
        }
    }

    internal static void RunStaTests(string testExecutable)
    {
        RunOnStaDispatcher(async () =>
        {
            await RunViewModelHookFailureTestAsync(testExecutable);
            await RunTrayFailurePolicyTestAsync();
            await RunTraySuccessPathUnchangedTestAsync();
        }, TimeSpan.FromSeconds(60), "Process Guardian stage-12 STA tests");
    }

    private static async Task RunViewModelHookFailureTestAsync(string testExecutable)
    {
        var testDirectory = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", $"stage12-hook-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var childPidFile = Path.Combine(testDirectory, "children.txt");
        var readyName = $"ProcessGuardianStage12Hook_{Guid.NewGuid():N}";
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        var configPath = Path.Combine(testDirectory, "config.json");
        await new ConfigurationService(configPath).SaveAsync([
            new ApplicationConfiguration
            {
                Id = Guid.NewGuid(),
                SoftwareName = "Stage 12 hook failure",
                ExecutablePath = testExecutable,
                AddedAt = DateTimeOffset.UtcNow,
                Arguments = $"--managed-window-child {readyName} \"{childPidFile}\" 1",
                WorkingDirectory = testDirectory,
            },
        ]);

        var hookApi = new FailingWinEventHookApi(ErrorAccessDenied);
        var service = new ManagedApplicationService(synchronizationContext: SynchronizationContext.Current) { WinEventHookApi = hookApi };
        var unrelated = StartUnrelatedChild();
        var viewModel = new MainViewModel(
            service,
            new FixedExecutableFilePicker(),
            new ConfigurationService(configPath),
            confirmStop: _ => true,
            confirmRemove: _ => true);
        int[] processIds = [];
        try
        {
            await viewModel.InitializationTask;
            var item = viewModel.ApplicationItems.Single();
            await item.LaunchCommand.ExecuteAsync();
            if (!ready.WaitOne(8000))
            {
                throw new TimeoutException("The hook-failure test app did not create its window and background children.");
            }

            var managed = item.ManagedApplication ?? throw new InvalidOperationException("The hook-failure app was not retained.");
            processIds = new[] { checked((int)managed.ProcessId) }.Concat(File.ReadAllLines(childPidFile).Select(int.Parse)).ToArray();
            if (managed.Status != ManagedApplicationStatus.MonitoringUnavailable
                || managed.Error is not Win32Exception { NativeErrorCode: ErrorAccessDenied }
                || item.MonitorStatusText != "Hook failed (Windows error 5)"
                || item.StatusText != "Monitoring failed"
                || !item.CanStop
                || hookApi.SetCalls < 1)
            {
                throw new InvalidOperationException($"Hook failure was not shown as monitoring unavailable (status={managed.Status}; monitor={item.MonitorStatusText}; text={item.StatusText}; canStop={item.CanStop}; error={managed.Error}).");
            }

            if (processIds.Any(IsExited))
            {
                throw new InvalidOperationException("A hook failure ended managed processes; the app must stay managed.");
            }

            await item.StopCommand.ExecuteAsync();
            foreach (var processId in processIds)
            {
                if (!WaitForExit(checked((uint)processId), TimeSpan.FromSeconds(8)))
                {
                    throw new InvalidOperationException($"Stop after a hook failure left process {processId} of the managed tree running.");
                }
            }

            if (!item.RecentOperationResult.StartsWith("Stop completed", StringComparison.Ordinal) || unrelated.HasExited)
            {
                throw new InvalidOperationException($"Stop after hook failure did not complete or affected an unrelated process ({item.RecentOperationResult}).");
            }

            Console.WriteLine($"PASS: stage-12 fault: SetWinEventHook failure at service/ViewModel level shows 'Hook failed (Windows error 5)', keeps the app managed, Stop ends the whole tree ({processIds.Length} processes), unrelated child alive");
        }
        finally
        {
            await viewModel.DisposeAsync();
            service.Dispose();
            StopChild(unrelated);
            foreach (var processId in processIds.Where(pid => !IsExited(pid)))
            {
                // Only reached if an assertion above failed; these are this test's own children.
                try
                {
                    using var leftover = Process.GetProcessById(processId);
                    leftover.Kill();
                    leftover.WaitForExit(5000);
                }
                catch (ArgumentException)
                {
                    // Already gone.
                }
            }

            DeleteDirectory(testDirectory);
        }
    }

    private static async Task RunTrayFailurePolicyTestAsync()
    {
        var testDirectory = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", $"stage12-tray-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var failures = new (string Name, Func<ITrayIconService> Factory, Type ExpectedType)[]
        {
            ("icon resource", () => throw new InvalidOperationException("The Process Guardian tray icon resource could not be loaded."), typeof(InvalidOperationException)),
            ("Icon constructor", CreateTrayFromCorruptIcon, typeof(ArgumentException)),
            ("TrayIconService constructor", () => new TrayIconService(null!), typeof(ArgumentNullException)),
        };

        try
        {
            for (var index = 0; index < failures.Length; index++)
            {
                var (failureName, factory, expectedType) = failures[index];
                var logger = new FileLogService(Path.Combine(testDirectory, $"logs-{index}"));
                var previousLogger = AppLog.Replace(logger);
                var window = new MainWindow(new ConfigurationService(Path.Combine(testDirectory, $"config-{index}.json")), new FixedExecutableFilePicker());
                var calls = new List<string>();
                var closed = false;
                window.Closed += (_, _) => closed = true;
                var coordinator = new ApplicationExitCoordinator(
                    stopAcceptingOperations: () => calls.Add("stop"),
                    waitForOperations: () => { calls.Add("wait"); return Task.CompletedTask; },
                    cleanup: async () =>
                    {
                        calls.Add("cleanup");
                        await window.ViewModel.DisposeAsync();
                        window.AllowApplicationClose();
                    },
                    shutdown: () =>
                    {
                        calls.Add("shutdown");
                        window.Close();
                    });
                Exception? notified = null;
                var notifyCount = 0;
                try
                {
                    var result = TrayStartup.Configure(
                        window,
                        factory,
                        exception => { notifyCount++; notified = exception; },
                        coordinator.ExitAsync,
                        openWindow: () => { },
                        openApplications: () => { });

                    if (result.TrayAvailable || result.TrayIcon is not null || result.Router is not null
                        || notifyCount != 1 || notified is null || !expectedType.IsInstanceOfType(notified) || !window.IsNoTrayMode)
                    {
                        throw new InvalidOperationException($"Tray failure ({failureName}) did not select no-tray mode and notify once (tray={result.TrayAvailable}; notify={notifyCount}; type={notified?.GetType().Name}; noTray={window.IsNoTrayMode}).");
                    }

                    // The log must already be on disk: Configure flushes before notifying the user.
                    var entries = await logger.ReadRecentAsync();
                    if (!entries.Any(entry => entry.Level == LogLevel.Critical
                        && entry.Message.StartsWith("System tray icon could not be created", StringComparison.Ordinal)
                        && entry.Exception?.Contains(expectedType.FullName!, StringComparison.Ordinal) == true))
                    {
                        throw new InvalidOperationException($"Tray failure ({failureName}) was not logged as Critical with its exception before notification.");
                    }

                    window.Show();
                    await PumpAsync();
                    if (!window.IsVisible)
                    {
                        throw new InvalidOperationException("The main window was not shown in no-tray mode.");
                    }

                    window.WindowState = WindowState.Minimized;
                    await PumpAsync();
                    if (!window.IsVisible || window.Visibility != Visibility.Visible || window.WindowState != WindowState.Minimized)
                    {
                        throw new InvalidOperationException("Minimize hid the window in no-tray mode (it must stay in the taskbar).");
                    }

                    window.WindowState = WindowState.Normal;
                    await PumpAsync();
                    window.Close();
                    await PumpAsync();
                    if (!coordinator.ExitRequested)
                    {
                        throw new InvalidOperationException("Close in no-tray mode did not request the application exit.");
                    }

                    // Same task the close button started (ExitAsync is idempotent).
                    await coordinator.ExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    await PumpAsync();
                    if (!closed || string.Join(",", calls) != "stop,wait,cleanup,shutdown")
                    {
                        throw new InvalidOperationException($"Close in no-tray mode did not run the ApplicationExitCoordinator path (closed={closed}; calls={string.Join(",", calls)}).");
                    }

                    Console.WriteLine($"PASS: stage-12 fault: tray creation failure ({failureName}) logs Critical + flush, notifies once via callback (no modal), shows the window, minimize stays in taskbar, close runs exit coordinator (stop,wait,cleanup,shutdown)");
                }
                finally
                {
                    if (!closed)
                    {
                        window.AllowApplicationClose();
                        window.Close();
                        await window.ViewModel.DisposeAsync();
                    }

                    AppLog.Replace(previousLogger);
                    await logger.DisposeAsync();
                }
            }
        }
        finally
        {
            DeleteDirectory(testDirectory);
        }
    }

    private static async Task RunTraySuccessPathUnchangedTestAsync()
    {
        var testDirectory = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", $"stage12-tray-ok-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var window = new MainWindow(new ConfigurationService(Path.Combine(testDirectory, "config.json")), new FixedExecutableFilePicker());
        var tray = new FakeTrayIconService();
        var exitCalls = 0;
        var notifyCalls = 0;
        var openCalls = 0;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        TrayStartupResult? result = null;
        try
        {
            result = TrayStartup.Configure(
                window,
                () => tray,
                _ => notifyCalls++,
                () => { exitCalls++; return Task.CompletedTask; },
                openWindow: () => openCalls++,
                openApplications: () => { });
            if (!result.TrayAvailable || !ReferenceEquals(result.TrayIcon, tray) || result.Router is null || notifyCalls != 0 || window.IsNoTrayMode)
            {
                throw new InvalidOperationException("Successful tray creation did not keep the normal tray mode.");
            }

            window.Show();
            await PumpAsync();
            window.WindowState = WindowState.Minimized;
            await PumpAsync();
            if (window.IsVisible)
            {
                throw new InvalidOperationException("With a tray, minimize must still hide the window to the tray.");
            }

            window.WindowState = WindowState.Normal;
            window.Show();
            await PumpAsync();
            window.Close();
            await PumpAsync();
            if (closed || window.IsVisible || exitCalls != 0)
            {
                throw new InvalidOperationException("With a tray, close must still hide to the tray without exiting.");
            }

            tray.RaiseOpen();
            tray.RaiseExit();
            await PumpAsync();
            if (openCalls != 1 || exitCalls != 1)
            {
                throw new InvalidOperationException($"The tray router built by TrayStartup did not route Open/Exit (open={openCalls}; exit={exitCalls}).");
            }

            Console.WriteLine("PASS: stage-12: tray creation success keeps today's behavior (close and minimize hide to tray, no notification, tray Open/Exit routed)");
        }
        finally
        {
            result?.Router?.Dispose();
            tray.Dispose();
            window.AllowApplicationClose();
            window.Close();
            await window.ViewModel.DisposeAsync();
            DeleteDirectory(testDirectory);
        }
    }

    private static ITrayIconService CreateTrayFromCorruptIcon()
    {
        using var stream = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        using var icon = new System.Drawing.Icon(stream);
        return new TrayIconService(icon);
    }

    private static Task PumpAsync() =>
        Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static void RunOnStaDispatcher(Func<Task> body, TimeSpan timeout, string name)
    {
        Exception? failure = null;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    finished.TrySetResult();
                }
            }));
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = name,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!finished.Task.Wait(timeout))
        {
            throw new TimeoutException($"{name} did not finish within {timeout.TotalSeconds:0} s.");
        }

        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException($"{name} failed.", failure);
        }
    }

    private static void AssertWin32(Exception exception, int expectedCode, string expectedMessagePart)
    {
        if (exception is not Win32Exception win32 || win32.NativeErrorCode != expectedCode
            || !win32.Message.Contains(expectedMessagePart, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected Win32Exception {expectedCode} containing '{expectedMessagePart}', got {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static Process StartUnrelatedChild()
    {
        var startInfo = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("No test executable path."))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--child");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the unrelated test child.");
    }

    private static void StopChild(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill();
            process.WaitForExit();
        }

        process.Dispose();
    }

    private static bool WaitForExit(uint processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static bool IsExited(uint processId) => IsExited(checked((int)processId));

    private static bool IsExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static int CountThreads(Process process)
    {
        var threads = process.Threads;
        try
        {
            return threads.Count;
        }
        finally
        {
            foreach (ProcessThread thread in threads)
            {
                thread.Dispose();
            }
        }
    }

    private static void DeleteDirectory(string directory)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }
}

/// <summary>Real Win32 calls, except for the one call each test makes fail (with a chosen Windows error).</summary>
internal sealed class FaultInjectingJobProcessNativeApi : IJobProcessNativeApi
{
    private readonly IJobProcessNativeApi _real = Win32JobProcessNativeApi.Instance;

    public int? CreateJobError { get; set; }

    public int? SetInformationError { get; set; }

    public int? AssignError { get; set; }

    public int? ResumeError { get; set; }

    public int? TerminateError { get; set; }

    public uint? WaitResultOverride { get; set; }

    public SafeJobObjectHandle? CreatedJobHandle { get; private set; }

    public uint? CreatedProcessId { get; private set; }

    public SafeJobObjectHandle CreateJobObject(IntPtr jobAttributes, string? name)
    {
        if (CreateJobError is { } error)
        {
            // An invalid handle, exactly what a failed CreateJobObjectW returns.
            CreatedJobHandle = new SafeJobObjectHandle();
            Marshal.SetLastPInvokeError(error);
            return CreatedJobHandle;
        }

        CreatedJobHandle = _real.CreateJobObject(jobAttributes, name);
        return CreatedJobHandle;
    }

    public bool SetInformationJobObject(SafeJobObjectHandle job, int informationClass, ref JobObjectExtendedLimitInformation information, uint informationLength)
    {
        if (SetInformationError is { } error)
        {
            Marshal.SetLastPInvokeError(error);
            return false;
        }

        return _real.SetInformationJobObject(job, informationClass, ref information, informationLength);
    }

    public bool AssignProcessToJobObject(SafeJobObjectHandle job, SafeProcessHandle process)
    {
        if (AssignError is { } error)
        {
            Marshal.SetLastPInvokeError(error);
            return false;
        }

        return _real.AssignProcessToJobObject(job, process);
    }

    public bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation)
    {
        var created = _real.CreateProcess(applicationName, commandLine, processAttributes, threadAttributes, inheritHandles, creationFlags, environment, currentDirectory, ref startupInfo, out processInformation);
        var lastError = Marshal.GetLastPInvokeError();
        if (created)
        {
            CreatedProcessId = processInformation.ProcessId;
        }

        Marshal.SetLastPInvokeError(lastError);
        return created;
    }

    public uint ResumeThread(SafeThreadObjectHandle thread)
    {
        if (ResumeError is { } error)
        {
            Marshal.SetLastPInvokeError(error);
            return uint.MaxValue;
        }

        return _real.ResumeThread(thread);
    }

    public bool TerminateProcess(SafeProcessHandle process, uint exitCode)
    {
        if (TerminateError is { } error)
        {
            Marshal.SetLastPInvokeError(error);
            return false;
        }

        return _real.TerminateProcess(process, exitCode);
    }

    public uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds)
    {
        if (WaitResultOverride is { } result)
        {
            return result;
        }

        return _real.WaitForSingleObject(handle, milliseconds);
    }
}

/// <summary>SetWinEventHook always fails with the chosen Windows error; UnhookWinEvent is only counted.</summary>
internal sealed class FailingWinEventHookApi(int error) : IWinEventHookApi
{
    private int _setCalls;
    private int _unhookCalls;

    public int SetCalls => Volatile.Read(ref _setCalls);

    public int UnhookCalls => Volatile.Read(ref _unhookCalls);

    public IntPtr SetWinEventHook(uint eventMinimum, uint eventMaximum, IntPtr eventModule, WindowNativeMethods.WinEventCallback callback, uint processId, uint threadId, uint flags)
    {
        Interlocked.Increment(ref _setCalls);
        Marshal.SetLastPInvokeError(error);
        return IntPtr.Zero;
    }

    public bool UnhookWinEvent(IntPtr hook)
    {
        Interlocked.Increment(ref _unhookCalls);
        return false;
    }
}
