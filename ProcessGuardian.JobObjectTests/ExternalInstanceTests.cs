using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using ProcessGuardian.Models;
using ProcessGuardian.Services.CommandLine;
using ProcessGuardian.Services.Configuration;
using ProcessGuardian.Services.ExternalInstances;
using ProcessGuardian.Services.ManagedApplications;
using ProcessGuardian.Services.ProcessManagement;
using ProcessGuardian.ViewModels;

namespace ProcessGuardian.JobObjectTests;

/// <summary>
/// Checks for the pre-launch "already running outside Process Guardian" refusal (HANDOVER 7.17). Every process
/// these tests start is this test executable (--child or --window-child) or a Process Guardian build under test
/// inside a test-owned Job; only those processes are ever killed. Data directories are isolated under %TEMP% or
/// .test-artifacts; the real configuration and real applications are never touched.
/// </summary>
internal static class ExternalInstanceTests
{
    private const string DataDirectoryVariable = "PROCESSGUARDIAN_DATA_DIR";
    private const uint BmClick = 0x00F5;
    private const uint WmGetText = 0x000D;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly TimeSpan ForwarderTimeout = TimeSpan.FromSeconds(40);

    public static async Task RunAsync(string guardianExecutable, string testExecutable)
    {
        guardianExecutable = Path.GetFullPath(guardianExecutable);
        testExecutable = Path.GetFullPath(testExecutable);
        TestInjectedSnapshot();
        TestRealProcesses(testExecutable);
        TestMessagesAndProtocol();
        RunOnStaDispatcher(() => TestViewModelAsync(testExecutable), TimeSpan.FromSeconds(120), "External-instance view model test");
        await TestCommandLineAsync(guardianExecutable, testExecutable);
    }

    // ---- Detector with injected process snapshots -------------------------------------------------------------

    private static void TestInjectedSnapshot()
    {
        var queried = new List<uint>();
        ProcessSnapshotEntry[] snapshot =
        [
            new(0, 1, "app"),
            new(100, 1, "app"),
            new(101, 1, "APP"),
            new(102, 2, "app"),
            new(103, 1, "other"),
            new(104, 1, "app"),
            new(105, 1, "app.exe"),
            new(106, 1, ""),
            new(107, 1, "app"),
        ];
        string? Query(uint processId)
        {
            queried.Add(processId);
            return processId switch
            {
                100 => throw new Win32Exception(5),
                101 => @"c:\APPS2\..\apps\sub\..\App.EXE",
                104 => null,
                105 => @"\\?\C:\Apps\app.exe",
                106 => @"C:\Apps\app.exe",
                107 => @"D:\Other\app.exe",
                _ => throw new InvalidOperationException($"PID {processId} must have been filtered before the path query."),
            };
        }

        var detector = new ExternalInstanceDetector(() => snapshot, Query, sessionId: 1, alwaysExcludedProcessIds: [999]);
        var result = detector.Find(@"C:\Apps\app.exe", [105]);
        Require(result.Error is null, $"Unexpected detector error: {result.Error}");
        Require(result.ProcessIds.SequenceEqual(new uint[] { 101, 106 }),
            $"Expected PIDs 101,106 (case-insensitive, normalized, empty name queried), got [{string.Join(",", result.ProcessIds)}].");
        Require(result.UnreadableCandidateCount == 2, $"Expected 2 unreadable candidates (throwing and null queries), got {result.UnreadableCandidateCount}.");
        Require(queried.OrderBy(id => id).SequenceEqual(new uint[] { 100, 101, 104, 106, 107 }),
            $"PID 0, other sessions, other names and excluded PIDs must not be queried; queried [{string.Join(",", queried)}].");
        Console.WriteLine("PASS: external-instance detector matches case-insensitively after normalization and filters session, name and excluded PIDs before querying.");

        queried.Clear();
        var prefixed = new ExternalInstanceDetector(() => snapshot, Query, sessionId: 1, alwaysExcludedProcessIds: [101, 106]);
        var prefixedResult = prefixed.Find("  \"C:\\APPS\\.\\app.exe\"  ", []);
        Require(prefixedResult.ProcessIds.SequenceEqual(new uint[] { 105 }),
            $"A \\\\?\\ image path and a quoted/relative-segment target must match; got [{string.Join(",", prefixedResult.ProcessIds)}].");
        Require(!queried.Contains(101u) && !queried.Contains(106u), "Always-excluded PIDs must not be queried.");

        var otherDirectory = detector.Find(@"C:\Elsewhere\app.exe", []);
        Require(!otherDirectory.Found && otherDirectory.Error is null, "The same file name in another directory must not match.");
        Require(!detector.Find("   ", []).Found, "An empty path must not match.");
        Console.WriteLine("PASS: external-instance detector handles \\\\?\\ prefixes, quotes and relative segments, and a different path gives no match.");

        var failingQuery = new ExternalInstanceDetector(() => snapshot, _ => throw new UnauthorizedAccessException("injected"), sessionId: 1);
        var failingResult = failingQuery.Find(@"C:\Apps\app.exe", []);
        Require(!failingResult.Found && failingResult.Error is null && failingResult.UnreadableCandidateCount == 6,
            $"Injected path-query failures must count as not running (found={failingResult.Found}, unreadable={failingResult.UnreadableCandidateCount}, error={failingResult.Error}).");
        var failingEnumeration = new ExternalInstanceDetector(() => throw new InvalidOperationException("injected enumeration failure"), _ => null, sessionId: 1);
        var enumerationResult = failingEnumeration.Find(@"C:\Apps\app.exe", []);
        Require(!enumerationResult.Found && enumerationResult.Error is InvalidOperationException,
            "An unexpected enumeration failure must be reported as Error with no match (the launch is then allowed).");
        Console.WriteLine("PASS: injected path-query and enumeration failures are treated as not running without throwing.");
    }

