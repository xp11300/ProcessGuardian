using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using ProcessGuardian.Models;
using ProcessGuardian.Services.CommandLine;
using ProcessGuardian.Services.Configuration;
using ProcessGuardian.Services.Logging;
using ProcessGuardian.Services.ManagedApplications;
using ProcessGuardian.Services.ProcessManagement;
using ProcessGuardian.Services.Startup;
using ProcessGuardian.ViewModels;

namespace ProcessGuardian.JobObjectTests;

/// <summary>
/// "Start with Windows" (Settings checkbox, HKCU Run value, --autostart). Registry tests use only a private key
/// HKCU\Software\ProcessGuardian.Tests\AutoStart-&lt;guid&gt; that is deleted afterwards; the real
/// HKCU\...\CurrentVersion\Run "ProcessGuardian" value is recorded before and compared after, and is never written.
/// The end-to-end checks start the real ProcessGuardian.exe in a test-owned Job with an isolated data directory.
/// </summary>
internal static class AutoStartTests
{
    private const string TestRootPath = @"Software\ProcessGuardian.Tests";
    private const string DataDirectoryVariable = "PROCESSGUARDIAN_DATA_DIR";
    private const string FakeExecutable = @"C:\Program Files\PG AutoStart Test\ProcessGuardian.exe";

    public static async Task RunAsync(string guardianExecutable, string testExecutable)
    {
        guardianExecutable = Path.GetFullPath(guardianExecutable);
        testExecutable = Path.GetFullPath(testExecutable);
        var realBefore = ReadRealRunValue();
        var testsRootExisted = Registry.CurrentUser.OpenSubKey(TestRootPath) is { } existing && Dispose(existing);
        var keyPath = TestRootPath + @"\AutoStart-" + Guid.NewGuid().ToString("N");
        try
        {
            RunServiceTests(keyPath);
            await RunServiceFailureTestsAsync(keyPath);
            RunCommandLineTests();
            RunViewModelTests(keyPath);
            await RunEndToEndTestsAsync(guardianExecutable, testExecutable);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            if (!testsRootExisted)
            {
                using var root = Registry.CurrentUser.OpenSubKey(TestRootPath);
                if (root is not null && root.SubKeyCount == 0 && root.ValueCount == 0)
                {
                    root.Dispose();
                    Registry.CurrentUser.DeleteSubKey(TestRootPath, throwOnMissingSubKey: false);
                }
            }
        }

        Require(Registry.CurrentUser.OpenSubKey(keyPath) is not { } left || !Dispose(left), $"The test registry key HKCU\\{keyPath} was not removed.");
        Require(testsRootExisted || Registry.CurrentUser.OpenSubKey(TestRootPath) is not { } rootLeft || !Dispose(rootLeft),
            $"The test registry root HKCU\\{TestRootPath} (created by this test) was not removed.");
        var realAfter = ReadRealRunValue();
        Require(realBefore == realAfter, $"The real HKCU Run \"ProcessGuardian\" value changed: before {realBefore}, after {realAfter}.");
        Console.WriteLine($"PASS: auto-start tests removed their registry key and left the real HKCU Run \"ProcessGuardian\" value unchanged ({realAfter}).");
    }

