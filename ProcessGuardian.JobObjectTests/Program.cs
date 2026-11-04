using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using ProcessGuardian.Services;
using System.Text.Json;
using ProcessGuardian.Models;
using ProcessGuardian.Services.Configuration;
using ProcessGuardian.Services.ApplicationLifecycle;
using ProcessGuardian;
using ProcessGuardian.Services.ManagedApplications;
using ProcessGuardian.JobObjectTests;
using ProcessGuardian.Services.ProcessManagement;
using ProcessGuardian.Services.JobObjects;
using ProcessGuardian.Services.WindowMonitoring;
using ProcessGuardian.ViewModels;
using ProcessGuardian.Services.Logging;
using System.Windows.Threading;

if (args.Length == 1 && args[0] == "--child")
{
    await Task.Delay(Timeout.Infinite);
    return 0;
}

if (args.Length == 2 && args[0] == "--launcher-child")
{
    using var readySignal = EventWaitHandle.OpenExisting(args[1]);
    readySignal.Set();
    await Task.Delay(Timeout.Infinite);
    return 0;
}

if (args.Length == 7 && args[0] == "--argument-probe")
{
    using var readySignal = EventWaitHandle.OpenExisting(args[1]);
    File.WriteAllLines(args[2], args.Skip(3));
    readySignal.Set();
    await Task.Delay(Timeout.Infinite);
    return 0;
}

if (args.Length == 7 && args[0] == "--nested-job-owner")
{
    await RunNestedJobOwnerAsync(args[1], args[2], args[3], args[4], args[5], args[6]);
    return 0;
}

if (args.Length == 2 && args[0] == "--nested-job-probe")
{
    using var readySignal = EventWaitHandle.OpenExisting(args[1]);
    readySignal.Set();
    await Task.Delay(Timeout.Infinite);
    return 0;
}

if (args.Length == 2 && args[0] == "--window-child")
{
    TestWindowNativeMethods.RunWindowChild(args[1]);
    return 0;
}

if (args.Length == 3 && args[0] == "--helper-window-child")
{
    TestWindowNativeMethods.RunWindowChild(args[1], 1, false, args[2]);
    return 0;
}

if (args.Length == 4 && args[0] == "--managed-helper-window-child")
{
    var backgroundProcesses = StartBackgroundChildren(2);
    File.WriteAllLines(args[2], backgroundProcesses.Select(process => process.Id.ToString()));
    TestWindowNativeMethods.RunWindowChild(args[1], 1, false, args[3]);
    return 0;
}

if ((args.Length == 3 || args.Length == 4) && args[0] == "--hide-on-close-window-child")
{
    // Optional 4th argument: "replace:<ms>" (the first close hides the window and a replacement main
    // window appears in the same process after <ms>), "sibling" (another Job process shows a window) or
    // "jobchild:<showEvent>" (another Job process creates a hidden, larger hide-on-close main window and
    // shows it when the test runner signals <showEvent>; Task 16).
    var mode = args.Length == 4 ? args[3] : string.Empty;
    var replacementDelayMs = mode.StartsWith("replace:", StringComparison.Ordinal) ? int.Parse(mode["replace:".Length..]) : -1;
    var backgroundProcesses = StartBackgroundChildren(2);
    var childIds = backgroundProcesses.Select(process => process.Id.ToString()).ToList();
    if (mode == "sibling")
    {
        var siblingReadyName = $"ProcessGuardianSiblingWindow_{Guid.NewGuid():N}";
        using var siblingReady = new EventWaitHandle(false, EventResetMode.ManualReset, siblingReadyName);
        var siblingStartInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        siblingStartInfo.ArgumentList.Add("--sibling-window-child");
        siblingStartInfo.ArgumentList.Add(siblingReadyName);
        using var sibling = Process.Start(siblingStartInfo)
            ?? throw new InvalidOperationException("Could not start the sibling window test process.");
        if (!siblingReady.WaitOne(5000))
        {
            throw new InvalidOperationException("The sibling window test process did not create its window.");
        }

        childIds.Add(sibling.Id.ToString());
    }

    if (mode.StartsWith("jobchild:", StringComparison.Ordinal))
    {
        var jobChildReadyName = $"ProcessGuardianJobChildWindow_{Guid.NewGuid():N}";
        using var jobChildReady = new EventWaitHandle(false, EventResetMode.ManualReset, jobChildReadyName);
        var jobChildStartInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        jobChildStartInfo.ArgumentList.Add("--job-window-child");
        jobChildStartInfo.ArgumentList.Add(jobChildReadyName);
        jobChildStartInfo.ArgumentList.Add(mode["jobchild:".Length..]);
        using var jobChild = Process.Start(jobChildStartInfo)
            ?? throw new InvalidOperationException("Could not start the Job child window test process.");
        if (!jobChildReady.WaitOne(5000))
        {
            throw new InvalidOperationException("The Job child window test process did not create its window.");
        }

        // Always the last PID in the file.
        childIds.Add(jobChild.Id.ToString());
    }

    File.WriteAllLines(args[2], childIds);
    TestWindowNativeMethods.RunWindowChild(args[1], 1, false, null, hideOnClose: true, replacementDelayMs: replacementDelayMs);
    return 0;
}

if (args.Length == 2 && args[0] == "--sibling-window-child")
{
    TestWindowNativeMethods.RunWindowChild(args[1]);
    return 0;
}

if (args.Length == 3 && args[0] == "--job-window-child")
{
    TestWindowNativeMethods.RunWindowChild(args[1], 1, false, null, hideOnClose: true, showEventName: args[2]);
    return 0;
}

if (args.Length == 1 && args[0] == "--background-child")
{
    await Task.Delay(Timeout.Infinite);
    return 0;
}

if ((args.Length == 4 || args.Length == 5) && args[0] == "--managed-window-child")
{
    var backgroundProcesses = StartBackgroundChildren(2);
    File.WriteAllLines(args[2], backgroundProcesses.Select(process => process.Id.ToString()));
    TestWindowNativeMethods.RunWindowChild(args[1], int.Parse(args[3]), args.Length == 5 && args[4] == "vary");
    return 0;
}

if (args.Length == 3 && args[0] == "--managed-exit-child")
{
    var backgroundProcesses = StartBackgroundChildren(2);
    File.WriteAllLines(args[2], backgroundProcesses.Select(process => process.Id.ToString()));
    using var readySignal = EventWaitHandle.OpenExisting(args[1]);
    readySignal.Set();
    return 0;
}

if (args.Length == 2 && args[0] == AppUserModelIdTests.ChildMode)
{
    return AppUserModelIdTests.RunChild(args[1]);
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("These tests require Windows.");
    return 2;
}

// Every ProcessGuardian.exe started by this run inherits this variable, so config.json and Logs go to a
// per-run folder instead of the real %LOCALAPPDATA%\ProcessGuardian, which must stay byte-for-byte unchanged.
var realDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessGuardian");
var realDataSnapshot = SnapshotDirectory(realDataDirectory);
var isolatedDataDirectory = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", $"ProcessGuardianData_{Guid.NewGuid():N}");
Environment.SetEnvironmentVariable(AppDataPaths.OverrideVariable, isolatedDataDirectory);
Console.WriteLine($"INFO: {AppDataPaths.OverrideVariable}={isolatedDataDirectory}");