    // ---- Detector against real processes this test starts -----------------------------------------------------

    private static void TestRealProcesses(string testExecutable)
    {
        var defaults = ExternalInstanceDetector.GetDefaultExcludedProcessIds();
        Require(defaults.Contains((uint)Environment.ProcessId), "The detector must always exclude its own process.");
        using var child = StartUnrelatedChild(testExecutable);
        try
        {
            var childId = (uint)child.Id;
            var detector = new ExternalInstanceDetector();
            Require(WaitUntil(() => detector.Find(testExecutable, []).ProcessIds.Contains(childId), TimeSpan.FromSeconds(10)),
                "The detector did not find a test child started outside Process Guardian with the same path.");
            var found = detector.Find(testExecutable, []);
            Require(!found.ProcessIds.Contains((uint)Environment.ProcessId), "The detector reported its own process.");
            Require(detector.Find(testExecutable.ToUpperInvariant(), []).ProcessIds.Contains(childId), "An upper-case target path must still match.");
            var directory = Path.GetDirectoryName(testExecutable)!;
            var variant = Path.Combine(directory, ".", "..", Path.GetFileName(directory), Path.GetFileName(testExecutable)).Replace('\\', '/');
            Require(detector.Find(variant, []).ProcessIds.Contains(childId), $"A non-normalized target path ({variant}) must still match.");
            var elsewhere = Path.Combine(Path.GetTempPath(), "PG-ExternalInstance-" + Guid.NewGuid().ToString("N"), Path.GetFileName(testExecutable));
            Require(!detector.Find(elsewhere, []).Found, "The same file name in another directory must not match a real process.");
            Require(!detector.Find(testExecutable, [childId]).ProcessIds.Contains(childId), "An excluded (managed) PID must not be reported.");
            Console.WriteLine("PASS: the real detector finds a same-path test child, ignores case and path form, excludes itself, managed PIDs and other directories.");
        }
        finally
        {
            StopOwnChild(child);
        }
    }