    private static void RunServiceTests(string keyPath)
    {
        using var root = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        var service = new AutoStartService(root, "Run", @"StartupApproved\Run", FakeExecutable);
        var expected = $"\"{FakeExecutable}\" --autostart";
        Require(service.ExpectedCommand == expected, $"Unexpected command: {service.ExpectedCommand}");
        Require(service.GetStatus() is { State: AutoStartState.Disabled, Error: null, IsRegistered: false }, "A missing Run key must read as Disabled.");

        using (var run = root.CreateSubKey("Run", writable: true))
        {
            run.SetValue("OtherApp", "\"C:\\Other\\other.exe\" /min", RegistryValueKind.String);
        }

        Require(service.Enable().Succeeded, "Enable failed on the test key.");
        Require(ReadValue(root, "Run", "ProcessGuardian") is (string data, RegistryValueKind.String) && data == expected,
            "Enable must write a REG_SZ with the quoted path (with spaces) and --autostart.");
        var enabled = service.GetStatus();
        Require(enabled.State == AutoStartState.Enabled && enabled.RegisteredExecutablePath == FakeExecutable && !enabled.DisabledInTaskManager,
            $"Expected Enabled with the current path, got {enabled}.");
        Require(service.Enable().Succeeded && service.GetStatus().State == AutoStartState.Enabled, "A second Enable must overwrite and stay Enabled.");

        WriteRun(root, "\"D:\\Other Dir\\ProcessGuardian.exe\" --autostart");
        var different = service.GetStatus();
        Require(different.State == AutoStartState.EnabledDifferentPath && different.RegisteredExecutablePath == @"D:\Other Dir\ProcessGuardian.exe",
            $"A quoted other path must read as EnabledDifferentPath, got {different}.");
        WriteRun(root, @"C:\Old\ProcessGuardian.exe --autostart");
        Require(service.GetStatus() is { State: AutoStartState.EnabledDifferentPath, RegisteredExecutablePath: @"C:\Old\ProcessGuardian.exe" },
            "An unquoted other path must read as EnabledDifferentPath with that path.");
        WriteRun(root, $"\"{FakeExecutable}\"");
        Require(service.GetStatus().State == AutoStartState.EnabledDifferentPath, "The current path without --autostart must not count as Enabled.");
        WriteRun(root, $"\"{FakeExecutable.ToLowerInvariant()}\"  --AUTOSTART ");
        Require(service.GetStatus().State == AutoStartState.Enabled, "Path and option comparisons must be case-insensitive.");
        using (var run = root.CreateSubKey("Run", writable: true))
        {
            run.SetValue("ProcessGuardian", 1, RegistryValueKind.DWord);
        }

        Require(service.GetStatus().State == AutoStartState.EnabledDifferentPath, "A non-string Run value must read as EnabledDifferentPath.");
        Require(service.Enable().Succeeded && service.GetStatus().State == AutoStartState.Enabled, "Enable must replace a different-path value (re-check updates the path).");

        using (var approved = root.CreateSubKey(@"StartupApproved\Run", writable: true))
        {
            approved.SetValue("ProcessGuardian", new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        }

        Require(service.GetStatus() is { State: AutoStartState.Enabled, DisabledInTaskManager: true }, "StartupApproved 0x03 must read as disabled in Task Manager.");
        using (var approved = root.CreateSubKey(@"StartupApproved\Run", writable: true))
        {
            approved.SetValue("ProcessGuardian", new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        }

        Require(service.GetStatus() is { DisabledInTaskManager: false }, "StartupApproved 0x02 must read as enabled.");
        var approvedBefore = ReadValue(root, @"StartupApproved\Run", "ProcessGuardian");

        Require(service.Disable().Succeeded, "Disable failed on the test key.");
        Require(ReadValue(root, "Run", "ProcessGuardian") is null && service.GetStatus().State == AutoStartState.Disabled, "Disable must delete the value.");
        Require(service.Disable().Succeeded, "Disable without a value must succeed (no-op).");
        Require(ReadValue(root, "Run", "OtherApp") is ("\"C:\\Other\\other.exe\" /min", RegistryValueKind.String), "Other Run values must be untouched.");
        var approvedAfter = ReadValue(root, @"StartupApproved\Run", "ProcessGuardian");
        Require(approvedBefore is (byte[] a, _) && approvedAfter is (byte[] b, _) && a.SequenceEqual(b), "StartupApproved must never be written.");

        var noPath = new AutoStartService(root, "Run", null, executablePath: null);
        var noPathResult = noPath.Enable();
        Require(!noPathResult.Succeeded && !string.IsNullOrWhiteSpace(noPathResult.Error) && ReadValue(root, "Run", "ProcessGuardian") is null,
            "Enable without an executable path must fail without writing.");
        Console.WriteLine("PASS: AutoStartService enable/disable/status on a test key (quoted path with spaces, different path, case, non-string, StartupApproved read-only, other values untouched).");
    }

    private static async Task RunServiceFailureTestsAsync(string keyPath)
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "PG-AutoStartTests-Log-" + Guid.NewGuid().ToString("N"));
        var logger = new FileLogService(logDirectory);
        var previous = AppLog.Replace(logger);
        try
        {
            // A read-only key: writing the Run value fails with UnauthorizedAccessException and is reported and logged.
            using (Registry.CurrentUser.CreateSubKey(keyPath + @"\ReadOnly"))
            {
            }

            using var readOnly = Registry.CurrentUser.OpenSubKey(keyPath + @"\ReadOnly", writable: false)!;
            var service = new AutoStartService(readOnly, "Run", null, FakeExecutable);
            var result = service.Enable();
            Require(!result.Succeeded && result.Exception is UnauthorizedAccessException && result.Error!.Contains("UnauthorizedAccessException", StringComparison.Ordinal),
                $"A read-only key must fail with UnauthorizedAccessException (got {result}).");
            Require(service.GetStatus().State == AutoStartState.Disabled, "Nothing may be written through a read-only key.");

            // A throwing store: every operation becomes a failure result, nothing escapes.
            var throwing = new AutoStartService(new ThrowingStore(), FakeExecutable);
            var status = throwing.GetStatus();
            Require(status.State == AutoStartState.Disabled && status.Error!.Contains("SecurityException", StringComparison.Ordinal), $"A read failure must be reported (got {status}).");
            var enable = throwing.Enable();
            Require(!enable.Succeeded && enable.Exception is IOException, $"A write IOException must be a failure result (got {enable}).");
            var disable = throwing.Disable();
            Require(!disable.Succeeded && disable.Exception is UnauthorizedAccessException, $"A delete failure must be a failure result (got {disable}).");

            await logger.FlushAsync();
            var entries = await logger.ReadRecentAsync();
            Require(entries.Count(entry => entry.Module == "AutoStart" && entry.Level == LogLevel.Warning) >= 4
                && entries.Any(entry => entry.Module == "AutoStart" && entry.Message.Contains("UnauthorizedAccessException", StringComparison.Ordinal)),
                $"Registry failures must be logged as AutoStart warnings (got {entries.Count} entries).");
        }
        finally
        {
            AppLog.Replace(previous);
            await logger.DisposeAsync();
            DeleteDirectory(logDirectory);
        }

        Console.WriteLine("PASS: AutoStartService registry failures (read-only key, Security/IO/UnauthorizedAccess exceptions) become logged failure results without throwing.");
    }