try
{
    using (var emptyJob = new JobObjectManager())
    {
        Console.WriteLine("PASS: create Job Object and set KILL_ON_JOB_CLOSE");

        using var child = StartChild();
        emptyJob.AssignProcess(child);
        if (emptyJob.ActiveProcessCount < 1)
        {
            throw new InvalidOperationException("The active Job Object process count did not include its test child.");
        }

        Console.WriteLine("PASS: assign dedicated child process");

        emptyJob.Terminate(exitCode: 23);
        if (!child.WaitForExit(5000))
        {
            throw new InvalidOperationException("Child process did not exit after TerminateJobObject.");
        }

        Console.WriteLine($"PASS: TerminateJobObject ended child (exit code {child.ExitCode})");
    }

    var closeJob = new JobObjectManager();
    using var closeChild = StartChild();
    closeJob.AssignProcess(closeChild);
    closeJob.Dispose(); // This is the last Job Object handle owned by this test.
    closeJob.Dispose();

    if (!closeChild.WaitForExit(5000))
    {
        closeChild.Kill();
        throw new InvalidOperationException("Child process did not exit when the last Job Object handle closed.");
    }

    Console.WriteLine("PASS: closing the last Job Object handle automatically ended child");

    var unrelatedProcess = StartChild();
    try
    {
        using var isolatedJob = new JobObjectManager();
        using var assignedProcess = StartChild();
        isolatedJob.AssignProcess(assignedProcess);
        isolatedJob.Dispose();
        if (!assignedProcess.WaitForExit(5000) || unrelatedProcess.HasExited)
        {
            throw new InvalidOperationException("Closing a Job Object affected a process outside that Job.");
        }

        Console.WriteLine("PASS: closing a Job Object terminates its member but preserves an unrelated process");
    }
    finally
    {
        StopTestChild(unrelatedProcess);
        unrelatedProcess.Dispose();
    }

    var launcher = new ProcessLauncher();
    var executablePath = Environment.ProcessPath
        ?? throw new InvalidOperationException("Could not determine the test executable path.");
    var readyName = $"ProcessGuardianReady_{Guid.NewGuid():N}";
    using (var readySignal = new EventWaitHandle(false, EventResetMode.ManualReset, readyName))
    using (var launched = launcher.Launch(executablePath, $"--launcher-child {readyName}", Path.GetTempPath()))
    using (var launchedProcess = Process.GetProcessById((int)launched.ProcessId))
    {
        if (!readySignal.WaitOne(5000))
        {
            throw new InvalidOperationException("The launched test child did not signal that it had started.");
        }

        if (launchedProcess.HasExited)
        {
            throw new InvalidOperationException("The launched test child exited unexpectedly.");
        }

        Console.WriteLine("PASS: ProcessLauncher started the test child and it ran its startup code");

        if (!launched.JobObject.ContainsProcess(launchedProcess))
        {
            throw new InvalidOperationException("The launched process was not found in its Job Object.");
        }

        Console.WriteLine("PASS: launched process belongs to its Job Object");

        launched.Dispose();
        launched.Dispose();
        if (!launchedProcess.WaitForExit(5000))
        {
            throw new InvalidOperationException("The launched child did not exit when its Job Object closed.");
        }

        Console.WriteLine("PASS: closing ProcessLaunchResult's Job Object ended the launched child");
    }

    try
    {
        launcher.Launch(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"));
        throw new InvalidOperationException("Launching a missing executable unexpectedly succeeded.");
    }
    catch (FileNotFoundException)
    {
        Console.WriteLine("PASS: missing executable is rejected before creating a process");
    }

    var failedLaunchReadyName = $"ProcessGuardianReady_{Guid.NewGuid():N}";
    using (var failureSignal = new EventWaitHandle(false, EventResetMode.ManualReset, failedLaunchReadyName))
    {
        try
        {
            var missingWorkingDirectory = Path.Combine(Path.GetTempPath(), $"missing-dir-{Guid.NewGuid():N}");
            launcher.Launch(executablePath, $"--launcher-child {failedLaunchReadyName}", missingWorkingDirectory);
            throw new InvalidOperationException("Launching with a missing working directory unexpectedly succeeded.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (failureSignal.WaitOne(250))
            {
                throw new InvalidOperationException("A process was left running after CreateProcessW failed.");
            }

            Console.WriteLine("PASS: CreateProcessW failure left no launched test child");
        }
    }

    using (var failedStartService = new ManagedApplicationService())
    {
        var failedStart = failedStartService.Start(
            "Missing test executable",
            Path.Combine(Path.GetTempPath(), $"missing-managed-{Guid.NewGuid():N}.exe"));
        if (failedStart.Status != ManagedApplicationStatus.Failed || failedStart.Error is null || failedStart.ProcessId != 0)
        {
            throw new InvalidOperationException("A failed launch was not reported with Failed status.");
        }

        Console.WriteLine("PASS: ManagedApplicationService reports launch failures without creating a process");
    }

    var argumentTestDirectory = Path.Combine(
        Environment.CurrentDirectory,
        ".test-artifacts",
        $"ProcessGuardianArguments_{Guid.NewGuid():N}");
    Directory.CreateDirectory(argumentTestDirectory);
    var argumentResultFile = Path.Combine(argumentTestDirectory, "received arguments.txt");
    var argumentReadyName = $"ProcessGuardianArgumentReady_{Guid.NewGuid():N}";
    var expectedArguments = new[] { "with spaces", "embedded\"quote", "ends with " + '\\', "&|<> $value" };
    var argumentText = $"--argument-probe {argumentReadyName} {QuoteWindowsArgumentForTest(argumentResultFile)} "
        + string.Join(' ', expectedArguments.Select(QuoteWindowsArgumentForTest));
    using (var argumentReady = new EventWaitHandle(false, EventResetMode.ManualReset, argumentReadyName))
    using (var argumentLaunch = launcher.Launch(executablePath, argumentText, workingDirectory: "  "))
    {
        if (!argumentReady.WaitOne(5000))
        {
            throw new InvalidOperationException("The command-line argument probe did not start.");
        }

        var actualArguments = File.ReadAllLines(argumentResultFile);
        if (!actualArguments.SequenceEqual(expectedArguments, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"CreateProcessW argument parsing changed values. Expected [{string.Join(" | ", expectedArguments)}], got [{string.Join(" | ", actualArguments)}].");
        }

        Console.WriteLine("PASS: CreateProcessW preserves spaces, quotes, trailing backslashes, and shell metacharacters in raw arguments");
    }

    try
    {
        launcher.Launch(executablePath, "--argument-probe\0 --child");
        throw new InvalidOperationException("A command line containing a null character unexpectedly launched.");
    }
    catch (ArgumentException)
    {
        Console.WriteLine("PASS: embedded null characters are rejected before process creation");
    }

    try
    {
        Directory.Delete(argumentTestDirectory, recursive: true);
    }
    catch
    {
        // Keep test outcomes independent from best-effort artifact cleanup.
    }

    await RunNestedJobCompatibilityTest(executablePath);
    await RunLoggingTestsAsync();

    var windowReadyName = $"ProcessGuardianWindowReady_{Guid.NewGuid():N}";
    using (var windowReady = new EventWaitHandle(false, EventResetMode.ManualReset, windowReadyName))
    using (var windowChild = StartWindowChild(windowReadyName))
    {
        if (!windowReady.WaitOne(5000))
        {
            StopTestChild(windowChild);
            throw new InvalidOperationException("The dedicated Win32 test window did not start.");
        }

        WindowMonitor? monitor = null;
        try
        {
            monitor = new WindowMonitor((uint)windowChild.Id);
            if (monitor.Status != WindowMonitorStatus.Monitoring || monitor.MainWindowHandle == IntPtr.Zero
                || !monitor.MainWindowIsVisible || string.IsNullOrWhiteSpace(monitor.MainWindowTitle))
            {
                throw new InvalidOperationException($"Window selection was not reliable (status: {monitor.Status}).");
            }

            Console.WriteLine("PASS: monitor found the visible top-level test window");

            var closed = new TaskCompletionSource<WindowMonitorStatusChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            monitor.StatusChanged += (_, eventArgs) =>
            {
                if (eventArgs.Status == WindowMonitorStatus.WindowClosed)
                {
                    closed.TrySetResult(eventArgs);
                }
            };

            if (!TestWindowNativeMethods.PostMessage(monitor.MainWindowHandle, 0x0010, UIntPtr.Zero, IntPtr.Zero))
            {
                throw new System.ComponentModel.Win32Exception("Could not post WM_CLOSE to the dedicated test window.");
            }

            var closedEvent = closed.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (closedEvent.WindowHandle == IntPtr.Zero || windowChild.HasExited)
            {
                throw new InvalidOperationException("Window close was not reported while the target process remained alive.");
            }

            Console.WriteLine("PASS: EVENT_OBJECT_DESTROY reported the test window closing");
        }
        finally
        {
            monitor?.Dispose();
            StopTestChild(windowChild);
        }
    }

    var exitReadyName = $"ProcessGuardianExitReady_{Guid.NewGuid():N}";
    using (var exitReady = new EventWaitHandle(false, EventResetMode.ManualReset, exitReadyName))
    using (var exitChild = StartWindowChild(exitReadyName))
    {
        if (!exitReady.WaitOne(5000))
        {
            StopTestChild(exitChild);
            throw new InvalidOperationException("The process-exit test window did not start.");
        }

        WindowMonitor? monitor = null;
        try
        {
            monitor = new WindowMonitor((uint)exitChild.Id);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            monitor.StatusChanged += (_, eventArgs) =>
            {
                if (eventArgs.Status == WindowMonitorStatus.ProcessExited)
                {
                    exited.TrySetResult();
                }
            };

            exitChild.Kill();
            exitChild.WaitForExit();
            exited.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (!SpinWait.SpinUntil(() => !monitor.IsHookInstalled, TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException("The WinEvent hook remained installed after the target process exited.");
            }

            if (monitor.Status != WindowMonitorStatus.ProcessExited)
            {
                throw new InvalidOperationException($"Expected ProcessExited status, got {monitor.Status}.");
            }

            Console.WriteLine("PASS: target process exit was reported and monitoring ended");
        }
        finally
        {
            monitor?.Dispose();
            StopTestChild(exitChild);
        }
    }

    var disposeReadyName = $"ProcessGuardianDisposeReady_{Guid.NewGuid():N}";
    using (var disposeReady = new EventWaitHandle(false, EventResetMode.ManualReset, disposeReadyName))
    using (var disposeChild = StartWindowChild(disposeReadyName))
    {
        if (!disposeReady.WaitOne(5000))
        {
            StopTestChild(disposeChild);
            throw new InvalidOperationException("The hook-disposal test window did not start.");
        }

        WindowMonitor? monitor = null;
        var callbacksAfterDispose = 0;
        var disposeReturned = 0;
        try
        {
            monitor = new WindowMonitor((uint)disposeChild.Id);
            var window = monitor.MainWindowHandle;
            monitor.StatusChanged += (_, _) =>
            {
                if (Volatile.Read(ref disposeReturned) != 0)
                {
                    Interlocked.Increment(ref callbacksAfterDispose);
                }
            };
            monitor.Dispose();
            Volatile.Write(ref disposeReturned, 1);

            if (monitor.IsHookInstalled || monitor.Status != WindowMonitorStatus.Stopped)
            {
                throw new InvalidOperationException("Dispose did not release the WinEvent hook and stop monitoring.");
            }

            if (!TestWindowNativeMethods.PostMessage(window, 0x0010, UIntPtr.Zero, IntPtr.Zero))
            {
                throw new System.ComponentModel.Win32Exception("Could not close the test window after disposing its monitor.");
            }

            Thread.Sleep(500);
            if (Volatile.Read(ref callbacksAfterDispose) != 0)
            {
                throw new InvalidOperationException("A monitor callback was delivered after Dispose returned.");
            }

            Console.WriteLine("PASS: Dispose unhooked WinEvent monitoring and suppressed later callbacks");
        }
        finally
        {
            monitor?.Dispose();
            StopTestChild(disposeChild);
        }
    }

    RunHiddenHelperWindowMonitorTests();

    var managedPidFileOne = Path.Combine(Path.GetTempPath(), $"ProcessGuardian-managed-{Guid.NewGuid():N}.txt");
    var managedPidFileTwo = Path.Combine(Path.GetTempPath(), $"ProcessGuardian-managed-{Guid.NewGuid():N}.txt");
    var managedReadyOne = $"ProcessGuardianManagedReady_{Guid.NewGuid():N}";
    var managedReadyTwo = $"ProcessGuardianManagedReady_{Guid.NewGuid():N}";
    using (var managedService = new ManagedApplicationService())
    using (var readyOne = new EventWaitHandle(false, EventResetMode.ManualReset, managedReadyOne))
    using (var readyTwo = new EventWaitHandle(false, EventResetMode.ManualReset, managedReadyTwo))
    {
        var first = managedService.Start(
            "First test software",
            executablePath,
            $"--managed-window-child {managedReadyOne} {QuoteArgument(managedPidFileOne)} 1",
            Path.GetTempPath());
        var second = managedService.Start(
            "Second test software",
            executablePath,
            $"--managed-window-child {managedReadyTwo} {QuoteArgument(managedPidFileTwo)} 1",
            Path.GetTempPath());

        if (!readyOne.WaitOne(5000) || !readyTwo.WaitOne(5000))
        {
            throw new InvalidOperationException("One or both managed test applications failed to start their windows.");
        }

        var firstPids = ReadChildPids(managedPidFileOne);
        var secondPids = ReadChildPids(managedPidFileTwo);
        WaitForCondition(() => first.Status == ManagedApplicationStatus.Running
            && second.Status == ManagedApplicationStatus.Running, "both applications to enter Running state");

        if (ReferenceEquals(first.LaunchResult!.JobObject, second.LaunchResult!.JobObject))
        {
            throw new InvalidOperationException("The two applications unexpectedly share one Job Object.");
        }

        AssertJobMembership(first, firstPids);
        AssertJobMembership(second, secondPids);
        if (JobContainsAny(first, secondPids) || JobContainsAny(second, firstPids))
        {
            throw new InvalidOperationException("One application's Job Object contains the other application's processes.");
        }

        Console.WriteLine("PASS: two managed instances each contain their main process and two children in separate Jobs");

        CloseManagedWindow(first);
        WaitForCondition(() => first.Status == ManagedApplicationStatus.Completed, "first application cleanup");
        WaitForProcessesExit(new[] { checked((int)first.ProcessId) }.Concat(firstPids), "first application's process tree");
        managedService.Stop(first);
        first.LaunchResult!.Dispose();
        AssertProcessesAlive(new[] { checked((int)second.ProcessId) }.Concat(secondPids), "second application's process tree");
        Console.WriteLine("PASS: repeated Stop and Dispose are safe; the other application's process tree remains alive");

        CloseManagedWindow(second);
        try
        {
            WaitForCondition(() => second.Status == ManagedApplicationStatus.Completed, "second application cleanup");
        }
        catch (TimeoutException exception)
        {
            var monitor = second.WindowMonitor;
            var handle = monitor?.MainWindowHandle ?? IntPtr.Zero;
            throw new InvalidOperationException(
                $"Second app did not clean after its window close: app={second.Status}, processExited={second.PrimaryProcessExited}, monitor={monitor?.Status}, hook={monitor?.IsHookInstalled}, hwnd=0x{handle.ToInt64():X}, hwndValid={(handle != IntPtr.Zero && TestWindowNativeMethods.IsWindow(handle))}, visible={monitor?.MainWindowIsVisible}, monitoringError={monitor?.MonitoringException}, callbackError={monitor?.CallbackException}, appError={second.Error}, jobMembers={second.LaunchResult?.JobObject.ActiveProcessCount}.",
                exception);
        }
        WaitForProcessesExit(new[] { checked((int)second.ProcessId) }.Concat(secondPids), "second application's process tree");
        Console.WriteLine("PASS: closing the second window ended its complete process tree");
    }

    var retainedWindowPidFile = Path.Combine(Path.GetTempPath(), $"ProcessGuardian-retained-window-{Guid.NewGuid():N}.txt");
    var retainedWindowReadyName = $"ProcessGuardianRetainedWindow_{Guid.NewGuid():N}";
    using (var retainedWindowService = new ManagedApplicationService())
    using (var retainedWindowReady = new EventWaitHandle(false, EventResetMode.ManualReset, retainedWindowReadyName))
    {
        var retainedWindowApp = retainedWindowService.Start(
            "Multi-window safety test",
            executablePath,
            $"--managed-window-child {retainedWindowReadyName} {QuoteArgument(retainedWindowPidFile)} 2 vary",
            Path.GetTempPath());
        if (!retainedWindowReady.WaitOne(5000))
        {
            throw new InvalidOperationException("The multi-window safety process did not create its windows.");
        }

        var childPids = ReadChildPids(retainedWindowPidFile);
        WaitForCondition(() => retainedWindowApp.WindowMonitor?.Status == WindowMonitorStatus.Monitoring,
            "multi-window safety monitor selection");
        var monitor = retainedWindowApp.WindowMonitor!;
        var originallySelectedWindow = monitor.MainWindowHandle;
        if (!TestWindowNativeMethods.PostMessage(originallySelectedWindow, 0x0010, UIntPtr.Zero, IntPtr.Zero))
        {
            throw new InvalidOperationException("Could not close the selected window in the multi-window safety test.");
        }

        WaitForCondition(
            () => monitor.Status == WindowMonitorStatus.Monitoring
                && monitor.MainWindowHandle != IntPtr.Zero
                && monitor.MainWindowHandle != originallySelectedWindow,
            "monitor to select the remaining top-level window without closing the process tree");
        AssertProcessesAlive(new[] { checked((int)retainedWindowApp.ProcessId) }.Concat(childPids),
            "multi-window process tree after its first top-level window closes");
        if (retainedWindowApp.Status != ManagedApplicationStatus.Running)
        {
            throw new InvalidOperationException("Closing one of multiple windows incorrectly stopped the managed application.");
        }

        Console.WriteLine("PASS: closing one of multiple eligible windows preserves the process tree and tracks the remaining window");

        CloseManagedWindow(retainedWindowApp);
        WaitForCondition(() => retainedWindowApp.Status == ManagedApplicationStatus.Completed,
            "cleanup after all eligible windows close");
        WaitForProcessesExit(new[] { checked((int)retainedWindowApp.ProcessId) }.Concat(childPids),
            "multi-window process tree after its last eligible window closes");
        Console.WriteLine("PASS: closing the last eligible window still cleans the managed process tree");
    }

    var helperPidFile = Path.Combine(Path.GetTempPath(), $"ProcessGuardian-helper-window-{Guid.NewGuid():N}.txt");
    var helperHandleFile = Path.Combine(Path.GetTempPath(), $"ProcessGuardian-helper-handles-{Guid.NewGuid():N}.txt");
    var helperReadyName = $"ProcessGuardianHelperWindow_{Guid.NewGuid():N}";
    using (var helperService = new ManagedApplicationService())
    using (var helperReady = new EventWaitHandle(false, EventResetMode.ManualReset, helperReadyName))
    {
        var helperApp = helperService.Start(
            "Hidden helper window service test",
            executablePath,
            $"--managed-helper-window-child {helperReadyName} {QuoteArgument(helperPidFile)} {QuoteArgument(helperHandleFile)}",
            Path.GetTempPath());
        if (!helperReady.WaitOne(5000))
        {
            throw new InvalidOperationException("The hidden-helper service test process did not create its windows.");
        }

        var childPids = ReadChildPids(helperPidFile);
        var (_, helperWindow) = ReadHelperWindowHandles(helperHandleFile);
        WaitForCondition(() => helperApp.Status == ManagedApplicationStatus.Running
            && helperApp.WindowMonitor?.Status == WindowMonitorStatus.Monitoring, "hidden-helper service app to enter Running");
        var tree = new[] { checked((int)helperApp.ProcessId) }.Concat(childPids).ToArray();

        CloseManagedWindow(helperApp);
        WaitForCondition(() => helperApp.WindowMonitor?.Status == WindowMonitorStatus.NoReliableWindow
            && helperApp.Status == ManagedApplicationStatus.MonitoringUnavailable,
            "service to report MonitoringUnavailable while the hidden helper window remains");
        Thread.Sleep(1000);
        AssertProcessesAlive(tree, "hidden-helper application's process tree while its helper window remains");
        if (helperApp.Status != ManagedApplicationStatus.MonitoringUnavailable)
        {
            throw new InvalidOperationException($"The service left MonitoringUnavailable while a hidden window remained (status: {helperApp.Status}).");
        }

        if (!TestWindowNativeMethods.PostMessage(helperWindow, 0x0010, UIntPtr.Zero, IntPtr.Zero))
        {
            throw new InvalidOperationException("Could not close the hidden helper window in the service test.");
        }

        WaitForCondition(() => helperApp.Status == ManagedApplicationStatus.Completed,
            "cleanup after the hidden helper window is destroyed");
        WaitForProcessesExit(tree, "hidden-helper application's process tree after all its windows closed");
        Console.WriteLine("PASS: service keeps the tree while a hidden helper window remains, then completes cleanup after it is destroyed");
    }

    var closeRacePidFile = Path.Combine(Path.GetTempPath(), $"ProcessGuardian-close-race-{Guid.NewGuid():N}.txt");
    var closeRaceReadyName = $"ProcessGuardianCloseRace_{Guid.NewGuid():N}";
    using (var closeRaceService = new ManagedApplicationService())
    using (var closeRaceReady = new EventWaitHandle(false, EventResetMode.ManualReset, closeRaceReadyName))
    {
        var closeRaceApp = closeRaceService.Start(
            "Window-close process-exit race test",
            executablePath,
            $"--managed-window-child {closeRaceReadyName} {QuoteArgument(closeRacePidFile)} 1",
            Path.GetTempPath());
        if (!closeRaceReady.WaitOne(5000))
        {
            throw new InvalidOperationException("The close-race test process did not create its window.");
        }

        var childPids = ReadChildPids(closeRacePidFile);
        var raceWindow = closeRaceApp.WindowMonitor?.MainWindowHandle
            ?? throw new InvalidOperationException("The close-race test has no selected window.");
        using var processToExit = Process.GetProcessById(checked((int)closeRaceApp.ProcessId));
        if (!TestWindowNativeMethods.PostMessage(raceWindow, 0x0010, UIntPtr.Zero, IntPtr.Zero))
        {
            throw new InvalidOperationException("Could not post a close request in the race test.");
        }

        try
        {
            if (!processToExit.HasExited)
            {
                processToExit.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // The window-close cleanup may already have ended the test process.
        }

        WaitForCondition(() => closeRaceApp.Status == ManagedApplicationStatus.Completed,
            "simultaneous window close and process exit cleanup");
        WaitForProcessesExit(new[] { checked((int)closeRaceApp.ProcessId) }.Concat(childPids),
            "window-close/process-exit race test process tree");
        closeRaceService.Stop(closeRaceApp);
        Console.WriteLine("PASS: window destruction and process exit racing trigger one safe cleanup");
    }

    var earlyExitPidFile = Path.Combine(Path.GetTempPath(), $"ProcessGuardian-early-{Guid.NewGuid():N}.txt");
    var earlyExitReadyName = $"ProcessGuardianEarlyReady_{Guid.NewGuid():N}";
    using (var earlyExitService = new ManagedApplicationService())
    using (var earlyExitReady = new EventWaitHandle(false, EventResetMode.ManualReset, earlyExitReadyName))
    {
        var earlyExit = earlyExitService.Start(
            "Early exit test software",
            executablePath,
            $"--managed-exit-child {earlyExitReadyName} {QuoteArgument(earlyExitPidFile)}",
            Path.GetTempPath());

        if (!earlyExitReady.WaitOne(5000))
        {
            throw new InvalidOperationException("The early-exit test process did not report its child processes.");
        }

        var childPids = ReadChildPids(earlyExitPidFile);
        WaitForCondition(() => earlyExit.Status == ManagedApplicationStatus.Completed, "early process exit cleanup");
        WaitForProcessesExit(childPids, "children of the early-exited process");
        Console.WriteLine("PASS: natural main-process exit stopped monitoring and closed its Job to end both children");
    }

    var ambiguousPidFile = Path.Combine(Path.GetTempPath(), $"ProcessGuardian-ambiguous-{Guid.NewGuid():N}.txt");
    var ambiguousReadyName = $"ProcessGuardianAmbiguousReady_{Guid.NewGuid():N}";
    using (var ambiguousService = new ManagedApplicationService())
    using (var ambiguousReady = new EventWaitHandle(false, EventResetMode.ManualReset, ambiguousReadyName))
    {
        var ambiguous = ambiguousService.Start(
            "Ambiguous window test software",
            executablePath,
            $"--managed-window-child {ambiguousReadyName} {QuoteArgument(ambiguousPidFile)} 2",
            Path.GetTempPath());

        if (!ambiguousReady.WaitOne(5000))
        {
            throw new InvalidOperationException("The ambiguous-window test process did not create its windows.");
        }

        var childPids = ReadChildPids(ambiguousPidFile);
        WaitForCondition(() => ambiguous.Status == ManagedApplicationStatus.MonitoringUnavailable,
            "ambiguous window monitoring state");

        if (ambiguous.WindowMonitor?.Status != WindowMonitorStatus.AmbiguousWindows)
        {
            throw new InvalidOperationException($"Expected AmbiguousWindows, got {ambiguous.WindowMonitor?.Status}.");
        }

        AssertProcessesAlive(new[] { checked((int)ambiguous.ProcessId) }.Concat(childPids), "ambiguous-window application's process tree");
        AssertJobMembership(ambiguous, childPids);
        Console.WriteLine("PASS: ambiguous main-window selection reported MonitoringUnavailable without killing the app");

        ambiguousService.Dispose();
        WaitForProcessesExit(new[] { checked((int)ambiguous.ProcessId) }.Concat(childPids), "ambiguous-window application after explicit service disposal");
        Console.WriteLine("PASS: explicit service disposal released the retained Job and cleaned the unavailable-monitor app");
    }

    RunHideAsCloseTests(executablePath);
    await RunHideAsCloseConfigurationTestAsync();
    await RunConfigurationTransientLockTestAsync();
    var guardianExecutable = FindGuardianExecutable();
    RunRepeatedManagedApplicationLifecycleTest(executablePath);
    RunViewModelWorkflowTest(guardianExecutable, executablePath);
    RunRealTrayIconRegistrationTest();
    RunTrayCommandRouterTest();
    await RunApplicationExitCoordinatorTest(executablePath);
    // Stage 12: abnormal-scenario fault injection (added tests only; existing tests above are unchanged).
    FaultInjectionTests.RunLauncherFaultTests(executablePath);
    FaultInjectionTests.RunServiceAssignFailureTest(executablePath);
    FaultInjectionTests.RunWindowMonitorHookFailureTest();
    FaultInjectionTests.RunStaTests(executablePath);
    await StartupFailureTests.RunAsync();
    await CommandLineTests.RunAsync();
    DesktopShortcutTests.Run();
    await SingleInstanceTests.RunAsync(guardianExecutable, executablePath);
    await ExternalInstanceTests.RunAsync(guardianExecutable, executablePath);
    await AppUserModelIdTests.RunAsync(guardianExecutable, executablePath);
    await AutoStartTests.RunAsync(guardianExecutable, executablePath);
    AssertNoDedicatedTestChildrenRemain();

    Console.WriteLine("PASS: no dedicated test child process remains after the full workflow");
    var isolatedEntries = Directory.Exists(isolatedDataDirectory)
        ? Directory.GetFileSystemEntries(isolatedDataDirectory, "*", SearchOption.AllDirectories).Length
        : 0;
    Console.WriteLine($"INFO: isolated data directory held {isolatedEntries} entries before cleanup");
    DeleteDirectoryWithRetry(isolatedDataDirectory);
    AssertDirectoryUnchanged(realDataDirectory, realDataSnapshot);
    Console.WriteLine("PASS: the real LocalAppData\\ProcessGuardian folder is unchanged (listing and SHA-256) after the full run");
    Console.WriteLine("All Job Object, process-launch, window-monitor, managed-application, WPF, tray, and exit workflow checks passed.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL: {exception}");
    try
    {
        DeleteDirectoryWithRetry(isolatedDataDirectory);
    }
    catch (Exception cleanupException)
    {
        Console.Error.WriteLine($"CLEANUP-FAIL: could not remove {isolatedDataDirectory}: {cleanupException.Message}");
    }

    return 1;
}

static Dictionary<string, string> SnapshotDirectory(string directory)
{
    var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (!Directory.Exists(directory))
    {
        return snapshot;
    }

    foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
    {
        var relativePath = Path.GetRelativePath(directory, entry);
        if (Directory.Exists(entry))
        {
            snapshot[relativePath] = "<dir>";
            continue;
        }

        using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        snapshot[relativePath] = $"{stream.Length}:{Convert.ToHexString(SHA256.HashData(stream))}";
    }

    return snapshot;
}

static void AssertDirectoryUnchanged(string directory, IReadOnlyDictionary<string, string> before)
{
    var after = SnapshotDirectory(directory);
    var differences = before.Keys
        .Union(after.Keys, StringComparer.OrdinalIgnoreCase)
        .Where(path => !before.TryGetValue(path, out var oldValue)
            || !after.TryGetValue(path, out var newValue)
            || oldValue != newValue)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (differences.Length > 0)
    {
        throw new InvalidOperationException($"The test run modified {directory}: {string.Join(", ", differences)}");
    }
}

static void DeleteDirectoryWithRetry(string directory)
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

static Process StartChild()
{
    var executable = Environment.ProcessPath
        ?? throw new InvalidOperationException("Could not determine the test executable path.");

    var startInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add("--child");

    return Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start the dedicated test child process.");
}

static Process StartWindowChild(string readyEventName)
{
    var executable = Environment.ProcessPath
        ?? throw new InvalidOperationException("Could not determine the test executable path.");

    var startInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add("--window-child");
    startInfo.ArgumentList.Add(readyEventName);

    return Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start the dedicated Win32 window test process.");
}

static Process StartHelperWindowChild(string readyEventName, string handleFile)
{
    var executable = Environment.ProcessPath
        ?? throw new InvalidOperationException("Could not determine the test executable path.");

    var startInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add("--helper-window-child");
    startInfo.ArgumentList.Add(readyEventName);
    startInfo.ArgumentList.Add(handleFile);

    return Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start the hidden-helper window test process.");
}

static (IntPtr Visible, IntPtr Helper) ReadHelperWindowHandles(string path)
{
    var lines = File.ReadAllLines(path);
    File.Delete(path);
    return (new IntPtr(long.Parse(lines[0])), new IntPtr(long.Parse(lines[1])));
}

static void RunHiddenHelperWindowMonitorTests()
{
    // (a) + (b): a hidden helper top-level window outlives the visible window.
    var readyName = $"ProcessGuardianHelperReady_{Guid.NewGuid():N}";
    var handleFile = Path.Combine(Path.GetTempPath(), $"ProcessGuardian-helper-{Guid.NewGuid():N}.txt");
    using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName))
    using (var child = StartHelperWindowChild(readyName, handleFile))
    {
        WindowMonitor? monitor = null;
        try
        {
            if (!ready.WaitOne(5000))
            {
                throw new InvalidOperationException("The hidden-helper test window did not start.");
            }

            var (visibleWindow, helperWindow) = ReadHelperWindowHandles(handleFile);
            monitor = new WindowMonitor((uint)child.Id);
            if (monitor.Status != WindowMonitorStatus.Monitoring || monitor.MainWindowHandle != visibleWindow)
            {
                throw new InvalidOperationException($"The visible window was not selected next to a hidden helper (status: {monitor.Status}).");
            }

            var closedEvents = 0;
            var closed = new TaskCompletionSource<WindowMonitorStatusChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            monitor.StatusChanged += (_, eventArgs) =>
            {
                if (eventArgs.Status == WindowMonitorStatus.WindowClosed)
                {
                    Interlocked.Increment(ref closedEvents);
                    closed.TrySetResult(eventArgs);
                }
            };

            if (!TestWindowNativeMethods.PostMessage(visibleWindow, 0x0010, UIntPtr.Zero, IntPtr.Zero))
            {
                throw new System.ComponentModel.Win32Exception("Could not close the visible window of the hidden-helper test.");
            }

            var activeMonitor = monitor;
            WaitForCondition(() => activeMonitor.Status == WindowMonitorStatus.NoReliableWindow && !TestWindowNativeMethods.IsWindow(visibleWindow),
                "NoReliableWindow after the visible window closed next to a hidden helper");
            Thread.Sleep(1500);
            if (monitor.Status != WindowMonitorStatus.NoReliableWindow || Volatile.Read(ref closedEvents) != 0
                || !TestWindowNativeMethods.IsWindow(helperWindow) || child.HasExited)
            {
                throw new InvalidOperationException($"A remaining hidden window did not keep NoReliableWindow (status: {monitor.Status}, closedEvents: {closedEvents}).");
            }

            Console.WriteLine("PASS: a remaining hidden top-level window keeps NoReliableWindow without a false WindowClosed");

            if (!TestWindowNativeMethods.PostMessage(helperWindow, 0x0010, UIntPtr.Zero, IntPtr.Zero))
            {
                throw new System.ComponentModel.Win32Exception("Could not close the hidden helper window.");
            }

            var closedEvent = closed.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (closedEvent.WindowHandle != visibleWindow || child.HasExited || monitor.Status != WindowMonitorStatus.WindowClosed)
            {
                throw new InvalidOperationException("WindowClosed did not report the destroyed main window while the process remained alive.");
            }

            Console.WriteLine("PASS: destroying the last hidden helper window after the visible close reports WindowClosed");
        }
        finally
        {
            monitor?.Dispose();
            StopTestChild(child);
            File.Delete(handleFile);
        }
    }

    // (d): after NoReliableWindow, a new visible window becomes the reliable main window.
    readyName = $"ProcessGuardianHelperReady_{Guid.NewGuid():N}";
    handleFile = Path.Combine(Path.GetTempPath(), $"ProcessGuardian-helper-{Guid.NewGuid():N}.txt");
    using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName))
    using (var child = StartHelperWindowChild(readyName, handleFile))
    {
        WindowMonitor? monitor = null;
        try
        {
            if (!ready.WaitOne(5000))
            {
                throw new InvalidOperationException("The hidden-helper reselection test window did not start.");
            }

            var (visibleWindow, helperWindow) = ReadHelperWindowHandles(handleFile);
            monitor = new WindowMonitor((uint)child.Id);
            var closedEvents = 0;
            var closed = new TaskCompletionSource<WindowMonitorStatusChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            monitor.StatusChanged += (_, eventArgs) =>
            {
                if (eventArgs.Status == WindowMonitorStatus.WindowClosed)
                {
                    Interlocked.Increment(ref closedEvents);
                    closed.TrySetResult(eventArgs);
                }
            };

            if (!TestWindowNativeMethods.PostMessage(visibleWindow, 0x0010, UIntPtr.Zero, IntPtr.Zero))
            {
                throw new System.ComponentModel.Win32Exception("Could not close the visible window of the reselection test.");
            }

            var activeMonitor = monitor;
            WaitForCondition(() => activeMonitor.Status == WindowMonitorStatus.NoReliableWindow,
                "NoReliableWindow before a new visible window appears");
            TestWindowNativeMethods.ShowTestWindow(helperWindow);
            WaitForCondition(() => activeMonitor.Status == WindowMonitorStatus.Monitoring && activeMonitor.MainWindowHandle == helperWindow,
                "Monitoring of the newly visible window");
            Thread.Sleep(600);
            if (monitor.Status != WindowMonitorStatus.Monitoring || Volatile.Read(ref closedEvents) != 0)
            {
                throw new InvalidOperationException($"A newly visible window did not stay selected (status: {monitor.Status}, closedEvents: {closedEvents}).");
            }

            Console.WriteLine("PASS: after NoReliableWindow a newly visible window returns the monitor to Monitoring");

            if (!TestWindowNativeMethods.PostMessage(helperWindow, 0x0010, UIntPtr.Zero, IntPtr.Zero))
            {
                throw new System.ComponentModel.Win32Exception("Could not close the newly selected window.");
            }

            var closedEvent = closed.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (closedEvent.WindowHandle != helperWindow || child.HasExited)
            {
                throw new InvalidOperationException("Closing the reselected window did not report WindowClosed for that window.");
            }

            Console.WriteLine("PASS: closing the reselected window reports WindowClosed for that window");
        }
        finally
        {
            monitor?.Dispose();
            StopTestChild(child);
            File.Delete(handleFile);
        }
    }
}