    // ---- Message text, reply code and exit code ---------------------------------------------------------------

    private static void TestMessagesAndProtocol()
    {
        var text = ExternalInstanceMessages.Format("Demo App", [12, 34]);
        Require(text.StartsWith(ExternalInstanceMessages.Headline, StringComparison.Ordinal) && text.Contains("Demo App", StringComparison.Ordinal)
            && text.Contains("12, 34", StringComparison.Ordinal), $"Unexpected message text: {text}");
        var many = ExternalInstanceMessages.Format("Demo App", Enumerable.Range(1, 12).Select(id => (uint)id).ToArray());
        Require(many.Contains("10", StringComparison.Ordinal) && !many.Contains(", 11", StringComparison.Ordinal) && many.Contains("12", StringComparison.Ordinal),
            $"At most ten PIDs must be listed, followed by the total: {many}");
        Require(CommandLineExitCodes.ForReply(CommandReplyCode.ExternalInstanceRunning) == CommandLineExitCodes.Refused
            && CommandLineExitCodes.Refused == 1, "ExternalInstanceRunning must map to exit code 1.");
        var reply = CommandReply.Create(CommandReplyCode.ExternalInstanceRunning, text);
        Require(!reply.Ok, "An ExternalInstanceRunning reply must not be Ok.");
        Require(CommandProtocol.TryParseReply(CommandProtocol.SerializeReply(reply).AsSpan().TrimEnd((byte)'\n'), out var parsed, out var error)
            && parsed is not null && parsed.Code == CommandReplyCode.ExternalInstanceRunning && parsed.Message == text && !parsed.Ok,
            $"The ExternalInstanceRunning reply did not survive the pipe protocol round trip ({error}).");
        string? shownError = null;
        var exitCode = ForwardResultPresenter.Present(
            new ForwardResult(ForwardStatus.Replied, parsed, null),
            (_, _) => throw new InvalidOperationException("ExternalInstanceRunning must not show an information box."),
            (message, _) => shownError = message,
            _ => throw new InvalidOperationException("ExternalInstanceRunning must not bring a window forward."));
        Require(exitCode == 1 && shownError == text, $"The forwarder must show the refusal as an error and exit 1 (exit {exitCode}).");
        Console.WriteLine("PASS: ExternalInstanceRunning text, pipe round trip, forwarder error box and exit code 1.");
    }

    // ---- View model: Launch button and RequestLaunchAsync -----------------------------------------------------