    private static void RunCommandLineTests()
    {
        foreach (var args in new[] { new[] { "--autostart" }, new[] { "--AutoStart" } })
        {
            var parsed = CommandLineParser.Parse(args);
            Require(parsed.IsValid && parsed.Options!.IsAutoStart && !parsed.Options.HasLaunch, $"{string.Join(" ", args)} must parse as auto-start.");
        }

        Require(CommandLineParser.Parse([]).Options is { IsAutoStart: false, HasLaunch: false }, "No arguments must not be auto-start.");
        Require(CommandLineParser.Parse(["--launch", "X"]).Options is { IsAutoStart: false, LaunchSelector: "X" }, "--launch must not set auto-start.");
        Require(CommandLineParser.Parse(["--launch=X"]).Options is { IsAutoStart: false, LaunchSelector: "X" }, "--launch= must not set auto-start.");
        var invalid = new (string[] Args, string Error)[]
        {
            (["--autostart", "--autostart"], "--autostart can be given only once."),
            (["--autostart=1"], "--autostart does not take a value."),
            (["--autostart="], "--autostart does not take a value."),
            (["--autostart", "--launch", "X"], "--autostart cannot be combined with --launch."),
            (["--launch", "X", "--autostart"], "--autostart cannot be combined with --launch."),
            (["--launch=X", "--autostart"], "--autostart cannot be combined with --launch."),
            (["--autostart", "--launch=X"], "--autostart cannot be combined with --launch."),
            (["--autostart", "extra"], "Unexpected extra argument: \"extra\"."),
            (["--autostartx"], "Unknown argument: \"--autostartx\"."),
            (["--launch", "--autostart"], "--launch needs a value: the software name or id of an entry."),
        };
        foreach (var (args, error) in invalid)
        {
            var parsed = CommandLineParser.Parse(args);
            Require(!parsed.IsValid && parsed.Error == error, $"{string.Join(" ", args)}: expected \"{error}\", got \"{parsed.Error}\".");
        }

        Require(CommandLineParser.UsageText.Contains("--autostart", StringComparison.Ordinal) && CommandLineParser.UsageText.Contains("--launch", StringComparison.Ordinal),
            "The usage text must list --autostart and still list --launch.");

        var plain = new CommandLineOptions(null);
        var launch = new CommandLineOptions("X");
        var auto = new CommandLineOptions(null, IsAutoStart: true);
        Require(!StartupWindowPolicy.ShouldStartHidden(plain, trayAvailable: true), "A plain start must show the window.");
        Require(StartupWindowPolicy.ShouldStartHidden(launch, trayAvailable: true), "--launch with a tray must start hidden.");
        Require(!StartupWindowPolicy.ShouldStartHidden(launch, trayAvailable: false), "--launch without a tray must show the window.");
        Require(StartupWindowPolicy.ShouldStartHidden(auto, trayAvailable: true), "--autostart with a tray must start hidden.");
        Require(!StartupWindowPolicy.ShouldStartHidden(auto, trayAvailable: false), "--autostart without a tray must show the window.");
        Console.WriteLine("PASS: --autostart parsing (alone, case, duplicate, value, combined with --launch, extra) and the cold-start hidden decision.");
    }