static List<Process> StartBackgroundChildren(int count)
{
    var executable = Environment.ProcessPath
        ?? throw new InvalidOperationException("Could not determine the test executable path.");
    var children = new List<Process>();

    try
    {
        for (var index = 0; index < count; index++)
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--background-child");
            children.Add(Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start a dedicated background test process."));
        }

        return children;
    }
    catch
    {
        foreach (var child in children)
        {
            StopTestChild(child);
            child.Dispose();
        }

        throw;
    }
}

static void StopTestChild(Process process)
{
    if (!process.HasExited)
    {
        process.Kill();
        process.WaitForExit();
    }
}

static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

static string QuoteWindowsArgumentForTest(string value)
{
    var builder = new System.Text.StringBuilder(value.Length + 2);
    builder.Append('"');
    var backslashes = 0;
    foreach (var character in value)
    {
        if (character == '\\')
        {
            backslashes++;
            continue;
        }

        if (character == '"')
        {
            builder.Append('\\', backslashes * 2 + 1);
            builder.Append('"');
        }
        else
        {
            builder.Append('\\', backslashes);
            builder.Append(character);
        }

        backslashes = 0;
    }

    builder.Append('\\', backslashes * 2);
    builder.Append('"');
    return builder.ToString();
}

static async Task RunNestedJobOwnerAsync(
    string startedEventName,
    string continueEventName,
    string completedEventName,
    string resultPath,
    string probeEventName,
    string executablePath)
{
    using var started = EventWaitHandle.OpenExisting(startedEventName);
    using var continueSignal = EventWaitHandle.OpenExisting(continueEventName);
    using var completed = EventWaitHandle.OpenExisting(completedEventName);
    started.Set();
    continueSignal.WaitOne();

    ProcessLaunchResult? nested = null;
    try
    {
        nested = new ProcessLauncher().Launch(
            executablePath,
            "--nested-job-probe " + probeEventName,
            Path.GetDirectoryName(executablePath));
        await File.WriteAllTextAsync(resultPath, "ASSIGNED:" + nested.ProcessId);
    }
    catch (System.ComponentModel.Win32Exception exception)
    {
        await File.WriteAllTextAsync(resultPath, "REJECTED:" + exception.NativeErrorCode);
    }
    catch (Exception exception)
    {
        await File.WriteAllTextAsync(resultPath, "ERROR:" + exception.GetType().Name + ":" + exception.Message);
    }

    completed.Set();
    if (nested is not null)
    {
        await Task.Delay(Timeout.Infinite);
    }

    await Task.Delay(Timeout.Infinite);
}

static async Task RunNestedJobCompatibilityTest(string executablePath)
{
    var testDirectory = Path.Combine(
        Environment.CurrentDirectory,
        ".test-artifacts",
        "ProcessGuardianNestedJobs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testDirectory);
    var resultPath = Path.Combine(testDirectory, "nested-result.txt");
    var startedName = "ProcessGuardianNestedStarted_" + Guid.NewGuid().ToString("N");
    var continueName = "ProcessGuardianNestedContinue_" + Guid.NewGuid().ToString("N");
    var completedName = "ProcessGuardianNestedCompleted_" + Guid.NewGuid().ToString("N");
    var probeName = "ProcessGuardianNestedProbe_" + Guid.NewGuid().ToString("N");
    using var started = new EventWaitHandle(false, EventResetMode.ManualReset, startedName);
    using var continueSignal = new EventWaitHandle(false, EventResetMode.ManualReset, continueName);
    using var completed = new EventWaitHandle(false, EventResetMode.ManualReset, completedName);
    using var probe = new EventWaitHandle(false, EventResetMode.ManualReset, probeName);
    using var outerJob = new JobObjectManager();

    var ownerArguments = "--nested-job-owner " + startedName + " " + continueName + " " + completedName + " "
        + QuoteWindowsArgumentForTest(resultPath) + " " + probeName + " "
        + QuoteWindowsArgumentForTest(executablePath);
    var commandLine = new System.Text.StringBuilder(
        QuoteWindowsArgumentForTest(executablePath) + " " + ownerArguments);
    var startupInfo = new TestStartupInfo
    {
        Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<TestStartupInfo>(),
    };

    if (!TestProcessNativeMethods.CreateProcess(
        executablePath,
        commandLine,
        IntPtr.Zero,
        IntPtr.Zero,
        inheritHandles: false,
        TestProcessNativeMethods.CreateSuspended,
        IntPtr.Zero,
        Path.GetDirectoryName(executablePath),
        ref startupInfo,
        out var processInformation))
    {
        throw new System.ComponentModel.Win32Exception(
            System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
            "Could not create the suspended outer-Job compatibility process.");
    }

    Process? ownerProcess = null;
    Process? nestedProcess = null;
    var resumed = false;
    var outerAssignmentFailed = false;
    try
    {
        ownerProcess = Process.GetProcessById(checked((int)processInformation.ProcessId));
        try
        {
            outerJob.AssignProcess(ownerProcess);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            outerAssignmentFailed = true;
            Console.WriteLine(
                "SKIP: the current Windows job restrictions prevented creating the test outer Job (error "
                + exception.NativeErrorCode + ").");
        }

        if (!outerAssignmentFailed)
        {
            var resumeResult = TestProcessNativeMethods.ResumeThread(processInformation.ThreadHandle);
            if (resumeResult == uint.MaxValue)
            {
                throw new System.ComponentModel.Win32Exception(
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                    "Could not resume the outer-Job compatibility process.");
            }

            resumed = true;
            if (!started.WaitOne(5000))
            {
                throw new TimeoutException("The process assigned to the outer Job did not start its nested-job check.");
            }

            var baselineCount = outerJob.ActiveProcessCount;
            continueSignal.Set();
            if (!completed.WaitOne(8000))
            {
                throw new TimeoutException("The nested-job compatibility attempt did not report a result.");
            }

            var result = await File.ReadAllTextAsync(resultPath);
            if (result.StartsWith("ASSIGNED:", StringComparison.Ordinal))
            {
                var nestedPid = int.Parse(result.AsSpan("ASSIGNED:".Length));
                if (!probe.WaitOne(5000))
                {
                    try
                    {
                        nestedProcess = Process.GetProcessById(nestedPid);
                    }
                    catch (ArgumentException)
                    {
                        nestedProcess = null;
                    }

                    var detail = nestedProcess is null
                        ? "the process exited before it could be queried"
                        : nestedProcess.HasExited
                            ? "exit code " + nestedProcess.ExitCode
                            : "still running without signaling its startup event";
                    throw new InvalidOperationException("The nested process was assigned but did not report startup (" + detail + ").");
                }

                nestedProcess = Process.GetProcessById(nestedPid);

                if (!outerJob.ContainsProcess(ownerProcess) || !outerJob.ContainsProcess(nestedProcess))
                {
                    throw new InvalidOperationException("Nested Job assignment did not retain the launched process under the outer Job.");
                }

                Console.WriteLine("PASS: an already-jobbed process successfully launched a nested-job process on this Windows host");
            }
            else if (result.StartsWith("REJECTED:", StringComparison.Ordinal))
            {
                if (probe.WaitOne(500)
                    || !SpinWait.SpinUntil(() => outerJob.ActiveProcessCount <= baselineCount, TimeSpan.FromSeconds(5)))
                {
                    throw new InvalidOperationException("A rejected nested Job assignment left a child running in the outer Job.");
                }

                Console.WriteLine("PASS: unsupported nested Job assignment was rejected without leaving a child running");
            }
            else
            {
                throw new InvalidOperationException("Nested Job test failed unexpectedly: " + result);
            }
        }
    }
    finally
    {
        if (!resumed)
        {
            TestProcessNativeMethods.TerminateProcess(processInformation.ProcessHandle, 1);
            TestProcessNativeMethods.WaitForSingleObject(processInformation.ProcessHandle, 5000);
        }

        outerJob.Dispose();
        TestProcessNativeMethods.CloseHandle(processInformation.ThreadHandle);
        TestProcessNativeMethods.CloseHandle(processInformation.ProcessHandle);

        if (ownerProcess is not null && !ownerProcess.WaitForExit(5000))
        {
            throw new TimeoutException("The process assigned to the outer Job survived Job closure.");
        }

        if (nestedProcess is not null)
        {
            if (!nestedProcess.WaitForExit(5000))
            {
                throw new TimeoutException("The nested process survived outer Job closure.");
            }

            nestedProcess.Dispose();
        }

        ownerProcess?.Dispose();
        try
        {
            Directory.Delete(testDirectory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of isolated test artifacts.
        }
    }
}

static async Task RunLoggingTestsAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "ProcessGuardianLoggingTests_" + Guid.NewGuid().ToString("N"));
    var logDirectory = Path.Combine(root, "not-yet-created", "Logs");
    Directory.CreateDirectory(root);
    var oldLog = Path.Combine(logDirectory, "processguardian-2020-01-01.log");
    Directory.CreateDirectory(logDirectory);
    await File.WriteAllTextAsync(oldLog, "expired");
    File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-30));
    Directory.Delete(Path.Combine(root, "not-yet-created"), recursive: true);

    await using (var logger = new FileLogService(logDirectory, maximumFileBytes: 4096, retention: TimeSpan.FromDays(14)))
    {
        var writers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 50; index++)
                logger.Write(index % 5 == 0 ? LogLevel.Warning : LogLevel.Information, "test-" + worker, "entry-" + index);
        })).ToArray();
        await Task.WhenAll(writers);
        logger.Write(LogLevel.Critical, "test", "critical payload", "Sample", 123, new InvalidOperationException("diagnostic stack"));
        await logger.FlushAsync();
        var files = Directory.GetFiles(logDirectory, "processguardian-*.log");
        if (files.Length < 2 || files.Any(path => new FileInfo(path).Length > 4600))
            throw new InvalidOperationException("Date-based log creation or size rotation failed.");
        var entries = await logger.ReadRecentAsync(1000);
        if (entries.Count != 401 || entries.All(entry => entry.Message != "critical payload") || entries.First().Exception?.Contains("InvalidOperationException") != true)
            throw new InvalidOperationException($"Concurrent log entries, JSON format, or exception details were lost (count={entries.Count}, critical={entries.Count(entry => entry.Message == "critical payload")}, exception={entries.FirstOrDefault(entry => entry.Message == "critical payload")?.Exception ?? "null"}).");
        if (entries.Any(entry => entry.Timestamp.Date != DateTimeOffset.Now.Date))
            throw new InvalidOperationException("The log filename/entry date did not match the current date.");
        Console.WriteLine("PASS: concurrent levels, structured JSON, current-date files, and size rolling");

        var diagnostics = new DiagnosticsViewModel(logger, confirmClear: () => true);
        await diagnostics.RefreshAsync();
        diagnostics.SelectedLevel = "Critical";
        diagnostics.SearchText = "critical";
        if (diagnostics.Entries.Count != 1 || diagnostics.Entries[0].Exception is null)
            throw new InvalidOperationException("Diagnostics level/keyword filtering failed.");
        await logger.ClearAsync();
        if (Directory.GetFiles(logDirectory, "processguardian-*.log").Length != 0)
            throw new InvalidOperationException("Clear did not remove flushed log files safely.");
        logger.Write(LogLevel.Information, "test", "after clear");
        await logger.FlushAsync();
        if ((await logger.ReadRecentAsync()).Count != 1)
            throw new InvalidOperationException("Logging did not recover after clear.");
        Console.WriteLine("PASS: diagnostic filters/search, exception detail, and clear while logger remains active");
    }
    var postDisposeFiles = Directory.GetFiles(logDirectory, "processguardian-*.log");
    if (postDisposeFiles.Length == 0 || !postDisposeFiles.SelectMany(File.ReadLines).Any(line => line.Contains("after clear", StringComparison.Ordinal)))
        throw new InvalidOperationException("Logger disposal did not flush the final entry to disk.");
    if (File.Exists(oldLog)) throw new InvalidOperationException("Expired log cleanup did not remove an old file.");
    Console.WriteLine("PASS: missing log directory creation, expired retention cleanup, flush, and dispose persisted final log");

    var blocker = Path.Combine(root, "file-blocker");
    await File.WriteAllTextAsync(blocker, "not a directory");
    await using (var failingLogger = new FileLogService(Path.Combine(blocker, "Logs")))
    {
        failingLogger.Write(LogLevel.Error, "test", "safe write failure");
        await failingLogger.FlushAsync();
        if (failingLogger.LastWriteException is null)
            throw new InvalidOperationException("A log write failure was not exposed for diagnostics.");
    }
    Console.WriteLine("PASS: logging I/O failure is captured and does not throw into the caller");

    var exitLogDirectory = Path.Combine(root, "exit-logs");
    var exitLogger = new FileLogService(exitLogDirectory);
    var shutdownCalled = false;
    exitLogger.Write(LogLevel.Information, "Application", "application started");
    var coordinator = new ApplicationExitCoordinator(
        stopAcceptingOperations: () => { },
        waitForOperations: () => Task.CompletedTask,
        cleanup: async () =>
        {
            exitLogger.Write(LogLevel.Information, "Application", "application exit cleanup completed");
            await exitLogger.FlushAsync();
            await exitLogger.DisposeAsync();
        },
        shutdown: () => shutdownCalled = true);
    await Task.WhenAll(coordinator.ExitAsync(), coordinator.ExitAsync());
    var exitLog = File.ReadAllText(Directory.GetFiles(exitLogDirectory, "processguardian-*.log").Single());
    if (!shutdownCalled || !exitLog.Contains("application started", StringComparison.Ordinal)
        || !exitLog.Contains("application exit cleanup completed", StringComparison.Ordinal))
        throw new InvalidOperationException("Application exit did not persist startup and shutdown log records before shutdown.");
    Console.WriteLine("PASS: repeated application exit flushes startup and cleanup logs before shutdown");
    DeleteDirectoryWithRetry(root);
}