    private static async Task TestViewModelAsync(string testExecutable)
    {
        var testDirectory = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", $"external-instance-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var tag = Guid.NewGuid().ToString("N")[..12];
        var managedEvent = $"PGEI_Managed_{tag}";
        var refusedEvent = $"PGEI_Refused_{tag}";
        using var managedReady = new EventWaitHandle(false, EventResetMode.ManualReset, managedEvent);
        using var refusedReady = new EventWaitHandle(false, EventResetMode.ManualReset, refusedEvent);
        var configPath = Path.Combine(testDirectory, "config.json");
        await new ConfigurationService(configPath).SaveAsync([
            Entry("EI Managed", testExecutable, managedEvent, testDirectory),
            Entry("EI Refused", testExecutable, refusedEvent, testDirectory),
        ]);

        // 1. Fake detector reports an instance: neither the Launch button nor RequestLaunchAsync starts anything.
        var fake = new FakeDetector([4242]);
        var warnings = new List<string>();
        var service = new ManagedApplicationService(synchronizationContext: SynchronizationContext.Current);
        var viewModel = new MainViewModel(service, new FixedExecutableFilePicker(), new ConfigurationService(configPath),
            confirmStop: _ => true, confirmRemove: _ => true, externalInstanceDetector: fake, showExternalInstanceWarning: warnings.Add);
        try
        {
            await viewModel.InitializationTask;
            var item = viewModel.ApplicationItems.Single(candidate => candidate.SoftwareName == "EI Refused");
            var statusBefore = item.StatusText;
            Require(item.CanLaunch && item.LaunchCommand.CanExecute(null), "The refused entry must be launchable before the check.");
            await item.LaunchCommand.ExecuteAsync();
            Require(warnings.Count == 1 && warnings[0].StartsWith(ExternalInstanceMessages.Headline, StringComparison.Ordinal)
                && warnings[0].Contains("4242", StringComparison.Ordinal) && warnings[0].Contains("EI Refused", StringComparison.Ordinal),
                $"The Launch button must show exactly one external-instance warning (got {warnings.Count}).");
            Require(fake.Calls == 1 && string.Equals(fake.LastPath, item.ExecutablePath, StringComparison.Ordinal), "The detector must be asked once with the entry path.");
            Require(item.ManagedApplication is null && item.StatusText == statusBefore && !item.CanStop && !item.HasError,
                $"A refused launch must leave the displayed state unchanged (status '{item.StatusText}', before '{statusBefore}').");
            Require(item.RecentOperationResult.StartsWith("Launch refused", StringComparison.Ordinal), $"Unexpected operation result: {item.RecentOperationResult}");
            Require(item.CanLaunch, "The entry must remain launchable after a refusal.");

            var request = await viewModel.RequestLaunchAsync("EI Refused");
            Require(request.Code == CommandReplyCode.ExternalInstanceRunning && request.Message.StartsWith(ExternalInstanceMessages.Headline, StringComparison.Ordinal)
                && request.Message.Contains("4242", StringComparison.Ordinal) && request.LaunchTask is null,
                $"RequestLaunchAsync must return ExternalInstanceRunning without a launch task (got {request.Code}).");
            Require(!request.ToReply().Ok && CommandLineExitCodes.ForReply(request.ToReply().Code) == 1, "The --launch reply must be a refusal with exit code 1.");
            Require(warnings.Count == 1, "The --launch path must not show the view model's warning box (the caller presents it).");
            Require(fake.Calls == 2, $"A refused --launch must query the detector exactly once (calls {fake.Calls}).");
            Require(!refusedReady.WaitOne(1500) && item.ManagedApplication is null, "A refused launch started the application.");

            // Without an instance, --launch checks once (not again inside the Launch command) and starts the entry.
            fake.ProcessIds = [];
            var started = await viewModel.RequestLaunchAsync("EI Refused");
            Require(started.Code == CommandReplyCode.Started && started.LaunchTask is not null, $"Expected Started, got {started.Code}: {started.Message}");
            await started.LaunchTask!;
            Require(fake.Calls == 3, $"An accepted --launch must check exactly once (calls {fake.Calls}).");
            Require(refusedReady.WaitOne(15000) && item.ManagedApplication is not null, "The accepted --launch did not start the entry.");
            await item.StopCommand.ExecuteAsync();
            Require(await WaitUntilAsync(() => item.ManagedApplication is null || item.ManagedApplication.Status is ManagedApplicationStatus.Completed or ManagedApplicationStatus.Failed, TimeSpan.FromSeconds(15)),
                "The accepted entry did not stop.");
            refusedReady.Reset();
            Console.WriteLine("PASS: Launch with an external instance shows the warning, starts nothing and keeps the state; --launch returns ExternalInstanceRunning (exit 1) and checks once.");
        }
        finally
        {
            await viewModel.DisposeAsync();
            service.Dispose();
        }

        // 2. Real detector: Process Guardian's own instance stays AlreadyRunning; an outside instance is refused.
        var realWarnings = new List<string>();
        var realService = new ManagedApplicationService(synchronizationContext: SynchronizationContext.Current);
        var realViewModel = new MainViewModel(realService, new FixedExecutableFilePicker(), new ConfigurationService(configPath),
            confirmStop: _ => true, confirmRemove: _ => true, externalInstanceDetector: new ExternalInstanceDetector(), showExternalInstanceWarning: realWarnings.Add);
        Process? outside = null;
        try
        {
            await realViewModel.InitializationTask;
            var managed = realViewModel.ApplicationItems.Single(candidate => candidate.SoftwareName == "EI Managed");
            var refused = realViewModel.ApplicationItems.Single(candidate => candidate.SoftwareName == "EI Refused");
            var first = await realViewModel.RequestLaunchAsync("EI Managed");
            Require(first.Code == CommandReplyCode.Started, $"The first managed launch must start (got {first.Code}: {first.Message}).");
            await first.LaunchTask!;
            Require(managedReady.WaitOne(15000), "The managed test child did not start.");
            var managedPid = managed.ManagedApplication?.ProcessId ?? throw new InvalidOperationException("The managed item has no process.");
            var managedIds = realViewModel.GetManagedProcessIds();
            Require(managedIds.Contains(managedPid), "GetManagedProcessIds must include the managed primary process.");
            Require(!new ExternalInstanceDetector().Find(testExecutable, managedIds).ProcessIds.Contains(managedPid), "A PG-managed PID must not be reported as external.");

            var again = await realViewModel.RequestLaunchAsync("EI Managed");
            Require(again.Code == CommandReplyCode.AlreadyRunning, $"Launching a PG-managed running entry must stay AlreadyRunning (got {again.Code}).");

            outside = StartUnrelatedChild(testExecutable);
            var outsideId = (uint)outside.Id;
            var blocked = await realViewModel.RequestLaunchAsync("EI Refused");
            Require(blocked.Code == CommandReplyCode.ExternalInstanceRunning && blocked.Message.Contains(outsideId.ToString(), StringComparison.Ordinal)
                && !blocked.Message.Contains($" {managedPid},", StringComparison.Ordinal) && !blocked.Message.EndsWith($" {managedPid}", StringComparison.Ordinal),
                $"An outside instance must be refused and only outside PIDs listed (got {blocked.Code}: {blocked.Message}).");
            await refused.LaunchCommand.ExecuteAsync();
            Require(realWarnings.Count == 1 && realWarnings[0].Contains(outsideId.ToString(), StringComparison.Ordinal), "The Launch button must warn about the outside instance.");
            Require(!refusedReady.WaitOne(1500) && refused.ManagedApplication is null, "An outside instance must block the launch.");
            Require(managed.ManagedApplication is { } still && still.ProcessId == managedPid && managed.CanStop, "The managed entry must be unaffected.");

            StopOwnChild(outside);
            outside.Dispose();
            outside = null;
            var allowed = await realViewModel.RequestLaunchAsync("EI Refused");
            Require(allowed.Code == CommandReplyCode.Started, $"After the outside instance ended, launching must be allowed (got {allowed.Code}: {allowed.Message}).");
            await allowed.LaunchTask!;
            Require(refusedReady.WaitOne(15000), "The second entry did not start after the outside instance ended.");
            await managed.StopCommand.ExecuteAsync();
            await refused.StopCommand.ExecuteAsync();
            Require(await WaitUntilAsync(() => managed.ManagedApplication?.Status is null or ManagedApplicationStatus.Completed or ManagedApplicationStatus.Failed
                && refused.ManagedApplication?.Status is null or ManagedApplicationStatus.Completed or ManagedApplicationStatus.Failed, TimeSpan.FromSeconds(15)),
                "The managed test entries did not stop.");
            Console.WriteLine("PASS: with the real detector PG-managed instances stay AlreadyRunning, an outside same-path instance is refused, and launching works again once it ends.");
        }
        finally
        {
            if (outside is not null)
            {
                StopOwnChild(outside);
                outside.Dispose();
            }

            await realViewModel.DisposeAsync();
            realService.Dispose();
        }

        TryDeleteDirectory(testDirectory);
    }