    private static void RunViewModelTests(string keyPath)
    {
        RunOnStaDispatcher(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "PG-AutoStartTests-VM-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var configPath = Path.Combine(directory, "config.json");
            File.WriteAllText(configPath, "[]", new UTF8Encoding(false));
            var configHash = HashFile(configPath);
            using var root = Registry.CurrentUser.CreateSubKey(keyPath + @"\ViewModel", writable: true);
            var errors = new List<string>();
            var service = new ManagedApplicationService(synchronizationContext: SynchronizationContext.Current);
            var viewModel = new MainViewModel(service, new FixedExecutableFilePicker(), new ConfigurationService(configPath),
                autoStartService: new AutoStartService(root, "Run", null, FakeExecutable), showAutoStartError: errors.Add);
            var raised = 0;
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.IsAutoStartEnabled)) raised++;
            };
            try
            {
                await viewModel.InitializationTask;
                Require(viewModel.IsAutoStartAvailable && !viewModel.IsAutoStartEnabled && !viewModel.HasAutoStartHint, "Initial state must be available and unchecked.");
                Require(viewModel.SetAutoStartCommand.CanExecute(true), "The checkbox command must be enabled.");
                viewModel.SetAutoStartCommand.Execute(true);
                Require(viewModel.IsAutoStartEnabled && ReadValue(root, "Run", "ProcessGuardian") is (string, _) && errors.Count == 0, "Checking must write the value and show checked.");

                // An outside change is picked up when the Settings tab is shown.
                WriteRun(root, "\"D:\\Moved Folder\\ProcessGuardian.exe\" --autostart");
                viewModel.CurrentPage = "Settings";
                Require(viewModel.IsAutoStartEnabled && viewModel.AutoStartHint == "启动项路径与当前程序不一致：D:\\Moved Folder\\ProcessGuardian.exe，重新勾选以更新",
                    $"Showing Settings must re-read the registry and show the different-path hint (got '{viewModel.AutoStartHint}').");
                viewModel.SetAutoStartCommand.Execute(false);
                Require(!viewModel.IsAutoStartEnabled && ReadValue(root, "Run", "ProcessGuardian") is null && !viewModel.HasAutoStartHint, "Unchecking must delete the value.");
                viewModel.SetAutoStartCommand.Execute(true);
                Require(viewModel.IsAutoStartEnabled && !viewModel.HasAutoStartHint && ReadValue(root, "Run", "ProcessGuardian") is (string data, _) && data.Contains(FakeExecutable, StringComparison.Ordinal),
                    "Re-checking must update the path to the current executable.");
                viewModel.SetAutoStartCommand.Execute(false);
                Require(HashFile(configPath) == configHash, "The auto-start setting must not be stored in config.json.");
                Console.WriteLine("PASS: Settings checkbox follows the registry (check, uncheck, outside change on tab show, different-path hint, re-check updates the path); config.json unchanged.");
            }
            finally
            {
                await viewModel.DisposeAsync();
                service.Dispose();
            }

            // Failure: a read-only key cannot be written; the checkbox reverts (IsAutoStartEnabled re-raised as false).
            using (Registry.CurrentUser.CreateSubKey(keyPath + @"\ViewModelReadOnly"))
            {
            }

            using var readOnly = Registry.CurrentUser.OpenSubKey(keyPath + @"\ViewModelReadOnly", writable: false)!;
            var failService = new ManagedApplicationService(synchronizationContext: SynchronizationContext.Current);
            var failErrors = new List<string>();
            var failViewModel = new MainViewModel(failService, new FixedExecutableFilePicker(), new ConfigurationService(configPath),
                autoStartService: new AutoStartService(readOnly, "Run", null, FakeExecutable), showAutoStartError: failErrors.Add);
            var failRaised = 0;
            failViewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.IsAutoStartEnabled)) failRaised++;
            };
            var throwingService = new ManagedApplicationService(synchronizationContext: SynchronizationContext.Current);
            var throwingErrors = new List<string>();
            var store = new ThrowingStore { ReadValue = "\"" + FakeExecutable + "\" --autostart" };
            var throwingViewModel = new MainViewModel(throwingService, new FixedExecutableFilePicker(), new ConfigurationService(configPath),
                autoStartService: new AutoStartService(store, FakeExecutable), showAutoStartError: throwingErrors.Add);
            var unavailableService = new ManagedApplicationService(synchronizationContext: SynchronizationContext.Current);
            var unavailable = new MainViewModel(unavailableService, new FixedExecutableFilePicker(), new ConfigurationService(configPath));
            try
            {
                await failViewModel.InitializationTask;
                var result = failViewModel.SetAutoStart(true);
                Require(result is { Succeeded: false } && !failViewModel.IsAutoStartEnabled && failRaised >= 1, "A failed enable must leave the checkbox unchecked and re-raise the state.");
                Require(failErrors.Count == 1 && failErrors[0].StartsWith("无法开启开机自动启动。", StringComparison.Ordinal)
                    && failErrors[0].Contains("UnauthorizedAccessException", StringComparison.Ordinal), $"Expected one Chinese error with the reason, got [{string.Join(" | ", failErrors)}].");

                await throwingViewModel.InitializationTask;
                Require(throwingViewModel.IsAutoStartEnabled, "The throwing store reports a registered value.");
                var disable = throwingViewModel.SetAutoStart(false);
                Require(disable is { Succeeded: false } && throwingViewModel.IsAutoStartEnabled && throwingErrors.Count == 1
                    && throwingErrors[0].StartsWith("无法关闭开机自动启动。", StringComparison.Ordinal), "A failed disable must keep the checkbox checked and show the error.");

                await unavailable.InitializationTask;
                Require(!unavailable.IsAutoStartAvailable && !unavailable.IsAutoStartEnabled && !unavailable.SetAutoStartCommand.CanExecute(true),
                    "Without a service the setting must be unavailable and never touch the registry.");
                Require(unavailable.SetAutoStart(true) is null, "Without a service SetAutoStart must do nothing.");
            }
            finally
            {
                await failViewModel.DisposeAsync();
                failService.Dispose();
                await throwingViewModel.DisposeAsync();
                throwingService.Dispose();
                await unavailable.DisposeAsync();
                unavailableService.Dispose();
                DeleteDirectory(directory);
            }

            Console.WriteLine("PASS: a failed registry write/delete reverts the checkbox to the real state and shows one Chinese error with the reason; no service = unavailable.");
        }, TimeSpan.FromSeconds(60), "AutoStart ViewModel");
    }

    private static async Task RunEndToEndTestsAsync(string guardianExecutable, string testExecutable)
    {
        var root = Path.Combine(Path.GetTempPath(), "PG-AutoStartTests-" + Guid.NewGuid().ToString("N"));
        var dataA = Path.Combine(root, "A");
        var dataB = Path.Combine(root, "B");
        Directory.CreateDirectory(dataA);
        Directory.CreateDirectory(dataB);
        var tag = Guid.NewGuid().ToString("N")[..12];
        var entryEvent = $"PGAS_Entry_{tag}";
        using var entryReady = new EventWaitHandle(false, EventResetMode.ManualReset, entryEvent);
        WriteConfiguration(dataA, new ApplicationConfiguration
        {
            Id = Guid.NewGuid(),
            SoftwareName = "AS Entry",
            ExecutablePath = testExecutable,
            Arguments = "--window-child " + entryEvent,
            AddedAt = DateTimeOffset.UtcNow,
        });
        File.WriteAllText(Path.Combine(dataB, "config.json"), "{ this is not valid json", new UTF8Encoding(false));
        var configHash = HashFile(Path.Combine(dataA, "config.json"));
        var launcher = new ProcessLauncher();
        ProcessLaunchResult? guardianA = null;
        ProcessLaunchResult? guardianB = null;
        try
        {
            // 1. Cold --autostart: stays in the tray, launches nothing.
            guardianA = LaunchGuardian(launcher, guardianExecutable, dataA, "--autostart");
            await WaitForLogAsync(dataA, line => line.Message == "Startup --autostart: configuration loaded; staying in the tray", "--autostart stays in the tray");
            await Task.Delay(1500);
            Require(!HasVisibleTopLevelWindow(guardianA.ProcessId), "A cold --autostart start must not show a window.");
            Require(!entryReady.WaitOne(0), "--autostart must not launch any entry.");
            Require(CountLog(dataA, line => line.Message == "Process Guardian started") == 1, "Exactly one Process Guardian must have started.");
            Console.WriteLine("PASS: real exe cold --autostart stays hidden in the tray and launches nothing.");

            // 2. Second --autostart while running: exits 0 quietly; nothing forwarded, no window brought up.
            var second = await RunSecondInstanceAsync(guardianExecutable, dataA, "--autostart");
            Require(second.ExitCode == 0 && second.Boxes.Count == 0, $"A second --autostart must exit 0 without message boxes (got exit {second.ExitCode}, {second.Boxes.Count} boxes).");
            await Task.Delay(1500);
            Require(!HasVisibleTopLevelWindow(guardianA.ProcessId), "A second --autostart must not bring up the running window.");
            Require(!ReadLog(dataA).Any(line => line.Module == CommandPipeServer.LogModule && (line.Message.Contains("Forwarded", StringComparison.OrdinalIgnoreCase) || line.Message.Contains("client", StringComparison.OrdinalIgnoreCase))),
                "A second --autostart must not forward a request.");
            Require(CountLog(dataA, line => line.Message == "Process Guardian started") == 1 && !entryReady.WaitOne(0), "The second --autostart must not start anything.");
            Require(HashFile(Path.Combine(dataA, "config.json")) == configHash, "--autostart must not rewrite config.json.");
            Console.WriteLine("PASS: real exe second --autostart while running exits 0 quietly (no box, no forward, no window).");
            TerminateGuardian(guardianA);
            guardianA = null;

            // 3. Cold --autostart with a broken config.json: the window is shown so the error is visible.
            guardianB = LaunchGuardian(launcher, guardianExecutable, dataB, "--autostart");
            await WaitForLogAsync(dataB, line => line.Message == "Startup --autostart: the configuration could not be loaded; showing the main window", "--autostart config failure");
            Require(await WaitUntilAsync(() => HasVisibleTopLevelWindow(guardianB.ProcessId), TimeSpan.FromSeconds(10)), "--autostart with a broken config must show the window.");
            Console.WriteLine("PASS: real exe cold --autostart with a broken config.json shows the main window.");
        }
        finally
        {
            if (guardianA is not null) TerminateGuardian(guardianA);
            if (guardianB is not null) TerminateGuardian(guardianB);
            DeleteDirectory(root);
        }
    }

    private static void TerminateGuardian(ProcessLaunchResult launch)
    {
        using var process = Process.GetProcessById((int)launch.ProcessId);
        launch.JobObject.Terminate();
        process.WaitForExit(15000);
        launch.Dispose();
    }

    private sealed class ThrowingStore : IAutoStartStore
    {
        public string? ReadValue { get; init; }

        public object? ReadRunValue(string valueName) => ReadValue ?? throw new SecurityException("Test: read denied.");

        public void WriteRunValue(string valueName, string command) => throw new IOException("Test: write failed.");

        public void DeleteRunValue(string valueName) => throw new UnauthorizedAccessException("Test: delete denied.");

        public byte[]? ReadStartupApprovedValue(string valueName) => throw new UnauthorizedAccessException("Test: StartupApproved denied.");
    }

    private static string ReadRealRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoStartService.RunSubKeyPath, writable: false);
        if (key?.GetValue(AutoStartService.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value)
        {
            return "absent";
        }

        return $"present ({key.GetValueKind(AutoStartService.ValueName)}): {value}";
    }

    private static (object Value, RegistryValueKind Kind)? ReadValue(RegistryKey root, string subKey, string name)
    {
        using var key = root.OpenSubKey(subKey);
        return key?.GetValue(name) is { } value ? (value, key.GetValueKind(name)) : null;
    }

    private static void WriteRun(RegistryKey root, string command)
    {
        using var run = root.CreateSubKey("Run", writable: true);
        run.SetValue("ProcessGuardian", command, RegistryValueKind.String);
    }

    private static bool Dispose(RegistryKey key)
    {
        key.Dispose();
        return true;
    }

    private static void WriteConfiguration(string dataDirectory, params ApplicationConfiguration[] entries)
    {
        var json = JsonSerializer.Serialize(entries.ToList(), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dataDirectory, "config.json"), json, new UTF8Encoding(false));
    }

    private static ProcessLaunchResult LaunchGuardian(ProcessLauncher launcher, string guardianExecutable, string dataDirectory, string? arguments)
    {
        // ProcessLauncher passes this process's environment on, so the variable is set only around the launch.
        var previous = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(DataDirectoryVariable, dataDirectory);
            return launcher.Launch(guardianExecutable, arguments, Path.GetDirectoryName(guardianExecutable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DataDirectoryVariable, previous);
        }
    }

    private sealed record SecondInstanceResult(int ExitCode, List<string> Boxes);

    /// <summary>Starts a second Process Guardian (this test's own process), records/dismisses only its own dialogs.</summary>
    private static async Task<SecondInstanceResult> RunSecondInstanceAsync(string guardianExecutable, string dataDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(guardianExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(guardianExecutable)!,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment[DataDirectoryVariable] = dataDirectory;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The second instance did not start.");
        var boxes = new List<string>();
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (!process.HasExited)
        {
            if (DateTime.UtcNow > deadline)
            {
                var path = process.MainModule?.FileName;
                Require(string.Equals(path is null ? null : Path.GetFullPath(path), guardianExecutable, StringComparison.OrdinalIgnoreCase),
                    $"Refusing to kill PID {process.Id}: it is not the build under test ({path}).");
                process.Kill();
                process.WaitForExit(10000);
                throw new InvalidOperationException("The second --autostart instance did not exit within 40 s.");
            }

            foreach (var dialog in FindDialogs((uint)process.Id))
            {
                boxes.Add(GetText(dialog));
                SendMessageTimeout(dialog, WmClose, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, 2000, out _);
            }

            await Task.Delay(100);
        }

        process.WaitForExit();
        return new SecondInstanceResult(process.ExitCode, boxes);
    }

    private sealed record LogLine(string Level, string Module, string Message);

    private static List<LogLine> ReadLog(string dataDirectory)
    {
        var lines = new List<LogLine>();
        var logDirectory = Path.Combine(dataDirectory, "Logs");
        if (!Directory.Exists(logDirectory))
        {
            return lines;
        }

        foreach (var path in Directory.EnumerateFiles(logDirectory, "processguardian-*.log"))
        {
            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                text = reader.ReadToEnd();
            }

            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    lines.Add(new LogLine(Property(document.RootElement, "Level"), Property(document.RootElement, "Module"), Property(document.RootElement, "Message")));
                }
                catch (JsonException)
                {
                    // A line still being written is incomplete; it is read again on the next poll.
                }
            }
        }

        return lines;
    }

    private static string Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.Number
                    ? ((LogLevel)property.Value.GetInt32()).ToString()
                    : property.Value.ToString();
            }
        }

        return string.Empty;
    }

    private static int CountLog(string dataDirectory, Func<LogLine, bool> predicate) => ReadLog(dataDirectory).Count(predicate);

    private static async Task WaitForLogAsync(string dataDirectory, Func<LogLine, bool> predicate, string description)
    {
        if (!await WaitUntilAsync(() => ReadLog(dataDirectory).Any(predicate), TimeSpan.FromSeconds(20)))
        {
            var recent = string.Join(Environment.NewLine, ReadLog(dataDirectory).TakeLast(15).Select(line => $"  [{line.Level}] {line.Module}: {line.Message}"));
            throw new InvalidOperationException($"Log entry not found ({description}) in {dataDirectory}. Recent entries:{Environment.NewLine}{recent}");
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(100);
        }

        return true;
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void DeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 20 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 19)
                {
                    throw;
                }

                Thread.Sleep(250);
            }
        }
    }

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

    private const uint WmClose = 0x0010;
    private const uint WmGetText = 0x000D;
    private const uint SmtoAbortIfHung = 0x0002;

    private static bool HasVisibleTopLevelWindow(uint processId)
    {
        var found = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == processId && IsWindowVisible(window))
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static List<IntPtr> FindDialogs(uint processId)
    {
        var dialogs = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == processId && IsWindowVisible(window))
            {
                var className = new StringBuilder(64);
                GetClassName(window, className, className.Capacity);
                if (className.ToString() == "#32770")
                {
                    dialogs.Add(window);
                }
            }

            return true;
        }, IntPtr.Zero);
        return dialogs;
    }

    private static string GetText(IntPtr window)
    {
        var buffer = new StringBuilder(1024);
        SendMessageTimeoutText(window, WmGetText, (IntPtr)buffer.Capacity, buffer, SmtoAbortIfHung, 2000, out _);
        return buffer.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutText(IntPtr window, uint message, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