static void RunHideAsCloseTests(string executablePath)
{
    // Short injected timings keep the existing waits (8 s WaitForCondition) unchanged.
    var testTimings = new HideAsCloseTimings(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1.2));
    var hideLogDirectory = Path.Combine(Path.GetTempPath(), $"ProcessGuardian-hide-logs-{Guid.NewGuid():N}");
    var hideLogger = new FileLogService(hideLogDirectory);
    var previousLogger = AppLog.Replace(hideLogger);
    try
    {
        RunHideAsCloseTestsCore(executablePath, testTimings, hideLogger);
    }
    finally
    {
        AppLog.Replace(previousLogger);
        hideLogger.DisposeAsync().AsTask().GetAwaiter().GetResult();
        DeleteDirectoryWithRetry(hideLogDirectory);
    }
}

static void RunHideAsCloseTestsCore(string executablePath, HideAsCloseTimings testTimings, FileLogService hideLogger)
{
    void WaitUntilArmed() => Thread.Sleep(testTimings.ArmDelay + TimeSpan.FromMilliseconds(500));

    bool LogContains(string text, uint processId)
    {
        hideLogger.FlushAsync().GetAwaiter().GetResult();
        return hideLogger.ReadRecentAsync(10000).GetAwaiter().GetResult()
            .Any(entry => entry.ProcessId == checked((int)processId) && entry.Message.Contains(text, StringComparison.Ordinal));
    }

    ManagedApplication StartHideChild(ManagedApplicationService service, string name, bool option, out int[] tree, string? mode = null)
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"ProcessGuardian-hide-window-{Guid.NewGuid():N}.txt");
        var readyName = $"ProcessGuardianHideWindow_{Guid.NewGuid():N}";
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        service.HideAsCloseTimings = testTimings;
        var application = service.Start(
            name,
            executablePath,
            $"--hide-on-close-window-child {readyName} {QuoteArgument(pidFile)}" + (mode is null ? string.Empty : $" {mode}"),
            Path.GetTempPath(),
            treatMainWindowHideAsClose: option);
        if (!ready.WaitOne(5000))
        {
            if (File.Exists(pidFile))
            {
                File.Delete(pidFile);
            }

            throw new InvalidOperationException($"{name}: the hide-on-close test process did not create its window.");
        }

        var childPids = ReadChildPids(pidFile);
        tree = new[] { checked((int)application.ProcessId) }.Concat(childPids).ToArray();
        WaitForCondition(() => application.Status == ManagedApplicationStatus.Running
            && application.WindowMonitor?.Status == WindowMonitorStatus.Monitoring, $"{name} to enter Running");
        AssertJobMembership(application, childPids);
        return application;
    }

    void AssertStaysRunning(ManagedApplication application, IntPtr expectedWindow, int[] tree, string description)
    {
        // Poll for the full 2 s window so a late WindowClosed/Completed is detected.
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            var monitor = application.WindowMonitor;
            if (application.Status != ManagedApplicationStatus.Running
                || monitor?.Status != WindowMonitorStatus.Monitoring
                || monitor.MainWindowHandle != expectedWindow)
            {
                throw new InvalidOperationException(
                    $"{description}: expected Running/Monitoring on hwnd 0x{expectedWindow.ToInt64():X}, got app={application.Status}, monitor={monitor?.Status}, hwnd=0x{(monitor?.MainWindowHandle ?? IntPtr.Zero).ToInt64():X}.");
            }

            Thread.Sleep(50);
        }

        AssertProcessesAlive(tree, description);
    }

    // a. Option ON: WM_CLOSE only hides the main window -> WindowClosed -> Completed, Job closed, tree ended.
    using (var service = new ManagedApplicationService())
    {
        var application = StartHideChild(service, "Hide-as-close ON test", option: true, out var tree);
        var monitor = application.WindowMonitor!;
        var window = monitor.MainWindowHandle;
        WaitUntilArmed();
        // The service disposes the monitor during cleanup (status then reads Stopped), so capture the
        // WindowClosed notification itself instead of reading monitor.Status after Completed.
        long closedWindow = 0;
        monitor.StatusChanged += (_, eventArgs) =>
        {
            if (eventArgs.Status == WindowMonitorStatus.WindowClosed)
            {
                Interlocked.Exchange(ref closedWindow, eventArgs.WindowHandle.ToInt64());
            }
        };
        CloseManagedWindow(application);
        WaitForCondition(() => application.Status == ManagedApplicationStatus.Completed, "hide-as-close ON cleanup after the main window hid itself");
        if (Interlocked.Read(ref closedWindow) != window.ToInt64())
        {
            throw new InvalidOperationException($"Hide-as-close ON: expected WindowClosed for hwnd 0x{window.ToInt64():X}, got 0x{Interlocked.Read(ref closedWindow):X} (monitor={monitor.Status}, primaryExited={application.PrimaryProcessExited}).");
        }

        WaitForProcessesExit(tree, "hide-as-close ON process tree");
        if (TestWindowNativeMethods.IsWindow(window))
        {
            throw new InvalidOperationException("Hide-as-close ON: the hidden test window still exists after Job cleanup.");
        }

        try
        {
            _ = application.LaunchResult!.JobObject.ActiveProcessCount;
            throw new InvalidOperationException("Hide-as-close ON: the Job Object was not released after cleanup.");
        }
        catch (ObjectDisposedException)
        {
        }

        if (!LogContains("Hide-as-close pending", application.ProcessId) || !LogContains("Hide-as-close triggered", application.ProcessId))
        {
            throw new InvalidOperationException("Hide-as-close ON: the replacement grace and trigger were not logged.");
        }

        Console.WriteLine("PASS: hide-as-close ON: an armed hide with no replacement reports WindowClosed after the replacement grace, completes, closes the Job and ends the tree");
    }

    // b. Option OFF: the same hide keeps Running/Monitoring and the tree alive; Stop still cleans.
    using (var service = new ManagedApplicationService())
    {
        var application = StartHideChild(service, "Hide-as-close OFF test", option: false, out var tree);
        var window = application.WindowMonitor!.MainWindowHandle;
        CloseManagedWindow(application);
        if (!SpinWait.SpinUntil(() => !TestWindowNativeMethods.IsWindowVisible(window), TimeSpan.FromSeconds(8)))
        {
            throw new InvalidOperationException("Hide-as-close OFF: the test window did not hide itself.");
        }

        AssertStaysRunning(application, window, tree, "hide-as-close OFF after the main window hid itself");
        service.Stop(application);
        WaitForCondition(() => application.Status == ManagedApplicationStatus.Completed, "hide-as-close OFF Stop cleanup");
        WaitForProcessesExit(tree, "hide-as-close OFF process tree after Stop");
        Console.WriteLine("PASS: hide-as-close OFF: hiding the main window keeps the app Running for 2 s; Stop then cleans the tree");
    }

    // c. Option ON: minimizing (and minimize-then-hide, iconic) is not treated as a close.
    using (var service = new ManagedApplicationService())
    {
        var application = StartHideChild(service, "Hide-as-close minimize test", option: true, out var tree);
        var window = application.WindowMonitor!.MainWindowHandle;
        TestWindowNativeMethods.SetTestWindowShowState(window, TestWindowNativeMethods.SwMinimize);
        if (!SpinWait.SpinUntil(() => TestWindowNativeMethods.IsIconic(window), TimeSpan.FromSeconds(8)))
        {
            throw new InvalidOperationException("Hide-as-close minimize: the test window did not minimize.");
        }

        AssertStaysRunning(application, window, tree, "hide-as-close ON after minimize");
        TestWindowNativeMethods.SetTestWindowShowState(window, TestWindowNativeMethods.SwHideCommand);
        if (!SpinWait.SpinUntil(() => !TestWindowNativeMethods.IsWindowVisible(window), TimeSpan.FromSeconds(8))
            || !TestWindowNativeMethods.IsIconic(window))
        {
            throw new InvalidOperationException("Hide-as-close minimize: the minimized test window did not become hidden while iconic.");
        }

        AssertStaysRunning(application, window, tree, "hide-as-close ON after minimize then hide");
        service.Stop(application);
        WaitForCondition(() => application.Status == ManagedApplicationStatus.Completed, "hide-as-close minimize Stop cleanup");
        WaitForProcessesExit(tree, "hide-as-close minimize process tree after Stop");
        Console.WriteLine("PASS: hide-as-close ON: minimize (and a hidden iconic window) is not treated as a close; Stop then cleans the tree");
    }

    // d. Option ON: hide then re-show within the 400 ms confirmation is not a close; a later real hide still is.
    using (var service = new ManagedApplicationService())
    {
        var application = StartHideChild(service, "Hide-as-close re-show test", option: true, out var tree);
        var monitor = application.WindowMonitor!;
        var window = monitor.MainWindowHandle;
        WaitUntilArmed();
        TestWindowNativeMethods.SetTestWindowShowState(window, TestWindowNativeMethods.SwHideCommand);
        Thread.Sleep(100);
        TestWindowNativeMethods.SetTestWindowShowState(window, TestWindowNativeMethods.SwShowCommand);
        if (!TestWindowNativeMethods.IsWindowVisible(window))
        {
            throw new InvalidOperationException("Hide-as-close re-show: the test window is not visible after re-show.");
        }

        AssertStaysRunning(application, window, tree, "hide-as-close ON after hide then re-show within 400 ms");
        // Re-showing restarts arming; wait until the re-shown window is armed again.
        WaitUntilArmed();
        // The service disposes the monitor during cleanup (status then reads Stopped), so capture the
        // WindowClosed notification itself instead of reading monitor.Status after Completed.
        long closedWindow = 0;
        monitor.StatusChanged += (_, eventArgs) =>
        {
            if (eventArgs.Status == WindowMonitorStatus.WindowClosed)
            {
                Interlocked.Exchange(ref closedWindow, eventArgs.WindowHandle.ToInt64());
            }
        };
        CloseManagedWindow(application);
        WaitForCondition(() => application.Status == ManagedApplicationStatus.Completed, "hide-as-close cleanup after a later real hide");
        WaitForProcessesExit(tree, "hide-as-close re-show process tree after a later real hide");
        if (Interlocked.Read(ref closedWindow) != window.ToInt64())
        {
            throw new InvalidOperationException($"Hide-as-close re-show: expected WindowClosed for hwnd 0x{window.ToInt64():X} after the later hide, got 0x{Interlocked.Read(ref closedWindow):X} (monitor={monitor.Status}, primaryExited={application.PrimaryProcessExited}).");
        }

        Console.WriteLine("PASS: hide-as-close ON: hide then re-show within 400 ms is not a close; a later sustained hide still completes cleanup");
    }

    // e. Option ON: a startup window that hides before the arm delay is not a close; the next main
    //    window of the same process is selected, and a later armed hide of it completes cleanup.
    using (var service = new ManagedApplicationService())
    {
        var application = StartHideChild(service, "Hide-as-close not-armed test", option: true, out var tree, "replace:2500");
        var monitor = application.WindowMonitor!;
        var welcome = monitor.MainWindowHandle;
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<(WindowMonitorStatus Status, long Window)>();
        monitor.StatusChanged += (_, eventArgs) => statuses.Enqueue((eventArgs.Status, eventArgs.WindowHandle.ToInt64()));
        CloseManagedWindow(application);
        if (!SpinWait.SpinUntil(() => !TestWindowNativeMethods.IsWindowVisible(welcome), TimeSpan.FromSeconds(8)))
        {
            throw new InvalidOperationException("Hide-as-close not-armed: the startup window did not hide itself.");
        }

        // Until the replacement appears (2.5 s, longer than the replacement grace) the app stays Running.
        WaitForCondition(() =>
        {
            var current = monitor.MainWindowHandle;
            if (application.Status != ManagedApplicationStatus.Running || monitor.Status != WindowMonitorStatus.Monitoring)
            {
                throw new InvalidOperationException($"Hide-as-close not-armed: expected Running/Monitoring while waiting for the next main window, got app={application.Status}, monitor={monitor.Status}.");
            }

            return current != welcome && current != IntPtr.Zero;
        }, "hide-as-close not-armed selection of the next main window");
        var mainWindow = monitor.MainWindowHandle;
        TestWindowNativeMethods.GetWindowThreadProcessId(mainWindow, out var mainWindowProcessId);
        if (mainWindowProcessId != application.ProcessId || monitor.MainWindowTitle != TestWindowNativeMethods.ReplacementWindowTitle)
        {
            throw new InvalidOperationException($"Hide-as-close not-armed: unexpected next window (pid={mainWindowProcessId}, title={monitor.MainWindowTitle}).");
        }

        if (statuses.Any(entry => entry.Status != WindowMonitorStatus.Monitoring))
        {
            throw new InvalidOperationException($"Hide-as-close not-armed: unexpected monitor status changes: {string.Join(", ", statuses.Select(entry => entry.Status))}.");
        }

        if (!LogContains("Hide-as-close not armed", application.ProcessId))
        {
            throw new InvalidOperationException("Hide-as-close not-armed: the 'not armed' decision was not logged.");
        }

        AssertProcessesAlive(tree, "hide-as-close not-armed process tree after the startup window hid");
        WaitUntilArmed();
        CloseManagedWindow(application);
        WaitForCondition(() => application.Status == ManagedApplicationStatus.Completed, "hide-as-close cleanup after the next main window hid");
        WaitForProcessesExit(tree, "hide-as-close not-armed process tree");
        if (!statuses.Any(entry => entry.Status == WindowMonitorStatus.WindowClosed && entry.Window == mainWindow.ToInt64())
            || statuses.Any(entry => entry.Status == WindowMonitorStatus.WindowClosed && entry.Window == welcome.ToInt64()))
        {
            throw new InvalidOperationException("Hide-as-close not-armed: expected WindowClosed only for the next main window.");
        }

        Console.WriteLine("PASS: hide-as-close ON: a startup window hidden before arming is not a close ('not armed' logged); the next main window in the same process is selected and its later hide completes cleanup");
    }

    // f. Option ON: an armed hide followed by a replacement window within the grace is a switch, not a close.
    using (var service = new ManagedApplicationService())
    {
        var application = StartHideChild(service, "Hide-as-close replacement test", option: true, out var tree, "replace:700");
        var monitor = application.WindowMonitor!;
        var first = monitor.MainWindowHandle;
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<(WindowMonitorStatus Status, long Window)>();
        monitor.StatusChanged += (_, eventArgs) => statuses.Enqueue((eventArgs.Status, eventArgs.WindowHandle.ToInt64()));
        WaitUntilArmed();
        CloseManagedWindow(application);
        WaitForCondition(() => monitor.MainWindowHandle != first && monitor.MainWindowHandle != IntPtr.Zero,
            "hide-as-close switch to the replacement window within the grace");
        var replacement = monitor.MainWindowHandle;
        if (monitor.MainWindowTitle != TestWindowNativeMethods.ReplacementWindowTitle)
        {
            throw new InvalidOperationException($"Hide-as-close replacement: unexpected window title {monitor.MainWindowTitle}.");
        }

        // Past the end of the grace: still Running on the replacement, no WindowClosed.
        AssertStaysRunning(application, replacement, tree, "hide-as-close ON after an armed hide with a replacement within the grace");
        if (statuses.Any(entry => entry.Status != WindowMonitorStatus.Monitoring))
        {
            throw new InvalidOperationException($"Hide-as-close replacement: unexpected monitor status changes: {string.Join(", ", statuses.Select(entry => entry.Status))}.");
        }

        if (!LogContains("Hide-as-close pending", application.ProcessId))
        {
            throw new InvalidOperationException("Hide-as-close replacement: the replacement grace was not logged.");
        }

        WaitUntilArmed();
        CloseManagedWindow(application);
        WaitForCondition(() => application.Status == ManagedApplicationStatus.Completed, "hide-as-close cleanup after the replacement window hid");
        WaitForProcessesExit(tree, "hide-as-close replacement process tree");
        if (!statuses.Any(entry => entry.Status == WindowMonitorStatus.WindowClosed && entry.Window == replacement.ToInt64())
            || statuses.Any(entry => entry.Status == WindowMonitorStatus.WindowClosed && entry.Window == first.ToInt64()))
        {
            throw new InvalidOperationException("Hide-as-close replacement: expected WindowClosed only for the replacement window.");
        }

        Console.WriteLine("PASS: hide-as-close ON: an armed hide followed by a replacement window within the grace switches instead of closing; hiding the replacement then completes cleanup");
    }

    // g. Option ON: another Job process still shows a window at confirmation -> blocked, no close; Stop cleans.
    using (var service = new ManagedApplicationService())
    {
        var application = StartHideChild(service, "Hide-as-close sibling block test", option: true, out var tree, "sibling");
        var monitor = application.WindowMonitor!;
        var window = monitor.MainWindowHandle;
        long closedWindow = 0;
        monitor.StatusChanged += (_, eventArgs) =>
        {
            if (eventArgs.Status == WindowMonitorStatus.WindowClosed)
            {
                Interlocked.Exchange(ref closedWindow, eventArgs.WindowHandle.ToInt64());
            }
        };
        WaitUntilArmed();
        CloseManagedWindow(application);
        if (!SpinWait.SpinUntil(() => !TestWindowNativeMethods.IsWindowVisible(window), TimeSpan.FromSeconds(8)))
        {
            throw new InvalidOperationException("Hide-as-close sibling block: the main test window did not hide itself.");
        }

        AssertStaysRunning(application, window, tree, "hide-as-close ON while another Job process shows a window");
        if (Interlocked.Read(ref closedWindow) != 0 || !LogContains("Hide-as-close blocked", application.ProcessId))
        {
            throw new InvalidOperationException("Hide-as-close sibling block: expected a logged block and no WindowClosed.");
        }

        service.Stop(application);
        WaitForCondition(() => application.Status == ManagedApplicationStatus.Completed, "hide-as-close sibling block Stop cleanup");
        WaitForProcessesExit(tree, "hide-as-close sibling block process tree after Stop");
        Console.WriteLine("PASS: hide-as-close ON: a visible window in another Job process blocks the close (selection not switched); Stop then cleans the tree");
    }

    // Task 16 helpers: the Job child (mode "jobchild:<event>") is the last PID of the tree.
    void WaitForJobChildWindowVisible(int jobChildProcessId, string description)
    {
        WaitForCondition(() =>
        {
            var selection = WindowSelector.Select(checked((uint)jobChildProcessId));
            return selection.Status == WindowMonitorStatus.Monitoring && selection.Title == TestWindowNativeMethods.JobChildWindowTitle;
        }, description);
    }

    // h. Task 16, option ON: the launched window hides before arming (welcome window); a qualifying window
    //    then shown by another process of the same Job is selected; its later armed hide completes cleanup.
    using (var service = new ManagedApplicationService())
    {
        var showName = $"ProcessGuardianJobChildShow_{Guid.NewGuid():N}";
        using var show = new EventWaitHandle(false, EventResetMode.ManualReset, showName);
        var application = StartHideChild(service, "Hide-as-close Job child selection test", option: true, out var tree, $"jobchild:{showName}");
        var monitor = application.WindowMonitor!;
        var welcome = monitor.MainWindowHandle;
        var jobChildProcessId = tree[^1];
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<(WindowMonitorStatus Status, long Window)>();
        monitor.StatusChanged += (_, eventArgs) => statuses.Enqueue((eventArgs.Status, eventArgs.WindowHandle.ToInt64()));
        CloseManagedWindow(application);
        if (!SpinWait.SpinUntil(() => !TestWindowNativeMethods.IsWindowVisible(welcome), TimeSpan.FromSeconds(8)))
        {
            throw new InvalidOperationException("Hide-as-close Job child selection: the launched window did not hide itself.");
        }

        show.Set();
        WaitForCondition(() =>
        {
            if (application.Status != ManagedApplicationStatus.Running || monitor.Status != WindowMonitorStatus.Monitoring)
            {
                throw new InvalidOperationException($"Hide-as-close Job child selection: expected Running/Monitoring while waiting, got app={application.Status}, monitor={monitor.Status}.");
            }

            var current = monitor.MainWindowHandle;
            return current != welcome && current != IntPtr.Zero;
        }, "hide-as-close selection of the Job child window");
        var childWindow = monitor.MainWindowHandle;
        TestWindowNativeMethods.GetWindowThreadProcessId(childWindow, out var childWindowProcessId);
        if (childWindowProcessId != jobChildProcessId || monitor.MainWindowTitle != TestWindowNativeMethods.JobChildWindowTitle)
        {
            throw new InvalidOperationException($"Hide-as-close Job child selection: unexpected window (pid={childWindowProcessId}, expected {jobChildProcessId}, title={monitor.MainWindowTitle}).");
        }

        if (statuses.Any(entry => entry.Status != WindowMonitorStatus.Monitoring))
        {
            throw new InvalidOperationException($"Hide-as-close Job child selection: unexpected monitor status changes: {string.Join(", ", statuses.Select(entry => entry.Status))}.");
        }

        if (!LogContains("Hide-as-close not armed", application.ProcessId)
            || !LogContains($"selected main window belongs to non-launched Job process {jobChildProcessId}", application.ProcessId))
        {
            throw new InvalidOperationException("Hide-as-close Job child selection: the 'not armed' hide and the non-launched Job process selection were not logged.");
        }

        AssertProcessesAlive(tree, "hide-as-close Job child selection process tree after the switch");
        WaitUntilArmed();
        CloseManagedWindow(application);
        WaitForCondition(() => application.Status == ManagedApplicationStatus.Completed, "hide-as-close cleanup after the Job child window hid");
        WaitForProcessesExit(tree, "hide-as-close Job child selection process tree");
        if (!statuses.Any(entry => entry.Status == WindowMonitorStatus.WindowClosed && entry.Window == childWindow.ToInt64())
            || statuses.Any(entry => entry.Status == WindowMonitorStatus.WindowClosed && entry.Window == welcome.ToInt64()))
        {
            throw new InvalidOperationException("Hide-as-close Job child selection: expected WindowClosed only for the Job child window.");
        }

        if (!LogContains("Hide-as-close triggered", application.ProcessId))
        {
            throw new InvalidOperationException("Hide-as-close Job child selection: the trigger was not logged.");
        }

        Console.WriteLine("PASS: hide-as-close ON (Task 16): after an unarmed hide of the launched window, a qualifying window of another Job process is selected (logged); its armed hide completes cleanup");
    }

    // i. Task 16, option OFF: the same Job child window is never selected; the launched window stays selected.
    using (var service = new ManagedApplicationService())
    {
        var showName = $"ProcessGuardianJobChildShow_{Guid.NewGuid():N}";
        using var show = new EventWaitHandle(false, EventResetMode.ManualReset, showName);
        var application = StartHideChild(service, "Hide-as-close OFF Job child test", option: false, out var tree, $"jobchild:{showName}");
        var window = application.WindowMonitor!.MainWindowHandle;
        CloseManagedWindow(application);
        if (!SpinWait.SpinUntil(() => !TestWindowNativeMethods.IsWindowVisible(window), TimeSpan.FromSeconds(8)))
        {
            throw new InvalidOperationException("Hide-as-close OFF Job child: the launched window did not hide itself.");
        }

        show.Set();
        WaitForJobChildWindowVisible(tree[^1], "hide-as-close OFF Job child window to become visible");
        AssertStaysRunning(application, window, tree, "hide-as-close OFF while another Job process shows a qualifying window");
        service.Stop(application);
        WaitForCondition(() => application.Status == ManagedApplicationStatus.Completed, "hide-as-close OFF Job child Stop cleanup");
        WaitForProcessesExit(tree, "hide-as-close OFF Job child process tree after Stop");
        Console.WriteLine("PASS: hide-as-close OFF (Task 16): a qualifying window of another Job process is not selected; Stop then cleans the tree");
    }

    // j. Task 16, option ON: a qualifying window of a process outside the Job is never selected.
    using (var service = new ManagedApplicationService())
    {
        var application = StartHideChild(service, "Hide-as-close outsider test", option: true, out var tree);
        var welcome = application.WindowMonitor!.MainWindowHandle;
        CloseManagedWindow(application);
        if (!SpinWait.SpinUntil(() => !TestWindowNativeMethods.IsWindowVisible(welcome), TimeSpan.FromSeconds(8)))
        {
            throw new InvalidOperationException("Hide-as-close outsider: the launched window did not hide itself.");
        }

        var outsiderReadyName = $"ProcessGuardianOutsiderWindow_{Guid.NewGuid():N}";
        using var outsiderReady = new EventWaitHandle(false, EventResetMode.ManualReset, outsiderReadyName);
        var outsiderStartInfo = new ProcessStartInfo(executablePath) { UseShellExecute = false, CreateNoWindow = true };
        outsiderStartInfo.ArgumentList.Add("--window-child");
        outsiderStartInfo.ArgumentList.Add(outsiderReadyName);
        using var outsider = Process.Start(outsiderStartInfo)
            ?? throw new InvalidOperationException("Could not start the outsider window test process.");
        try
        {
            if (!outsiderReady.WaitOne(5000))
            {
                throw new InvalidOperationException("Hide-as-close outsider: the outsider process did not create its window.");
            }

            if (application.LaunchResult!.JobObject.GetProcessIds().Contains(checked((uint)outsider.Id)))
            {
                throw new InvalidOperationException("Hide-as-close outsider: the outsider process is unexpectedly in the managed Job.");
            }

            // The unarmed launched window stays selected (waiting); the outsider window is never selected.
            AssertStaysRunning(application, welcome, tree, "hide-as-close ON while a process outside the Job shows a qualifying window");
            if (LogContains("selected main window belongs to non-launched Job process", application.ProcessId))
            {
                throw new InvalidOperationException("Hide-as-close outsider: a non-launched process window was selected.");
            }
        }
        finally
        {
            // Only the outsider started by this test is terminated here.
            if (!outsider.HasExited)
            {
                outsider.Kill();
                outsider.WaitForExit(5000);
            }
        }

        service.Stop(application);
        WaitForCondition(() => application.Status == ManagedApplicationStatus.Completed, "hide-as-close outsider Stop cleanup");
        WaitForProcessesExit(tree, "hide-as-close outsider process tree after Stop");
        Console.WriteLine("PASS: hide-as-close ON (Task 16): a qualifying window of a process outside the Job is never selected; Stop then cleans the tree");
    }

    // k. Task 16, option ON: an armed hide of the launched window followed, within the grace, by a new
    //    window of another Job process is a switch, not a close; hiding that window then completes cleanup.
    using (var service = new ManagedApplicationService())
    {
        var showName = $"ProcessGuardianJobChildShow_{Guid.NewGuid():N}";
        using var show = new EventWaitHandle(false, EventResetMode.ManualReset, showName);
        var application = StartHideChild(service, "Hide-as-close Job child replacement test", option: true, out var tree, $"jobchild:{showName}");
        var monitor = application.WindowMonitor!;
        var first = monitor.MainWindowHandle;
        var jobChildProcessId = tree[^1];
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<(WindowMonitorStatus Status, long Window)>();
        monitor.StatusChanged += (_, eventArgs) => statuses.Enqueue((eventArgs.Status, eventArgs.WindowHandle.ToInt64()));
        WaitUntilArmed();
        CloseManagedWindow(application);
        if (!SpinWait.SpinUntil(() => !TestWindowNativeMethods.IsWindowVisible(first), TimeSpan.FromSeconds(8)))
        {
            throw new InvalidOperationException("Hide-as-close Job child replacement: the launched window did not hide itself.");
        }

        // After the 400 ms confirmation (grace pending) but well inside the 1.2 s grace.
        Thread.Sleep(700);
        show.Set();
        WaitForCondition(() => monitor.MainWindowHandle != first && monitor.MainWindowHandle != IntPtr.Zero,
            "hide-as-close switch to the Job child window within the grace");
        var replacement = monitor.MainWindowHandle;
        TestWindowNativeMethods.GetWindowThreadProcessId(replacement, out var replacementProcessId);
        if (replacementProcessId != jobChildProcessId || monitor.MainWindowTitle != TestWindowNativeMethods.JobChildWindowTitle)
        {
            throw new InvalidOperationException($"Hide-as-close Job child replacement: unexpected window (pid={replacementProcessId}, expected {jobChildProcessId}, title={monitor.MainWindowTitle}).");
        }

        AssertStaysRunning(application, replacement, tree, "hide-as-close ON after an armed hide with a Job child replacement within the grace");
        if (statuses.Any(entry => entry.Status != WindowMonitorStatus.Monitoring))
        {
            throw new InvalidOperationException($"Hide-as-close Job child replacement: unexpected monitor status changes: {string.Join(", ", statuses.Select(entry => entry.Status))}.");
        }

        if (!LogContains("Hide-as-close pending", application.ProcessId)
            || !LogContains($"selected main window belongs to non-launched Job process {jobChildProcessId}", application.ProcessId))
        {
            throw new InvalidOperationException("Hide-as-close Job child replacement: the grace and the non-launched Job process selection were not logged.");
        }

        WaitUntilArmed();
        CloseManagedWindow(application);
        WaitForCondition(() => application.Status == ManagedApplicationStatus.Completed, "hide-as-close cleanup after the Job child replacement hid");
        WaitForProcessesExit(tree, "hide-as-close Job child replacement process tree");
        if (!statuses.Any(entry => entry.Status == WindowMonitorStatus.WindowClosed && entry.Window == replacement.ToInt64())
            || statuses.Any(entry => entry.Status == WindowMonitorStatus.WindowClosed && entry.Window == first.ToInt64()))
        {
            throw new InvalidOperationException("Hide-as-close Job child replacement: expected WindowClosed only for the Job child window.");
        }

        Console.WriteLine("PASS: hide-as-close ON (Task 16): an armed hide followed by a new window of another Job process within the grace switches instead of closing; hiding it then completes cleanup");
    }
}