    // ---- Real ProcessGuardian.exe: startup and forwarded --launch ---------------------------------------------

    private static async Task TestCommandLineAsync(string guardianExecutable, string testExecutable)
    {
        var root = Path.Combine(Path.GetTempPath(), "PG-ExternalInstanceTests-" + Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDirectory);
        var targetEvent = $"PGEI_Cli_{Guid.NewGuid():N}"[..28];
        var entry = Entry("EI Cli", testExecutable, targetEvent, null);
        File.WriteAllText(Path.Combine(dataDirectory, "config.json"),
            JsonSerializer.Serialize(new List<ApplicationConfiguration> { entry }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        using var targetReady = new EventWaitHandle(false, EventResetMode.ManualReset, targetEvent);
        var launcher = new ProcessLauncher();
        ProcessLaunchResult? guardian = null;
        Process? guardianProcess = null;
        var outside = StartUnrelatedChild(testExecutable);
        try
        {
            var outsideId = outside.Id.ToString();
            // Cold start with --launch while an outside instance exists: refused with an error box owned by the PG under test.
            guardian = LaunchGuardian(launcher, guardianExecutable, dataDirectory, $"--launch \"{entry.SoftwareName}\"");
            guardianProcess = Process.GetProcessById((int)guardian.ProcessId);
            var startupBoxes = await CollectDialogsAsync(guardian.ProcessId, 1, TimeSpan.FromSeconds(30));
            Require(startupBoxes.Count == 1 && startupBoxes[0].Contains(ExternalInstanceMessages.Headline, StringComparison.Ordinal)
                && startupBoxes[0].Contains(outsideId, StringComparison.Ordinal),
                $"A cold --launch with an outside instance must show one refusal box [{string.Join(" || ", startupBoxes)}].");
            await WaitForLogAsync(dataDirectory, line => line.Level == "Warning"
                && line.Message.StartsWith($"Startup --launch selector \"{entry.SoftwareName}\": ExternalInstanceRunning", StringComparison.Ordinal), "startup refusal");
            await WaitForLogAsync(dataDirectory, line => line.Level == "Warning" && line.Module == ExternalInstanceDetector.LogModule
                || line.Level == "Warning" && line.Message.StartsWith("Launch refused:", StringComparison.Ordinal), "launch refused warning");
            Require(!targetReady.WaitOne(1000) && FindChildren(targetEvent).Length == 0, "A refused cold --launch started the entry.");
            Require(!guardianProcess.HasExited, "Process Guardian must keep running after a refused startup --launch.");
            Console.WriteLine("PASS: a cold start with --launch was refused with ExternalInstanceRunning, one error box, a Warning log, and nothing started.");

            // Forwarded --launch while the outside instance still exists: the forwarder exits 1 with one error box.
            var forwarded = await RunForwarderAsync(guardianExecutable, dataDirectory, "--launch", entry.SoftwareName);
            Require(forwarded.ExitCode == 1 && forwarded.Boxes.Count == 1 && forwarded.Boxes[0].Contains(ExternalInstanceMessages.Headline, StringComparison.Ordinal),
                $"A forwarded --launch must exit 1 with one refusal box: {forwarded}");
            await WaitForLogAsync(dataDirectory, line => line.Module == "CommandLine" && line.Level == "Warning"
                && line.Message.Contains(": ExternalInstanceRunning", StringComparison.Ordinal) && line.Message.StartsWith("Forwarded", StringComparison.Ordinal), "forwarded refusal");
            await Task.Delay(1000);
            Require(FindDialogs(guardian.ProcessId).Count == 0, "The primary instance must not show its own box for a forwarded refusal.");
            Require(!targetReady.WaitOne(0) && FindChildren(targetEvent).Length == 0, "A refused forwarded --launch started the entry.");
            Console.WriteLine("PASS: a forwarded --launch was refused with ExternalInstanceRunning, exit code 1 and one error box; nothing started.");

            // Once the outside instance is gone the same forwarded --launch starts the entry inside PG's Job.
            StopOwnChild(outside);
            var accepted = await RunForwarderAsync(guardianExecutable, dataDirectory, "--launch", entry.SoftwareName);
            Require(accepted.ExitCode == 0 && accepted.Boxes.Count == 0, $"After the outside instance ended, --launch must succeed silently: {accepted}");
            Require(targetReady.WaitOne(TimeSpan.FromSeconds(30)), "The accepted --launch did not start the entry.");
            var children = FindChildren(targetEvent);
            Require(children.Length == 1 && guardian.JobObject.ContainsProcess(children[0]), "The accepted entry is not inside the Process Guardian under test.");
            foreach (var child in children)
            {
                child.Dispose();
            }

            Console.WriteLine("PASS: after the outside instance ended, the forwarded --launch started the entry inside Process Guardian's Job (exit 0).");
        }
        finally
        {
            StopOwnChild(outside);
            outside.Dispose();
            if (guardian is not null)
            {
                if (guardianProcess is not null && !guardianProcess.HasExited)
                {
                    Require(guardianProcess.Id == (int)guardian.ProcessId && guardian.JobObject.ContainsProcess(guardianProcess), "Refusing to kill a Process Guardian this test did not start.");
                    guardianProcess.Kill();
                    guardianProcess.WaitForExit(15000);
                }

                guardian.JobObject.Terminate(0);
                guardian.Dispose();
            }

            guardianProcess?.Dispose();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (FindChildren(targetEvent) is { Length: > 0 } leftover && DateTime.UtcNow < deadline)
            {
                foreach (var child in leftover)
                {
                    child.Dispose();
                }

                Thread.Sleep(200);
            }

            TryDeleteDirectory(root);
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private sealed class FakeDetector(uint[] processIds) : IExternalInstanceDetector
    {
        public uint[] ProcessIds { get; set; } = processIds;

        public int Calls { get; private set; }

        public string? LastPath { get; private set; }

        public ExternalInstanceCheckResult Find(string executablePath, IReadOnlyCollection<uint> excludedProcessIds)
        {
            Calls++;
            LastPath = executablePath;
            return ProcessIds.Length == 0 ? ExternalInstanceCheckResult.None : new ExternalInstanceCheckResult(ProcessIds, 0, null);
        }
    }

    private static ApplicationConfiguration Entry(string name, string testExecutable, string eventName, string? workingDirectory) => new()
    {
        Id = Guid.NewGuid(),
        SoftwareName = name,
        ExecutablePath = testExecutable,
        Arguments = "--window-child " + eventName,
        WorkingDirectory = workingDirectory,
        AddedAt = DateTimeOffset.UtcNow,
    };

    private static Process StartUnrelatedChild(string testExecutable)
    {
        var startInfo = new ProcessStartInfo(testExecutable) { UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("--child");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the dedicated test child.");
    }

    private static void StopOwnChild(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(10000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    private static ProcessLaunchResult LaunchGuardian(ProcessLauncher launcher, string guardianExecutable, string dataDirectory, string? arguments)
    {
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

    private sealed record ForwarderResult(int ExitCode, List<string> Boxes)
    {
        public override string ToString() => $"exit {ExitCode}, boxes [{string.Join(" || ", Boxes.Select(box => box.Replace('\n', ' ')))}]";
    }

    private static async Task<ForwarderResult> RunForwarderAsync(string guardianExecutable, string dataDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(guardianExecutable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(guardianExecutable)! };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment[DataDirectoryVariable] = dataDirectory;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The forwarder did not start.");
        var boxes = new List<string>();
        var dismissed = new HashSet<IntPtr>();
        var deadline = DateTime.UtcNow + ForwarderTimeout;
        while (!process.HasExited)
        {
            if (DateTime.UtcNow > deadline)
            {
                var path = process.MainModule?.FileName;
                if (string.Equals(path is null ? null : Path.GetFullPath(path), guardianExecutable, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill();
                    process.WaitForExit(10000);
                }

                throw new InvalidOperationException($"A forwarder ({string.Join(" ", arguments)}) did not exit within {ForwarderTimeout.TotalSeconds:0} s.");
            }

            foreach (var dialog in FindDialogs((uint)process.Id))
            {
                if (dismissed.Add(dialog))
                {
                    boxes.Add(ReadDialogText(dialog));
                }

                DismissDialog(dialog);
            }

            await Task.Delay(100);
        }

        process.WaitForExit();
        return new ForwarderResult(process.ExitCode, boxes);
    }

    /// <summary>Collects and dismisses message boxes owned by a Process Guardian this test started.</summary>
    private static async Task<List<string>> CollectDialogsAsync(uint processId, int expected, TimeSpan timeout)
    {
        var boxes = new List<string>();
        var dismissed = new HashSet<IntPtr>();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && boxes.Count < expected)
        {
            foreach (var dialog in FindDialogs(processId))
            {
                if (dismissed.Add(dialog))
                {
                    boxes.Add(ReadDialogText(dialog));
                }

                DismissDialog(dialog);
            }

            await Task.Delay(100);
        }

        await Task.Delay(1000);
        foreach (var dialog in FindDialogs(processId))
        {
            if (dismissed.Add(dialog))
            {
                boxes.Add(ReadDialogText(dialog));
            }

            DismissDialog(dialog);
        }

        return boxes;
    }

    private static Process[] FindChildren(string eventName)
    {
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
        var result = new List<Process>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            string? commandLine = null;
            try
            {
                if (!process.HasExited)
                {
                    commandLine = TestProcessNativeMethods.GetProcessCommandLine(process.Handle);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                commandLine = null;
            }

            if (commandLine is not null && commandLine.Contains(eventName, StringComparison.Ordinal))
            {
                result.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return result.ToArray();
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
                    // A line still being written is read again on the next poll.
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
                    ? ((ProcessGuardian.Services.Logging.LogLevel)property.Value.GetInt32()).ToString()
                    : property.Value.ToString();
            }
        }

        return string.Empty;
    }

    private static async Task WaitForLogAsync(string dataDirectory, Func<LogLine, bool> predicate, string description)
    {
        if (!await WaitUntilAsync(() => ReadLog(dataDirectory).Any(predicate), TimeSpan.FromSeconds(15)))
        {
            var recent = string.Join(Environment.NewLine, ReadLog(dataDirectory).TakeLast(15).Select(line => $"  [{line.Level}] {line.Module}: {line.Message}"));
            throw new InvalidOperationException($"Log entry not found ({description}) in {dataDirectory}. Recent entries:{Environment.NewLine}{recent}");
        }
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            Thread.Sleep(100);
        }

        return true;
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

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 20 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
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

    private static List<IntPtr> FindDialogs(uint processId)
    {
        var dialogs = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == processId && IsWindowVisible(window) && GetClassNameText(window) == "#32770")
            {
                dialogs.Add(window);
            }

            return true;
        }, IntPtr.Zero);
        return dialogs;
    }

    private static string ReadDialogText(IntPtr dialog)
    {
        var builder = new StringBuilder();
        EnumChildWindows(dialog, (child, _) =>
        {
            if (GetClassNameText(child) == "Static")
            {
                var text = GetText(child);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.Append(text).Append('\n');
                }
            }

            return true;
        }, IntPtr.Zero);
        return builder.ToString().Trim();
    }

    private static void DismissDialog(IntPtr dialog)
    {
        var button = GetDlgItem(dialog, 2);
        if (button == IntPtr.Zero)
        {
            button = GetDlgItem(dialog, 1);
        }

        if (button != IntPtr.Zero)
        {
            SendMessageTimeout(button, BmClick, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, 2000, out _);
        }
    }

    private static string GetClassNameText(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static string GetText(IntPtr window)
    {
        var buffer = new StringBuilder(4096);
        SendMessageTimeoutText(window, WmGetText, (IntPtr)buffer.Capacity, buffer, SmtoAbortIfHung, 2000, out _);
        return buffer.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr dialog, int id);

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