static async Task RunHideAsCloseConfigurationTestAsync()
{
    var testDirectory = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", $"ProcessGuardianHideOptionConfig_{Guid.NewGuid():N}");
    Directory.CreateDirectory(testDirectory);
    try
    {
        var roundTripPath = Path.Combine(testDirectory, "config.json");
        var enabledId = Guid.NewGuid();
        var disabledId = Guid.NewGuid();
        await new ConfigurationService(roundTripPath).SaveAsync([
            new ApplicationConfiguration { Id = enabledId, SoftwareName = "Hide option on", ExecutablePath = @"C:\Missing\on.exe", TreatMainWindowHideAsClose = true },
            new ApplicationConfiguration { Id = disabledId, SoftwareName = "Hide option off", ExecutablePath = @"C:\Missing\off.exe" },
        ]);
        var savedJson = await File.ReadAllTextAsync(roundTripPath);
        if (!savedJson.Contains("\"TreatMainWindowHideAsClose\": true", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The hide-as-close option was not written to config.json.");
        }

        var loaded = await new ConfigurationService(roundTripPath).LoadAsync();
        var loadedEntries = loaded.Applications.ToDictionary(entry => entry.Id);
        if (!loadedEntries[enabledId].TreatMainWindowHideAsClose || loadedEntries[disabledId].TreatMainWindowHideAsClose)
        {
            throw new InvalidOperationException("The hide-as-close option did not survive a configuration round trip.");
        }

        var legacyPath = Path.Combine(testDirectory, "legacy-config.json");
        var legacyId = Guid.NewGuid();
        await File.WriteAllTextAsync(legacyPath,
            "[{\"Id\":\"" + legacyId + "\",\"SoftwareName\":\"Legacy entry\",\"ExecutablePath\":\"C:\\\\Missing\\\\legacy.exe\",\"AddedAt\":\"2026-01-01T00:00:00+00:00\",\"Arguments\":null,\"WorkingDirectory\":null}]",
            new System.Text.UTF8Encoding(false));
        var legacy = await new ConfigurationService(legacyPath).LoadAsync();
        var legacyEntry = legacy.Applications.Single();
        if (legacyEntry.Id != legacyId || legacyEntry.SoftwareName != "Legacy entry" || legacyEntry.TreatMainWindowHideAsClose)
        {
            throw new InvalidOperationException("A config.json without TreatMainWindowHideAsClose did not load as false.");
        }

        Console.WriteLine("PASS: TreatMainWindowHideAsClose round-trips through config.json and a legacy config without it loads as false");
    }
    finally
    {
        Directory.Delete(testDirectory, recursive: true);
    }
}

static int[] ReadChildPids(string path)
{
    var processIds = File.ReadAllLines(path).Select(int.Parse).ToArray();
    File.Delete(path);
    return processIds;
}

static void WaitForCondition(Func<bool> condition, string description)
{
    if (!SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(8)))
    {
        throw new TimeoutException($"Timed out waiting for {description}.");
    }
}

static bool JobContainsAny(ManagedApplication application, IEnumerable<int> processIds)
{
    foreach (var processId in processIds)
    {
        using var process = Process.GetProcessById(processId);
        if (application.LaunchResult!.JobObject.ContainsProcess(process))
        {
            return true;
        }
    }

    return false;
}

static void AssertJobMembership(ManagedApplication application, IEnumerable<int> childProcessIds)
{
    var children = childProcessIds.ToArray();
    var processIds = new[] { checked((int)application.ProcessId) }.Concat(children).ToArray();
    foreach (var processId in processIds)
    {
        using var process = Process.GetProcessById(processId);
        if (process.HasExited || !application.LaunchResult!.JobObject.ContainsProcess(process))
        {
            throw new InvalidOperationException($"Process {processId} is not alive in {application.SoftwareName}'s Job Object.");
        }
    }

    var activeCount = application.LaunchResult!.JobObject.ActiveProcessCount;
    if (activeCount < processIds.Length)
    {
        throw new InvalidOperationException($"Expected at least {processIds.Length} active processes in {application.SoftwareName}'s Job Object, got {activeCount}.");
    }
}

static void AssertProcessesAlive(IEnumerable<int> processIds, string description)
{
    foreach (var processId in processIds)
    {
        if (IsProcessExited(processId))
        {
            throw new InvalidOperationException($"{description}: process {processId} exited unexpectedly.");
        }
    }
}

static void WaitForProcessesExit(IEnumerable<int> processIds, string description)
{
    var ids = processIds.ToArray();
    if (!SpinWait.SpinUntil(() => ids.All(IsProcessExited), TimeSpan.FromSeconds(8)))
    {
        throw new TimeoutException($"Timed out waiting for {description} to exit.");
    }
}

static bool IsProcessExited(int processId)
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

static void CloseManagedWindow(ManagedApplication application)
{
    var window = application.WindowMonitor?.MainWindowHandle ?? IntPtr.Zero;
    if (window == IntPtr.Zero || !TestWindowNativeMethods.PostMessage(window, 0x0010, UIntPtr.Zero, IntPtr.Zero))
    {
        throw new InvalidOperationException($"Could not close {application.SoftwareName}'s test window.");
    }
}

static void RunViewModelWorkflowTest(string targetExecutable, string testChildExecutable)
{
    if (!File.Exists(targetExecutable))
    {
        throw new FileNotFoundException("The built WPF application is needed for the ViewModel workflow test.", targetExecutable);
    }

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
                await RunViewModelWorkflowAsync(targetExecutable, testChildExecutable);
                await RunMainWindowTrayLifecycleTestAsync(testChildExecutable);
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
        Name = "Process Guardian ViewModel workflow test",
    };

    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!finished.Task.Wait(TimeSpan.FromSeconds(45)))
    {
        throw new TimeoutException("The WPF ViewModel workflow did not finish.");
    }

    thread.Join();
    if (failure is not null)
    {
        throw new InvalidOperationException("The WPF command workflow failed.", failure);
    }
}

static async Task RunViewModelWorkflowAsync(string targetExecutable, string testChildExecutable)
{
    var expectedDefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProcessGuardian",
        "config.json");
    var overrideDirectory = Environment.GetEnvironmentVariable(AppDataPaths.OverrideVariable);
    string defaultPath;
    string relativeOverridePath;
    try
    {
        Environment.SetEnvironmentVariable(AppDataPaths.OverrideVariable, null);
        defaultPath = new ConfigurationService().ConfigurationPath;
        Environment.SetEnvironmentVariable(AppDataPaths.OverrideVariable, "relative-data");
        relativeOverridePath = new ConfigurationService().ConfigurationPath;
    }
    finally
    {
        Environment.SetEnvironmentVariable(AppDataPaths.OverrideVariable, overrideDirectory);
    }

    if (!string.Equals(defaultPath, expectedDefaultPath, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Default configuration path does not use LocalAppData\\ProcessGuardian\\config.json.");
    }

    Console.WriteLine("PASS: default configuration path is under LocalAppData\\ProcessGuardian");

    if (!string.Equals(relativeOverridePath, expectedDefaultPath, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("A relative PROCESSGUARDIAN_DATA_DIR value redirected the configuration path.");
    }

    if (string.IsNullOrEmpty(overrideDirectory)
        || !string.Equals(new ConfigurationService().ConfigurationPath, Path.Combine(overrideDirectory, "config.json"), StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("PROCESSGUARDIAN_DATA_DIR did not redirect the default configuration path.");
    }

    await using (var isolatedLogger = new FileLogService())
    {
        if (!string.Equals(isolatedLogger.LogDirectory, Path.Combine(overrideDirectory, "Logs"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("PROCESSGUARDIAN_DATA_DIR did not redirect the default log directory.");
        }
    }

    Console.WriteLine("PASS: PROCESSGUARDIAN_DATA_DIR redirects default config.json and Logs; unset or relative values keep LocalAppData");

    var testDirectory = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", $"ProcessGuardianConfigTests_{Guid.NewGuid():N}");
    Directory.CreateDirectory(testDirectory);
    var configurationPath = Path.Combine(testDirectory, "config.json");
    var configurationService = new ConfigurationService(configurationPath);
    var service = new ManagedApplicationService(synchronizationContext: SynchronizationContext.Current);
    Process? unrelatedProcess = null;
    // The workflow launches the added EXEs. They are the test program itself (a plain Win32 window via
    // --window-child, set just before the launch step) rather than ProcessGuardian.exe: a launched
    // ProcessGuardian.exe would now treat "--sample value" as a usage error, and two launched instances would share
    // this run's PROCESSGUARDIAN_DATA_DIR (entries have no per-entry environment), so the second one would forward
    // to the first and exit instead of running.
    var viewModel = new MainViewModel(
        service,
        new FixedExecutableFilePicker(testChildExecutable, testChildExecutable, testChildExecutable),
        configurationService,
        confirmRestoreDefaults: () => true);
    Exception? cleanupFailure = null;

    try
    {
        await viewModel.InitializationTask;
        viewModel.AddApplicationCommand.Execute(null);
        viewModel.AddApplicationCommand.Execute(null);
        if (viewModel.ApplicationItems.Count != 2
            || viewModel.ApplicationItems.Any(item => item.ManagedApplication is not null))
        {
            throw new InvalidOperationException("Add application did not add two stopped configurations.");
        }

        Console.WriteLine("PASS: Add command adds EXE configurations without launching them");

        var first = viewModel.ApplicationItems[0];
        var second = viewModel.ApplicationItems[1];
        first.SoftwareName = "Persisted test name";
        first.Arguments = "--sample value";
        first.WorkingDirectory = testDirectory;
        await viewModel.PendingSaveTask;

        if (!File.Exists(configurationPath))
        {
            throw new InvalidOperationException($"Configuration file was not saved. ViewModel error: {viewModel.ConfigurationError ?? "none"}");
        }

        var savedJson = await File.ReadAllTextAsync(configurationPath);
        var savedBytes = await File.ReadAllBytesAsync(configurationPath);
        if (savedBytes.Length == 0 || (savedBytes[0] != (byte)'[' && savedBytes[0] != 0xEF))
        {
            throw new InvalidOperationException("Saved configuration is not encoded as UTF-8 JSON.");
        }

        var savedEntries = JsonSerializer.Deserialize<List<ApplicationConfiguration>>(savedJson)
            ?? throw new InvalidOperationException("Saved JSON did not contain application entries.");
        if (savedEntries.Count != 2 || savedEntries[0].SoftwareName != "Persisted test name"
            || savedEntries[0].Arguments != "--sample value" || savedEntries[0].WorkingDirectory != testDirectory
            || savedEntries.Any(entry => entry.Id == Guid.Empty || entry.AddedAt == default))
        {
            throw new InvalidOperationException($"Saved configuration did not contain the expected editable fields. ViewModel error: {viewModel.ConfigurationError ?? "none"}");
        }

        Console.WriteLine("PASS: application add and configuration edits are saved as UTF-8 JSON");

        if (first.TreatMainWindowHideAsClose || second.TreatMainWindowHideAsClose)
        {
            throw new InvalidOperationException("TreatMainWindowHideAsClose must default to false for newly added applications.");
        }

        first.TreatMainWindowHideAsClose = true;
        await viewModel.PendingSaveTask;
        var hideOptionEntries = JsonSerializer.Deserialize<List<ApplicationConfiguration>>(await File.ReadAllTextAsync(configurationPath))
            ?? throw new InvalidOperationException("Saved JSON did not contain application entries after the hide option changed.");
        if (hideOptionEntries.Count != 2 || !hideOptionEntries[0].TreatMainWindowHideAsClose || hideOptionEntries[1].TreatMainWindowHideAsClose)
        {
            throw new InvalidOperationException($"The hide-as-close option was not persisted on change. ViewModel error: {viewModel.ConfigurationError ?? "none"}");
        }

        first.TreatMainWindowHideAsClose = false;
        await viewModel.PendingSaveTask;
        hideOptionEntries = JsonSerializer.Deserialize<List<ApplicationConfiguration>>(await File.ReadAllTextAsync(configurationPath))
            ?? throw new InvalidOperationException("Saved JSON did not contain application entries after the hide option was cleared.");
        if (hideOptionEntries.Any(entry => entry.TreatMainWindowHideAsClose))
        {
            throw new InvalidOperationException("Clearing the hide-as-close option was not persisted.");
        }

        Console.WriteLine("PASS: ViewModel exposes the hide-as-close option (default off) and saves it on change");

        var invalidWorkingDirectory = Path.Combine(testDirectory, "folder-that-does-not-exist");
        first.WorkingDirectory = invalidWorkingDirectory;
        await viewModel.PendingSaveTask;
        if (first.CanLaunch || !first.HasInvalidWorkingDirectory || !first.ErrorMessage!.Contains(invalidWorkingDirectory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An invalid working directory was not reported before process creation.");
        }
        first.WorkingDirectory = testDirectory;
        await viewModel.PendingSaveTask;

        var restoredViewModel = new MainViewModel(
            new ManagedApplicationService(synchronizationContext: SynchronizationContext.Current),
            new FixedExecutableFilePicker(),
            new ConfigurationService(configurationPath));
        await restoredViewModel.InitializationTask;
        if (restoredViewModel.ApplicationItems.Count != 2
            || restoredViewModel.ApplicationItems[0].SoftwareName != "Persisted test name"
            || restoredViewModel.ApplicationItems[0].Arguments != "--sample value"
            || restoredViewModel.ApplicationItems.Any(item => item.ManagedApplication is not null))
        {
            restoredViewModel.Dispose();
            throw new InvalidOperationException("Saved apps were not restored as stopped configurations.");
        }

        Console.WriteLine("PASS: a new ViewModel restores saved applications without launching them");
        restoredViewModel.Dispose();

        await TestConfigurationFailuresAsync(testDirectory);

        // Launch setup: the "--sample value" arguments above only exercise persistence; for the launch each entry
        // runs the test program's window-child mode (one plain window, exits when its Job is closed).
        var firstReadyName = "ProcessGuardianVmWorkflowFirst_" + Guid.NewGuid().ToString("N");
        var secondReadyName = "ProcessGuardianVmWorkflowSecond_" + Guid.NewGuid().ToString("N");
        using var firstReady = new EventWaitHandle(false, EventResetMode.ManualReset, firstReadyName);
        using var secondReady = new EventWaitHandle(false, EventResetMode.ManualReset, secondReadyName);
        first.Arguments = "--window-child " + firstReadyName;
        second.Arguments = "--window-child " + secondReadyName;
        await viewModel.PendingSaveTask;

        await first.LaunchCommand.ExecuteAsync();
        await second.LaunchCommand.ExecuteAsync();
        if (!firstReady.WaitOne(8000) || !secondReady.WaitOne(8000))
        {
            throw new TimeoutException("The command-launched window-child test applications did not create their windows.");
        }

        await WaitForViewModelConditionAsync(
            () => first.StatusText == "Running" && second.StatusText == "Running",
            "both WPF command-launched applications to run");

        var firstPid = checked((int)first.ProcessId);
        var secondPid = checked((int)second.ProcessId);
        if (first.CanLaunch || second.CanLaunch || viewModel.RunningSoftwareCount != 2
            || viewModel.ManagedProcessCount != 2 || !first.JobStatusText.Contains("live Job member", StringComparison.Ordinal)
            || !first.MonitorStatusText.StartsWith("Monitoring:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Launch state, duplicate-launch guard, or process counters are incorrect.");
        }

        await first.LaunchCommand.ExecuteAsync();
        if (checked((int)first.ProcessId) != firstPid)
        {
            throw new InvalidOperationException("A running configuration launched a duplicate process.");
        }

        Console.WriteLine("PASS: Launch command starts two apps, updates counters, and blocks a duplicate launch");

        var nextLaunchDirectory = Path.Combine(testDirectory, "next-launch");
        Directory.CreateDirectory(nextLaunchDirectory);
        first.Arguments = "--changed-for-next-launch";
        first.WorkingDirectory = nextLaunchDirectory;
        await viewModel.PendingSaveTask;
        var editedWhileRunning = await configurationService.LoadAsync();
        if (first.ProcessId != firstPid || IsProcessExited(firstPid)
            || editedWhileRunning.Applications.Single(entry => entry.Id == first.Id).Arguments != "--changed-for-next-launch"
            || editedWhileRunning.Applications.Single(entry => entry.Id == first.Id).WorkingDirectory != nextLaunchDirectory)
        {
            throw new InvalidOperationException($"Editing next-launch settings changed the running process or failed to persist. ViewModel error: {viewModel.ConfigurationError ?? "none"}");
        }
        Console.WriteLine("PASS: argument and working-directory edits persist for the next launch without changing the active instance");

        unrelatedProcess = StartChild();
        await first.StopCommand.ExecuteAsync();
        if (first.StatusText != "Stopped" || !IsProcessExited(firstPid) || IsProcessExited(secondPid) || unrelatedProcess.HasExited
            || !first.RecentOperationResult.StartsWith("Stop completed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Stop did not stop only the selected WPF test application.");
        }

        await first.RemoveCommand.ExecuteAsync();
        if (viewModel.ApplicationItems.Count != 1)
        {
            throw new InvalidOperationException("Remove did not remove the stopped software configuration.");
        }

        Console.WriteLine("PASS: Stop and Remove commands release the first instance without affecting the second");

        await TestMonitoringUnavailableManualStopAsync(testDirectory, testChildExecutable);

        await second.RemoveCommand.ExecuteAsync();
        await viewModel.PendingSaveTask;
        await WaitForViewModelConditionAsync(() => IsProcessExited(secondPid), "Remove to stop the remaining application");
        if (viewModel.ApplicationItems.Count != 0)
        {
            throw new InvalidOperationException("Removing the active second app did not stop and remove it.");
        }

        Console.WriteLine("PASS: Remove on a running app stops it before removing its list item");

        var afterRemove = await configurationService.LoadAsync();
        if (afterRemove.Error is not null || afterRemove.Applications.Count != 0)
        {
            throw new InvalidOperationException($"Removing app configurations did not update the saved JSON. ViewModel error: {viewModel.ConfigurationError ?? "none"}; load error: {afterRemove.Error?.Message ?? "none"}");
        }

        Console.WriteLine("PASS: deleting applications updates the saved configuration");

        viewModel.AddApplicationCommand.Execute(null);
        var pendingLaunch = viewModel.ApplicationItems.Single();
        viewModel.BeginExit();
        if (!viewModel.IsExiting || pendingLaunch.LaunchCommand.CanExecute(null)
            || viewModel.AddApplicationCommand.CanExecute(null))
        {
            throw new InvalidOperationException("ViewModel still accepted launch or add requests after Exit began.");
        }

        await viewModel.WaitForOperationsAsync();
        Console.WriteLine("PASS: ViewModel rejects new application operations after Exit begins");
    }
    finally
    {
        if (unrelatedProcess is not null)
        {
            StopTestChild(unrelatedProcess);
            unrelatedProcess.Dispose();
        }

        viewModel.Dispose();
        try
        {
            // The last Add schedules a save; deleting before it finishes lets SaveAsync recreate the folder.
            await viewModel.PendingSaveTask;
            DeleteDirectoryWithRetry(testDirectory);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
            Console.Error.WriteLine($"CLEANUP-FAIL: could not remove {testDirectory}: {exception}");
        }
    }

    if (cleanupFailure is not null)
    {
        throw new InvalidOperationException($"Could not remove the ViewModel test directory {testDirectory}.", cleanupFailure);
    }

    if (Directory.Exists(testDirectory))
    {
        throw new InvalidOperationException($"The ViewModel test directory reappeared after cleanup: {testDirectory}");
    }

    Console.WriteLine("PASS: ViewModel test directory is removed after the final pending save completes");
}

static async Task RunConfigurationTransientLockTestAsync()
{
    var directory = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", $"ProcessGuardianConfigLockTests_{Guid.NewGuid():N}");
    try
    {
        var path = Path.Combine(directory, "config.json");
        var service = new ConfigurationService(path);
        await service.SaveAsync([]);
        var entry = new ApplicationConfiguration
        {
            Id = Guid.NewGuid(),
            SoftwareName = "Transient lock test",
            ExecutablePath = Environment.ProcessPath ?? "transient-lock-test.exe",
            AddedAt = DateTimeOffset.UtcNow,
        };

        Task transientSave;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            transientSave = service.SaveAsync([entry]);
            await Task.Delay(100);
        }

        await transientSave;
        var afterTransient = await service.LoadAsync();
        if (afterTransient.Error is not null || afterTransient.Applications.Count != 1
            || afterTransient.Applications[0].Id != entry.Id)
        {
            throw new InvalidOperationException($"A save blocked by a transient lock was not committed. Load error: {afterTransient.Error?.Message ?? "none"}");
        }

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            try
            {
                await service.SaveAsync([]);
                throw new InvalidOperationException("A save blocked by a persistent lock was not surfaced to the caller.");
            }
            catch (IOException)
            {
            }
        }

        var afterPersistent = await service.LoadAsync();
        if (afterPersistent.Error is not null || afterPersistent.Applications.Count != 1
            || Directory.GetFiles(directory, "*.tmp").Length != 0)
        {
            throw new InvalidOperationException("A persistently locked save did not preserve the original configuration and clean its temporary file.");
        }

        Console.WriteLine("PASS: configuration save retries a transient file lock and surfaces a persistent one without losing data");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static async Task TestConfigurationFailuresAsync(string testDirectory)
{
    var missingPath = Path.Combine(testDirectory, "missing-exe.json");
    var missingConfig = new ConfigurationService(missingPath);
    await missingConfig.SaveAsync([
        new ApplicationConfiguration
        {
            Id = Guid.NewGuid(),
            SoftwareName = "Missing app",
            ExecutablePath = Path.Combine(testDirectory, "does-not-exist.exe"),
            AddedAt = DateTimeOffset.UtcNow,
        },
    ]);

    var missingResult = await missingConfig.LoadAsync();
    if (missingResult.Error is not null || missingResult.Applications.Count != 1
        || File.Exists(missingResult.Applications[0].ExecutablePath))
    {
        throw new InvalidOperationException("A missing executable configuration was not retained for UI warning state.");
    }

    var missingViewModel = new MainViewModel(
        new ManagedApplicationService(synchronizationContext: SynchronizationContext.Current),
        new FixedExecutableFilePicker(),
        new ConfigurationService(missingPath));
    await missingViewModel.InitializationTask;
    if (missingViewModel.ApplicationItems.Count != 1
        || missingViewModel.ApplicationItems[0].StatusText != "Missing executable"
        || missingViewModel.ApplicationItems[0].CanLaunch
        || missingViewModel.ApplicationItems[0].ManagedApplication is not null)
    {
        missingViewModel.Dispose();
        throw new InvalidOperationException("A missing executable did not display a safe non-launchable state.");
    }

    missingViewModel.Dispose();
    Console.WriteLine("PASS: missing executable is displayed and cannot auto-launch");

    var packagedPath = Path.Combine(testDirectory, "WindowsApps", "Package.exe");
    var packagedConfigPath = Path.Combine(testDirectory, "packaged-path.json");
    await new ConfigurationService(packagedConfigPath).SaveAsync([
        new ApplicationConfiguration
        {
            Id = Guid.NewGuid(),
            SoftwareName = "Packaged path clue",
            ExecutablePath = packagedPath,
            AddedAt = DateTimeOffset.UtcNow,
        },
    ]);
    var packagedViewModel = new MainViewModel(
        new ManagedApplicationService(synchronizationContext: SynchronizationContext.Current),
        new FixedExecutableFilePicker(),
        new ConfigurationService(packagedConfigPath));
    await packagedViewModel.InitializationTask;
    if (!packagedViewModel.ApplicationItems.Single().CompatibilityWarningText.StartsWith("Possible limitation:", StringComparison.Ordinal)
        || !packagedViewModel.ApplicationItems.Single().CompatibilityWarningText.Contains("may require", StringComparison.Ordinal))
    {
        packagedViewModel.Dispose();
        throw new InvalidOperationException("A WindowsApps path was presented as certainty instead of a possibility.");
    }
    packagedViewModel.Dispose();
    Console.WriteLine("PASS: WindowsApps path is shown as a possible packaging limitation, not confirmed detection");

    var corruptPath = Path.Combine(testDirectory, "corrupt.json");
    const string corruptContents = "{ invalid json";
    await File.WriteAllTextAsync(corruptPath, corruptContents);
    var corruptResult = await new ConfigurationService(corruptPath).LoadAsync();
    if (corruptResult.Error is not JsonException || await File.ReadAllTextAsync(corruptPath) != corruptContents)
    {
        throw new InvalidOperationException("Corrupt JSON was not reported while preserving its original contents.");
    }

    Console.WriteLine("PASS: corrupt JSON is reported and the original file is preserved");

    var blockedParent = Path.Combine(testDirectory, "not-a-directory");
    await File.WriteAllTextAsync(blockedParent, "block");
    try
    {
        await new ConfigurationService(Path.Combine(blockedParent, "config.json")).SaveAsync([]);
        throw new InvalidOperationException("Expected configuration save failure was not reported.");
    }
    catch (IOException)
    {
        Console.WriteLine("PASS: configuration write failures are surfaced to the caller");
        var failedDefaultPath = Path.Combine(blockedParent, "new-config.json");
        var failedDefault = await new ConfigurationService(failedDefaultPath).LoadAsync();
        if (failedDefault.Error is null || failedDefault.FailureStage != ConfigurationLoadFailureStage.CreateDefault)
            throw new InvalidOperationException("Failure to create a default configuration was not distinguished from a read failure.");

        var failedDefaultViewModel = new MainViewModel(
            new ManagedApplicationService(synchronizationContext: SynchronizationContext.Current),
            new FixedExecutableFilePicker(),
            new ConfigurationService(failedDefaultPath));
        await failedDefaultViewModel.InitializationTask;
        if (!failedDefaultViewModel.ConfigurationError!.Contains("Could not create the default configuration", StringComparison.Ordinal)
            || !failedDefaultViewModel.ConfigurationError.Contains(failedDefaultPath, StringComparison.Ordinal)
            || failedDefaultViewModel.AddApplicationCommand.CanExecute(null))
        {
            failedDefaultViewModel.Dispose();
            throw new InvalidOperationException("Configuration-directory write failure was not safely reported in the UI.");
        }

        failedDefaultViewModel.Dispose();
        Console.WriteLine("PASS: default configuration directory failure is clearly reported without enabling edits");

    var restorePath = Path.Combine(testDirectory, "restore.json");
    await new ConfigurationService(restorePath).SaveAsync([
        new ApplicationConfiguration
        {
            Id = Guid.NewGuid(),
            SoftwareName = "Restore test",
            ExecutablePath = Environment.ProcessPath ?? string.Empty,
            AddedAt = DateTimeOffset.UtcNow,
        },
    ]);

    var canceledRestore = new MainViewModel(
        new ManagedApplicationService(synchronizationContext: SynchronizationContext.Current),
        new FixedExecutableFilePicker(),
        new ConfigurationService(restorePath),
        confirmRestoreDefaults: () => false);
    await canceledRestore.InitializationTask;
    await canceledRestore.RestoreDefaultSettingsCommand.ExecuteAsync();
    if (canceledRestore.ApplicationItems.Count != 1)
    {
        canceledRestore.Dispose();
        throw new InvalidOperationException("Restore defaults changed settings without user confirmation.");
    }

    canceledRestore.Dispose();

    var confirmedRestore = new MainViewModel(
        new ManagedApplicationService(synchronizationContext: SynchronizationContext.Current),
        new FixedExecutableFilePicker(),
        new ConfigurationService(restorePath),
        confirmRestoreDefaults: () => true);
    await confirmedRestore.InitializationTask;
    await confirmedRestore.RestoreDefaultSettingsCommand.ExecuteAsync();
    await confirmedRestore.PendingSaveTask;
    var restoredDefaults = await new ConfigurationService(restorePath).LoadAsync();
    if (confirmedRestore.ApplicationItems.Count != 0 || restoredDefaults.Applications.Count != 0)
    {
        confirmedRestore.Dispose();
        throw new InvalidOperationException("Confirmed restore defaults did not clear saved applications.");
    }

    confirmedRestore.Dispose();
    Console.WriteLine("PASS: restore defaults requires confirmation and clears saved applications");
    }
}

static async Task WaitForViewModelConditionAsync(Func<bool> condition, string description)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline)
        {
            throw new TimeoutException($"Timed out waiting for {description}.");
        }

        await Task.Delay(50);
    }
}

static async Task TestMonitoringUnavailableManualStopAsync(string testDirectory, string testChildExecutable)
{
    var appDirectory = Path.Combine(testDirectory, "ambiguous-window-app");
    Directory.CreateDirectory(appDirectory);
    var childPidFile = Path.Combine(appDirectory, "children.txt");
    var readyName = "ProcessGuardianAmbiguousVm_" + Guid.NewGuid().ToString("N");
    using var readySignal = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
    var configPath = Path.Combine(appDirectory, "config.json");
    await new ConfigurationService(configPath).SaveAsync([
        new ApplicationConfiguration
        {
            Id = Guid.NewGuid(),
            SoftwareName = "Ambiguous window test",
            ExecutablePath = testChildExecutable,
            AddedAt = DateTimeOffset.UtcNow,
            Arguments = "--managed-window-child " + readyName + " " + QuoteWindowsArgumentForTest(childPidFile) + " 2",
            WorkingDirectory = appDirectory,
        },
    ]);

    var permitStop = false;
    var permitRemove = false;
    var unrelatedProcess = StartChild();
    var viewModel = new MainViewModel(
        new ManagedApplicationService(synchronizationContext: SynchronizationContext.Current),
        new FixedExecutableFilePicker(),
        new ConfigurationService(configPath),
        confirmStop: _ => permitStop,
        confirmRemove: _ => permitRemove);
    ApplicationItemViewModel? item = null;
    try
    {
        await viewModel.InitializationTask;
        item = viewModel.ApplicationItems.Single();
        await item.LaunchCommand.ExecuteAsync();
        if (!readySignal.WaitOne(8000)) throw new TimeoutException("The ambiguous-window test app did not create its test windows.");
        await WaitForViewModelConditionAsync(
            () => item.ManagedApplication?.WindowMonitor?.Status == WindowMonitorStatus.AmbiguousWindows,
            "ambiguous window status in the application view model");

        var managed = item.ManagedApplication ?? throw new InvalidOperationException("The managed test app was not retained.");
        var processIds = new[] { checked((int)managed.ProcessId) }.Concat(ReadChildPids(childPidFile)).ToArray();
        var expectedJobMembers = managed.LaunchResult!.JobObject.ActiveProcessCount;
        if (item.StatusText != "Monitoring unavailable" || !item.HasDetectedCompatibilityWarning
            || !item.CompatibilityWarningText.StartsWith("Detected:", StringComparison.Ordinal)
            || !item.MonitorStatusText.Contains("Multiple possible", StringComparison.Ordinal)
            || !item.JobStatusText.Contains("live Job member", StringComparison.Ordinal)
            || !item.JobStatusText.Contains($"Active; {expectedJobMembers} live Job member(s)", StringComparison.Ordinal)
            || expectedJobMembers < processIds.Length || !item.CanStop)
        {
            throw new InvalidOperationException($"Monitoring-unavailable state, warning, Job count, or safe manual Stop affordance was not shown (status={item.StatusText}; detected={item.HasDetectedCompatibilityWarning}; warning={item.CompatibilityWarningText}; monitor={item.MonitorStatusText}; job={item.JobStatusText}; canStop={item.CanStop}).");
        }

        await item.StopCommand.ExecuteAsync();
        if (!item.RecentOperationResult.StartsWith("Stop canceled", StringComparison.Ordinal)
            || processIds.Any(IsProcessExited) || unrelatedProcess.HasExited)
        {
            throw new InvalidOperationException("Canceling Stop changed the managed Job or affected an unrelated process.");
        }

        permitStop = true;
        await item.StopCommand.ExecuteAsync();
        WaitForProcessesExit(processIds, "manual Stop of an app whose main window is ambiguous");
        if (!item.RecentOperationResult.StartsWith("Stop completed", StringComparison.Ordinal)
            || item.JobStatusText != "Released" || unrelatedProcess.HasExited)
        {
            throw new InvalidOperationException("Manual Stop did not report its result, release the Job, or preserve an outside process.");
        }

        await item.RemoveCommand.ExecuteAsync();
        if (viewModel.ApplicationItems.Count != 1 || !item.RecentOperationResult.StartsWith("Remove canceled", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Canceling Remove removed the application configuration.");
        }

        permitRemove = true;
        await item.RemoveCommand.ExecuteAsync();
        if (viewModel.ApplicationItems.Count != 0 || unrelatedProcess.HasExited)
        {
            throw new InvalidOperationException("Confirmed Remove did not remove only the selected app entry.");
        }

        Console.WriteLine("PASS: monitoring unavailable keeps the Job alive, confirms Stop/Remove, cleans only this Job, and preserves an unrelated process");
    }
    finally
    {
        StopTestChild(unrelatedProcess);
        unrelatedProcess.Dispose();
        await viewModel.DisposeAsync();
        if (item?.ManagedApplication?.LaunchResult is { } launchResult
            && item.ManagedApplication.Status != ManagedApplicationStatus.Completed)
        {
            try { await Task.Run(launchResult.Dispose); } catch { }
        }
    }
}

static void RunRealTrayIconRegistrationTest()
{
    Exception? failure = null;
    var finished = new ManualResetEventSlim();
    var thread = new Thread(() =>
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ProcessGuardian.ico");
            using var icon = new System.Drawing.Icon(iconPath);
            using var tray = new TrayIconService(icon);
            if (!tray.IsVisible)
            {
                throw new InvalidOperationException("NotifyIcon did not remain visible after registration.");
            }

            tray.Dispose();
            if (tray.IsVisible)
            {
                throw new InvalidOperationException("NotifyIcon remained visible after disposal.");
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            finished.Set();
        }
    })
    {
        IsBackground = true,
        Name = "Process Guardian real NotifyIcon test",
    };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!finished.Wait(TimeSpan.FromSeconds(10)))
    {
        throw new TimeoutException("Real NotifyIcon registration test timed out.");
    }

    thread.Join();
    if (failure is not null)
    {
        throw new InvalidOperationException("Real NotifyIcon registration failed.", failure);
    }

    Console.WriteLine("PASS: real Windows NotifyIcon registered visible and was removed on disposal");
}

static void RunTrayCommandRouterTest()
{
    var fakeTray = new FakeTrayIconService();
    var openCount = 0;
    var applicationsCount = 0;
    var exitCount = 0;
    using (var router = new TrayCommandRouter(
        fakeTray,
        openWindow: () => openCount++,
        openApplications: () => applicationsCount++,
        exit: () =>
        {
            exitCount++;
            return Task.CompletedTask;
        }))
    {
        fakeTray.RaiseOpen();
        fakeTray.RaiseApplications();
        fakeTray.RaiseExit();
        if (!SpinWait.SpinUntil(() => exitCount == 1, TimeSpan.FromSeconds(3))
            || openCount != 1 || applicationsCount != 1)
        {
            throw new InvalidOperationException("Tray actions did not route to open, Applications, and Exit handlers.");
        }
    }

    fakeTray.RaiseOpen();
    if (openCount != 1)
    {
        throw new InvalidOperationException("Disposed tray router remained subscribed to callbacks.");
    }

    Console.WriteLine("PASS: tray Open, Applications, Exit routing and event unsubscription");
}

static async Task RunMainWindowTrayLifecycleTestAsync(string testExecutable)
{
    var testDirectory = Path.Combine(
        Environment.CurrentDirectory,
        ".test-artifacts",
        $"ProcessGuardianWindowTray_{Guid.NewGuid():N}");
    Directory.CreateDirectory(testDirectory);
    var configPath = Path.Combine(testDirectory, "config.json");
    var childPidFile = Path.Combine(testDirectory, "children.txt");
    var readyName = $"ProcessGuardianWindowTray_{Guid.NewGuid():N}";
    using var readySignal = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
    await new ConfigurationService(configPath).SaveAsync([
        new ApplicationConfiguration
        {
            Id = Guid.NewGuid(),
            SoftwareName = "Window hide lifecycle test",
            ExecutablePath = testExecutable,
            AddedAt = DateTimeOffset.UtcNow,
            Arguments = $"--managed-window-child {readyName} \"{childPidFile}\" 1",
            WorkingDirectory = testDirectory,
        },
    ]);

    var window = new MainWindow(new ConfigurationService(configPath), new FixedExecutableFilePicker());
    if (window.Icon is null)
    {
        throw new InvalidOperationException("The Process Guardian WPF icon resource was not loaded.");
    }

    var viewModel = window.ViewModel;
    var fakeTray = new FakeTrayIconService();
    var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    ApplicationExitCoordinator? exitCoordinator = null;

    try
    {
        window.Show();
        await viewModel.InitializationTask;
        var item = viewModel.ApplicationItems.Single();
        await item.LaunchCommand.ExecuteAsync();
        if (!readySignal.WaitOne(8000))
        {
            throw new TimeoutException("Managed test software did not create its window while hosted by MainWindow.");
        }

        await WaitForViewModelConditionAsync(
            () => item.ManagedApplication?.WindowMonitor?.Status == WindowMonitorStatus.Monitoring,
            "window monitoring before minimizing to tray");
        var managedApplication = item.ManagedApplication
            ?? throw new InvalidOperationException("The WPF item did not retain its managed application.");
        var monitor = managedApplication.WindowMonitor
            ?? throw new InvalidOperationException("The managed application has no WindowMonitor.");
        if (item.StatusText != "Running" || !item.JobStatusText.Contains("Active;", StringComparison.Ordinal)
            || !item.MonitorStatusText.StartsWith("Monitoring:", StringComparison.Ordinal)
            || !item.RecentOperationResult.StartsWith("Launched successfully", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(item.CompatibilityWarningText))
        {
            throw new InvalidOperationException("The real MainWindow item did not expose application, Job, monitor, operation, and compatibility state.");
        }

        viewModel.CurrentPage = "Compatibility";
        if (!viewModel.IsCompatibilityPage)
        {
            throw new InvalidOperationException("The Compatibility page was not selectable in the WPF view model.");
        }
        viewModel.CurrentPage = "Applications";
        var processIds = new[] { checked((int)managedApplication.ProcessId) }
            .Concat((await File.ReadAllLinesAsync(childPidFile)).Select(int.Parse))
            .ToArray();

        window.Close();
        if (window.IsVisible || viewModel.IsExiting || !item.CanStop
            || monitor.Status != WindowMonitorStatus.Monitoring)
        {
            throw new InvalidOperationException("Closing the WPF window stopped or hid managed application lifecycle state incorrectly.");
        }

        AssertProcessesAlive(processIds, "managed process tree after MainWindow was hidden");
        if (!fakeTray.IsVisible)
        {
            throw new InvalidOperationException("Tray service did not remain active while the main window was hidden.");
        }

        using var router = new TrayCommandRouter(
            fakeTray,
            openWindow: () => window.Show(),
            openApplications: () =>
            {
                viewModel.CurrentPage = "Applications";
                window.Show();
            },
            exit: () => exitCoordinator!.ExitAsync());

        fakeTray.RaiseOpen();
        if (!window.IsVisible || item.ManagedApplication?.ProcessId != managedApplication.ProcessId)
        {
            throw new InvalidOperationException("Tray Open did not restore the existing window and application state.");
        }

        fakeTray.RaiseApplications();
        window.Close();
        if (window.IsVisible || viewModel.CurrentPage != "Applications")
        {
            throw new InvalidOperationException("Tray Applications did not return to the Applications page.");
        }

        exitCoordinator = new ApplicationExitCoordinator(
            stopAcceptingOperations: viewModel.BeginExit,
            waitForOperations: viewModel.WaitForOperationsAsync,
            cleanup: async () =>
            {
                await viewModel.DisposeAsync();
                fakeTray.Dispose();
                window.AllowApplicationClose();
            },
            shutdown: () =>
            {
                window.AllowApplicationClose();
                window.Close();
                exited.TrySetResult();
            });

        fakeTray.RaiseExit();
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await exitCoordinator.ExitAsync();
        WaitForProcessesExit(processIds, "managed process tree after tray Exit");
        if (fakeTray.IsVisible || monitor.Status != WindowMonitorStatus.Stopped)
        {
            throw new InvalidOperationException("Tray Exit did not remove the icon and unhook WindowMonitor.");
        }

        try
        {
            _ = managedApplication.LaunchResult!.JobObject.ActiveProcessCount;
            throw new InvalidOperationException("Tray Exit left the managed Job Object handle open.");
        }
        catch (ObjectDisposedException)
        {
            // Expected: the tray Exit path disposed the managed application service.
        }

        Console.WriteLine("PASS: real WPF close hides without stopping software; routed tray restore and Exit clean all resources");
    }
    finally
    {
        await viewModel.DisposeAsync();

        window.AllowApplicationClose();
        window.Close();
        fakeTray.Dispose();
        try
        {
            Directory.Delete(testDirectory, recursive: true);
        }
        catch
        {
            // Preserve test outcome if temporary cleanup is blocked.
        }
    }
}

static async Task RunApplicationExitCoordinatorTest(string testExecutable)
{
    var readyName = $"ProcessGuardianExit_{Guid.NewGuid():N}";
    var testDirectory = Path.Combine(Environment.CurrentDirectory, ".test-artifacts");
    Directory.CreateDirectory(testDirectory);
    var childPidFile = Path.Combine(testDirectory, $"exit-children-{Guid.NewGuid():N}.txt");
    using var readySignal = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
    var service = new ManagedApplicationService();
    var application = service.Start(
        "Exit lifecycle test",
        testExecutable,
        $"--managed-window-child {readyName} \"{childPidFile}\" 1",
        testDirectory);
    var mainPid = checked((int)application.ProcessId);

    try
    {
        if (!readySignal.WaitOne(8000))
        {
            throw new TimeoutException("Exit lifecycle test child did not create its window and background processes.");
        }

        if (!SpinWait.SpinUntil(
            () => application.WindowMonitor?.Status == WindowMonitorStatus.Monitoring,
            TimeSpan.FromSeconds(8)))
        {
            throw new TimeoutException("Exit lifecycle test window was not monitored.");
        }

        var childPids = (await File.ReadAllLinesAsync(childPidFile)).Select(int.Parse).ToArray();
        var processIds = new[] { mainPid }.Concat(childPids).ToArray();
        AssertProcessesAlive(processIds, "exit lifecycle process tree before Exit");
        var monitor = application.WindowMonitor
            ?? throw new InvalidOperationException("WindowMonitor was not retained before application Exit.");
        var inFlightOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = new List<string>();
        var exitCoordinator = new ApplicationExitCoordinator(
            stopAcceptingOperations: () => steps.Add("stop"),
            waitForOperations: () =>
            {
                steps.Add("wait");
                return inFlightOperation.Task;
            },
            cleanup: () =>
            {
                steps.Add("cleanup");
                return Task.Run(service.Dispose);
            },
            shutdown: () => steps.Add("shutdown"));

        var firstExit = exitCoordinator.ExitAsync();
        var repeatedExit = exitCoordinator.ExitAsync();
        if (!ReferenceEquals(firstExit, repeatedExit) || !exitCoordinator.ExitRequested
            || steps.Count != 2 || steps[0] != "stop" || steps[1] != "wait")
        {
            throw new InvalidOperationException("Exit was not idempotent or did not stop accepting work before draining.");
        }

        AssertProcessesAlive(processIds, "managed processes while Exit waits for an in-flight operation");
        if (monitor.Status != WindowMonitorStatus.Monitoring)
        {
            throw new InvalidOperationException("WindowMonitor stopped before exit cleanup began.");
        }

        inFlightOperation.SetResult();
        await firstExit.WaitAsync(TimeSpan.FromSeconds(12));
        WaitForProcessesExit(processIds, "managed process tree after Exit");

        if (steps.Count != 4 || steps[2] != "cleanup" || steps[3] != "shutdown"
            || monitor.Status != WindowMonitorStatus.Stopped)
        {
            throw new InvalidOperationException("Exit did not clean Jobs and WindowMonitor before final shutdown.");
        }

        try
        {
            _ = application.LaunchResult!.JobObject.ActiveProcessCount;
            throw new InvalidOperationException("Job Object handle remained open after exit cleanup.");
        }
        catch (ObjectDisposedException)
        {
            // Expected: the service closed the Job Object after draining active operations.
        }

        Console.WriteLine("PASS: idempotent Exit drains work, then releases Job, WindowMonitor, and all test processes");
    }
    finally
    {
        service.Dispose();
        if (File.Exists(childPidFile))
        {
            File.Delete(childPidFile);
        }
    }
}

static void RunRepeatedManagedApplicationLifecycleTest(string testExecutable)
{
    var testDirectory = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", $"repeated-lifecycle-{Guid.NewGuid():N}");
    Directory.CreateDirectory(testDirectory);
    using var process = Process.GetCurrentProcess();
    var launcher = new ProcessLauncher();
    for (var iteration = 1; iteration <= 5; iteration++)
    {
        var readyName = $"ProcessGuardianLauncherRepeated_{Guid.NewGuid():N}";
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        var launch = launcher.Launch(testExecutable, $"--launcher-child {readyName}", testDirectory);
        using var child = Process.GetProcessById(checked((int)launch.ProcessId));
        if (!ready.WaitOne(8000))
        {
            launch.Dispose();
            throw new TimeoutException($"ProcessLauncher repeated iteration {iteration} did not start its dedicated child.");
        }

        var handlesBeforeClose = process.HandleCount;
        launch.Dispose();
        if (!child.WaitForExit(8000))
        {
            throw new TimeoutException($"ProcessLauncher repeated iteration {iteration} did not clean its Job member.");
        }

        process.Refresh();
        Console.WriteLine($"INFO: ProcessLauncher-only cycle {iteration}/5; process handles={handlesBeforeClose}->{process.HandleCount}");
    }

    for (var iteration = 1; iteration <= 5; iteration++)
    {
        var readyName = $"ProcessGuardianMonitorRepeated_{Guid.NewGuid():N}";
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        using var child = StartWindowChild(readyName);
        if (!ready.WaitOne(8000))
        {
            StopTestChild(child);
            throw new TimeoutException($"WindowMonitor-only repeated iteration {iteration} did not create its test window.");
        }

        process.Refresh();
        var handlesBeforeMonitor = process.HandleCount;
        var monitor = new WindowMonitor(checked((uint)child.Id));
        process.Refresh();
        var handlesAfterMonitorStart = process.HandleCount;
        using (monitor)
        {
            if (!monitor.IsHookInstalled)
            {
                throw new InvalidOperationException($"WindowMonitor-only iteration {iteration} did not install its test hook.");
            }
        }

        process.Refresh();
        var handlesAfterMonitorDispose = process.HandleCount;
        StopTestChild(child);
        child.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        process.Refresh();
        Console.WriteLine($"INFO: WindowMonitor-only cycle {iteration}/5; handles {handlesBeforeMonitor}->{handlesAfterMonitorStart}->{handlesAfterMonitorDispose}->{process.HandleCount}");
    }

    using var service = new ManagedApplicationService();
    var startHandles = process.HandleCount;
    var startThreads = GetThreadCountAndReleaseSnapshot(process);
    var maximumHandles = startHandles;
    var maximumThreads = startThreads;

    try
    {
        for (var iteration = 1; iteration <= 20; iteration++)
        {
            process.Refresh();
            var handlesBeforeIteration = process.HandleCount;
            var readyName = $"ProcessGuardianRepeated_{Guid.NewGuid():N}";
            var childPidFile = Path.Combine(testDirectory, $"children-{iteration}.txt");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
            var application = service.Start(
                $"Repeated lifecycle {iteration}",
                testExecutable,
                $"--managed-window-child {readyName} {QuoteArgument(childPidFile)} 1",
                testDirectory);

            if (application.Status == ManagedApplicationStatus.Failed || !ready.WaitOne(8000))
            {
                throw new InvalidOperationException($"Repeated lifecycle iteration {iteration} failed to create its test window.", application.Error);
            }

            WaitForCondition(
                () => application.WindowMonitor?.Status == WindowMonitorStatus.Monitoring,
                $"repeated lifecycle iteration {iteration} to install its window monitor");
            process.Refresh();
            var handlesAfterStart = process.HandleCount;
            var childPids = ReadChildPids(childPidFile);
            var processIds = new[] { checked((int)application.ProcessId) }.Concat(childPids).ToArray();
            AssertProcessesAlive(processIds, $"repeated lifecycle iteration {iteration} before Stop");
            AssertJobMembership(application, childPids);

            // Repeat Stop while cleanup is already completed to cover idempotent command paths.
            service.Stop(application);
            service.Stop(application);
            if (application.Status != ManagedApplicationStatus.Completed
                || application.WindowMonitor?.Status != WindowMonitorStatus.Stopped
                || application.WindowMonitor.IsHookInstalled)
            {
                throw new InvalidOperationException($"Repeated lifecycle iteration {iteration} did not finish with a stopped monitor and released Job.");
            }

            WaitForProcessesExit(processIds, $"repeated lifecycle iteration {iteration} after Stop");
            process.Refresh();
            var handlesAfterStop = process.HandleCount;
            maximumHandles = Math.Max(maximumHandles, process.HandleCount);
            maximumThreads = Math.Max(maximumThreads, GetThreadCountAndReleaseSnapshot(process));

            if (iteration % 5 == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                process.Refresh();
                Console.WriteLine($"INFO: lifecycle stress {iteration}/20; handles {handlesBeforeIteration}->{handlesAfterStart}->{handlesAfterStop}->{process.HandleCount}; threads={GetThreadCountAndReleaseSnapshot(process)}");
            }
        }

        service.Dispose();
        service.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        process.Refresh();
        var endHandles = process.HandleCount;
        var endThreads = GetThreadCountAndReleaseSnapshot(process);
        if (endHandles > startHandles + 12 || endThreads > startThreads + 8)
        {
            throw new InvalidOperationException(
                $"Process resource counts did not settle after 20 launch/Stop cycles (handles {startHandles}->{endHandles}, peak {maximumHandles}; threads {startThreads}->{endThreads}, peak {maximumThreads}).");
        }

        Console.WriteLine($"PASS: 20 launch/Stop cycles cleaned every process and monitor; handles {startHandles}->{endHandles} (peak {maximumHandles}), threads {startThreads}->{endThreads} (peak {maximumThreads})");
    }
    finally
    {
        service.Dispose();
        try { Directory.Delete(testDirectory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

static int GetThreadCountAndReleaseSnapshot(Process process)
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

static void AssertNoDedicatedTestChildrenRemain()
{
    using var current = Process.GetCurrentProcess();
    foreach (var candidate in Process.GetProcessesByName(current.ProcessName))
    {
        using (candidate)
        {
            if (candidate.Id == current.Id || candidate.HasExited)
            {
                continue;
            }

            try
            {
                string imagePath;
                try { imagePath = candidate.MainModule?.FileName ?? "<image unavailable>"; }
                catch (Exception exception) { imagePath = "<image unavailable: " + exception.GetType().Name + ">"; }
                var commandLine = TestProcessNativeMethods.GetProcessCommandLine(candidate.Handle) ?? "<command line unavailable>";
                throw new InvalidOperationException($"Dedicated test process {candidate.Id} ({imagePath}; {commandLine}) remains alive after the full test suite.");
            }
            catch (InvalidOperationException) when (candidate.HasExited)
            {
                // The process can exit between the initial process snapshot and the live check.
            }
        }
    }
}

static string FindGuardianExecutable()
{
    var localCandidate = Path.Combine(AppContext.BaseDirectory, "ProcessGuardian.exe");
    if (File.Exists(localCandidate))
    {
        return localCandidate;
    }

    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        var candidate = Path.Combine(directory.FullName, "ProcessGuardian", "bin", "Debug", "net10.0-windows", "ProcessGuardian.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    throw new FileNotFoundException("The built WPF application is required for the UI workflow test.");
}

sealed class FakeTrayIconService : ITrayIconService
{
    public event EventHandler? OpenRequested;

    public event EventHandler? ApplicationsRequested;

    public event EventHandler? ExitRequested;

    public bool IsVisible { get; private set; } = true;

    public void RaiseOpen() => OpenRequested?.Invoke(this, EventArgs.Empty);

    public void RaiseApplications() => ApplicationsRequested?.Invoke(this, EventArgs.Empty);

    public void RaiseExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose() => IsVisible = false;
}
