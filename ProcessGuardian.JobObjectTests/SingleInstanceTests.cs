using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProcessGuardian.Models;
using ProcessGuardian.Services.CommandLine;
using ProcessGuardian.Services.ProcessManagement;

namespace ProcessGuardian.JobObjectTests;

/// <summary>
/// End-to-end checks of --launch and single-instance forwarding with the real ProcessGuardian.exe. Every scenario
/// uses its own data directory under %TEMP% (PROCESSGUARDIAN_DATA_DIR) with a prepared config.json whose entries
/// start this test executable in --window-child mode. Every Process Guardian started here runs in a test-owned Job
/// (KILL_ON_JOB_CLOSE); message boxes are dismissed only when they belong to a forwarder process started here.
/// </summary>
internal static class SingleInstanceTests
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
        var root = Path.Combine(Path.GetTempPath(), "PG-SingleInstanceTests-" + Guid.NewGuid().ToString("N"));
        var dataA = Path.Combine(root, "A");
        var dataB = Path.Combine(root, "B");
        Directory.CreateDirectory(dataA);
        Directory.CreateDirectory(dataB);

        var tag = Guid.NewGuid().ToString("N")[..12];
        var targetEvent = $"PGSI_Target_{tag}";
        var secondEvent = $"PGSI_Second_{tag}";
        var dupEvent = $"PGSI_Dup_{tag}";
        var abandonedEvent = $"PGSI_Abandoned_{tag}";
        var target = Entry("SI Target", testExecutable, targetEvent);
        var second = Entry("SI Second", testExecutable, secondEvent);
        var dup1 = Entry("SI Dup", testExecutable, dupEvent);
        var dup2 = Entry("si dup", testExecutable, dupEvent);
        WriteConfiguration(dataA, target, second, dup1, dup2);

        using var targetReady = new EventWaitHandle(false, EventResetMode.ManualReset, targetEvent);
        using var secondReady = new EventWaitHandle(false, EventResetMode.ManualReset, secondEvent);
        using var dupReady = new EventWaitHandle(false, EventResetMode.ManualReset, dupEvent);
        using var abandonedReady = new EventWaitHandle(false, EventResetMode.ManualReset, abandonedEvent);
        var launcher = new ProcessLauncher();
        ProcessLaunchResult? guardianA = null;
        ProcessLaunchResult? guardianB = null;
        ProcessLaunchResult? guardianA2 = null;
        Process? processA = null;
        try
        {
            // 1. Cold start with --launch: the entry starts inside Process Guardian's own Job; the window stays hidden.
            guardianA = LaunchGuardian(launcher, guardianExecutable, dataA, $"--launch \"{target.SoftwareName}\"");
            processA = Process.GetProcessById((int)guardianA.ProcessId);
            Require(targetReady.WaitOne(TimeSpan.FromSeconds(30)), "The --launch target did not start from a cold start.");
            var targetChildren = WaitForChildren(targetEvent, 1);
            Require(targetChildren.Length == 1, $"Expected exactly one target child, found {targetChildren.Length}.");
            Require(guardianA.JobObject.ContainsProcess(targetChildren[0]), "The target child is not a descendant of the Process Guardian under test.");
            await WaitForLogAsync(dataA, entry => entry.Message.StartsWith($"Startup --launch selector \"{target.SoftwareName}\": Started", StringComparison.Ordinal), "startup launch entry");
            await Task.Delay(1500);
            Require(!HasVisibleTopLevelWindow(guardianA.ProcessId), "A cold start with --launch must stay hidden in the tray (a visible Process Guardian window was found; is the notification area unavailable?).");
            Console.WriteLine("PASS: a cold start with --launch started the entry inside Process Guardian's Job and kept the window hidden.");

            // 2. Second instance with --launch forwards to the first one and exits 0; nothing is written by the forwarder.
            var configHash = HashFile(Path.Combine(dataA, "config.json"));
            var filesBefore = ListFiles(dataA);
            var startedBefore = CountLog(dataA, entry => entry.Message == "Process Guardian started");
            Require(startedBefore == 1, $"Expected one 'Process Guardian started' entry, found {startedBefore}.");
            var forwardLaunch = await RunForwarderAsync(guardianExecutable, dataA, "--launch=si second");
            Require(forwardLaunch.ExitCode == 0 && forwardLaunch.Boxes.Count == 0, $"Forwarded launch must exit 0 silently: {forwardLaunch}");
            Require(secondReady.WaitOne(TimeSpan.FromSeconds(30)), "The forwarded launch did not start the entry.");
            Require(WaitForChildren(secondEvent, 1).Length == 1, "Expected exactly one child for the forwarded entry.");
            Require(FindChildren(targetEvent).Length == 1, "The first entry must still have exactly one child.");
            Require(guardianA.JobObject.ContainsProcess(FindChildren(secondEvent)[0]), "The forwarded entry is not a descendant of the first instance.");
            await WaitForLogAsync(dataA, entry => entry.Module == "CommandLine" && entry.Level == "Information"
                && entry.Message.StartsWith("Forwarded 'launch' selector \"si second\": Started", StringComparison.Ordinal), "forwarded launch entry");
            Require(CountLog(dataA, entry => entry.Message == "Process Guardian started") == 1, "The forwarder must not start a second Process Guardian (logger).");
            Require(HashFile(Path.Combine(dataA, "config.json")) == configHash, "config.json changed during forwarding.");
            Require(ListFiles(dataA).SequenceEqual(filesBefore), "The set of files in the data directory changed during forwarding.");
            Require(!processA.HasExited, "The first instance must keep running.");
            Console.WriteLine("PASS: a second --launch forwarded to the first instance (exit 0), logged there, one child each, no file added and config.json unchanged.");

            // 3. Already running: refused with exit 1, an information box, and no second child.
            var already = await RunForwarderAsync(guardianExecutable, dataA, "--launch", "SI TARGET");
            Require(already.ExitCode == 1 && already.Boxes.Count == 1 && already.Boxes[0].Contains("already running", StringComparison.OrdinalIgnoreCase),
                $"An already-running entry must exit 1 with one information box: {already}");
            var alreadyById = await RunForwarderAsync(guardianExecutable, dataA, "--launch", target.Id.ToString());
            Require(alreadyById.ExitCode == 1 && alreadyById.Boxes.Count == 1 && alreadyById.Boxes[0].Contains("already running", StringComparison.OrdinalIgnoreCase),
                $"Selecting the running entry by id must also be AlreadyRunning: {alreadyById}");
            await Task.Delay(1000);
            Require(FindChildren(targetEvent).Length == 1, "AlreadyRunning must not start a second child.");
            await WaitForLogAsync(dataA, entry => entry.Module == "CommandLine" && entry.Level == "Warning"
                && entry.Message.Contains(": AlreadyRunning", StringComparison.Ordinal), "AlreadyRunning warning");
            Console.WriteLine("PASS: launching a running entry (by name and by id) was refused with AlreadyRunning, exit 1 and no second child.");

            // 4./5./6. NotFound, Ambiguous and an unknown id are refused with exit 1 and start nothing.
            var notFound = await RunForwarderAsync(guardianExecutable, dataA, "--launch", "SI Tar");
            Require(notFound.ExitCode == 1 && notFound.Boxes.Count == 1 && notFound.Boxes[0].Contains("No configured application", StringComparison.Ordinal),
                $"A partial name must be NotFound with exit 1: {notFound}");
            var ambiguous = await RunForwarderAsync(guardianExecutable, dataA, "--launch=SI DUP");
            Require(ambiguous.ExitCode == 1 && ambiguous.Boxes.Count == 1 && ambiguous.Boxes[0].Contains("none was launched", StringComparison.Ordinal),
                $"A duplicated name must be Ambiguous with exit 1: {ambiguous}");
            var unknownId = await RunForwarderAsync(guardianExecutable, dataA, "--launch", Guid.NewGuid().ToString());
            Require(unknownId.ExitCode == 1 && unknownId.Boxes.Count == 1 && unknownId.Boxes[0].Contains("id", StringComparison.Ordinal),
                $"An unknown id must be NotFound with exit 1: {unknownId}");
            await Task.Delay(1000);
            Require(!dupReady.WaitOne(0) && FindChildren(dupEvent).Length == 0, "An ambiguous selector must not start anything.");
            Require(CountLog(dataA, entry => entry.Module == "CommandLine" && entry.Level == "Warning"
                && (entry.Message.Contains(": NotFound", StringComparison.Ordinal) || entry.Message.Contains(": Ambiguous", StringComparison.Ordinal))) >= 3,
                "NotFound/Ambiguous refusals must be logged as warnings in the first instance.");
            Console.WriteLine("PASS: NotFound, Ambiguous and unknown-id selectors were refused with exit 1 and started nothing.");

            // 7. Usage error: exit 2, nothing forwarded.
            var forwardedBefore = CountLog(dataA, entry => entry.Module == "CommandLine");
            var usage = await RunForwarderAsync(guardianExecutable, dataA, "--bogus");
            Require(usage.ExitCode == 2 && usage.Boxes.Count == 1 && usage.Boxes[0].Contains("Usage", StringComparison.Ordinal), $"An unknown argument must exit 2 with a usage box: {usage}");
            var usageTwice = await RunForwarderAsync(guardianExecutable, dataA, "--launch", "a", "--launch", "b");
            Require(usageTwice.ExitCode == 2, $"A repeated --launch must exit 2: {usageTwice}");
            await Task.Delay(500);
            Require(CountLog(dataA, entry => entry.Module == "CommandLine") == forwardedBefore, "A usage error must not forward anything.");
            Console.WriteLine("PASS: usage errors exit 2 without forwarding.");

            // 8. Plain second start: forwards 'show', exits 0, and the first instance's window becomes visible.
            var plain = await RunForwarderAsync(guardianExecutable, dataA);
            Require(plain.ExitCode == 0 && plain.Boxes.Count == 0, $"A plain second start must exit 0 silently: {plain}");
            Require(await WaitUntilAsync(() => HasVisibleTopLevelWindow(guardianA.ProcessId), TimeSpan.FromSeconds(10)), "A plain second start must show the first instance's window.");
            Require(CountLog(dataA, entry => entry.Message == "Process Guardian started") == 1, "A plain second start must not start another Process Guardian.");
            await WaitForLogAsync(dataA, entry => entry.Module == "CommandLine" && entry.Message.StartsWith("Forwarded 'show': Shown", StringComparison.Ordinal), "show entry");
            Console.WriteLine("PASS: a plain second start forwarded 'show' (exit 0) and showed the first instance's window.");

            // 9. A different data directory is a different instance: no forwarding between A and B.
            var aCommandEntries = CountLog(dataA, entry => entry.Module == "CommandLine");
            guardianB = LaunchGuardian(launcher, guardianExecutable, dataB, null);
            await WaitForLogAsync(dataB, entry => entry.Message == "Process Guardian started", "second data directory start");
            await Task.Delay(1000);
            using (var processB = Process.GetProcessById((int)guardianB.ProcessId))
            {
                Require(!processB.HasExited, "A Process Guardian with a different data directory must keep running (it must not forward).");
            }

            var forwardB = await RunForwarderAsync(guardianExecutable, dataB, "--launch", "SI Second");
            Require(forwardB.ExitCode == 1 && forwardB.Boxes.Count == 1, $"Data directory B has no 'SI Second' entry, so the request must be refused there: {forwardB}");
            await WaitForLogAsync(dataB, entry => entry.Module == "CommandLine" && entry.Message.Contains("\"SI Second\": NotFound", StringComparison.Ordinal), "data directory B refusal");
            Require(CountLog(dataA, entry => entry.Module == "CommandLine") == aCommandEntries, "Requests for data directory B must not reach the instance for A.");
            Require(FindChildren(secondEvent).Length == 1, "Data directory B must not start A's entries.");
            Console.WriteLine("PASS: a different data directory runs its own instance and does not forward to the first one.");

            // 10. Ownership: end only the first instance; its children go with its own Job (KILL_ON_JOB_CLOSE).
            var childrenOfA = FindChildren(targetEvent).Concat(FindChildren(secondEvent)).ToArray();
            Require(childrenOfA.Length == 2, $"Expected the two children of the first instance, found {childrenOfA.Length}.");
            // Keep the mutex object alive across the kill (an unowned handle only), so that the next start sees it
            // abandoned instead of finding a fresh mutex.
            using var mutexWitness = new Mutex(false, SingleInstanceIdentity.GetMutexName(SingleInstanceIdentity.ComputeKey(dataA)));
            KillGuardianUnderTest(processA, guardianA, guardianExecutable);
            Require(await WaitUntilAsync(() => childrenOfA.All(IsExited), TimeSpan.FromSeconds(15)), "The children of the first instance did not end with its Job.");
            Console.WriteLine("PASS: the forwarded and startup children belonged to the first instance's Job and ended with it.");

            // 11. The mutex of an instance that died is abandoned; the next start takes it over and works normally.
            var abandonedEntry = Entry("SI After Crash", testExecutable, abandonedEvent);
            WriteConfiguration(dataA, target, second, dup1, dup2, abandonedEntry);
            guardianA2 = LaunchGuardian(launcher, guardianExecutable, dataA, "--launch \"SI After Crash\"");
            Require(abandonedReady.WaitOne(TimeSpan.FromSeconds(30)), "After the first instance died, a new --launch did not start as the first instance.");
            await WaitForLogAsync(dataA, entry => entry.Module == "CommandLine" && entry.Level == "Warning"
                && entry.Message.Contains("without releasing its single-instance mutex", StringComparison.Ordinal), "abandoned mutex warning");
            Require(guardianA2.JobObject.ContainsProcess(FindChildren(abandonedEvent).Single()), "The child after the takeover is not a descendant of the new instance.");
            Console.WriteLine("PASS: an abandoned single-instance mutex was taken over by the next start.");
        }
        finally
        {
            foreach (var launch in new[] { guardianA2, guardianB, guardianA })
            {
                if (launch is null)
                {
                    continue;
                }

                launch.JobObject.Terminate();
                launch.Dispose();
            }

            processA?.Dispose();
            await WaitUntilAsync(() => new[] { targetEvent, secondEvent, dupEvent, abandonedEvent }.All(name => FindChildren(name).Length == 0), TimeSpan.FromSeconds(15));
            DeleteDirectory(root);
        }

        Require(new[] { targetEvent, secondEvent, dupEvent, abandonedEvent }.All(name => FindChildren(name).Length == 0), "Children started by the single-instance tests are still running.");
        Require(!Directory.Exists(root), "The single-instance test data directory was not removed.");
        Console.WriteLine("PASS: single-instance tests cleaned up their Process Guardian instances, children and data directories.");
    }

    private static ApplicationConfiguration Entry(string name, string testExecutable, string eventName) => new()
    {
        Id = Guid.NewGuid(),
        SoftwareName = name,
        ExecutablePath = testExecutable,
        Arguments = "--window-child " + eventName,
        AddedAt = DateTimeOffset.UtcNow,
    };

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

    private sealed record ForwarderResult(int ExitCode, List<string> Boxes)
    {
        public override string ToString() => $"exit {ExitCode}, boxes [{string.Join(" || ", Boxes.Select(box => box.Replace('\n', ' ')))}]";
    }

    /// <summary>
    /// Starts a second Process Guardian with the given data directory and arguments, dismisses any message box owned
    /// by that process (and only that process), and returns its exit code and the box texts.
    /// </summary>
    private static async Task<ForwarderResult> RunForwarderAsync(string guardianExecutable, string dataDirectory, params string[] arguments)
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
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The forwarder did not start.");
        var processId = (uint)process.Id;
        var boxes = new List<string>();
        var dismissed = new HashSet<IntPtr>();
        var deadline = DateTime.UtcNow + ForwarderTimeout;
        while (!process.HasExited)
        {
            if (DateTime.UtcNow > deadline)
            {
                KillForwarderUnderTest(process, guardianExecutable);
                throw new InvalidOperationException($"A forwarder ({string.Join(" ", arguments)}) did not exit within {ForwarderTimeout.TotalSeconds:0} s.");
            }

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

        process.WaitForExit();
        return new ForwarderResult(process.ExitCode, boxes);
    }

    private static void KillForwarderUnderTest(Process process, string guardianExecutable)
    {
        if (process.HasExited)
        {
            return;
        }

        var path = process.MainModule?.FileName;
        if (!string.Equals(path is null ? null : Path.GetFullPath(path), guardianExecutable, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to kill PID {process.Id}: it is not the build under test ({path}).");
        }

        process.Kill();
        process.WaitForExit(10000);
    }

    private static void KillGuardianUnderTest(Process process, ProcessLaunchResult launch, string guardianExecutable)
    {
        Require(process.Id == (int)launch.ProcessId && launch.JobObject.ContainsProcess(process), "Refusing to kill a Process Guardian this test did not start.");
        var path = process.MainModule?.FileName;
        Require(string.Equals(path is null ? null : Path.GetFullPath(path), guardianExecutable, StringComparison.OrdinalIgnoreCase),
            $"Refusing to kill PID {process.Id}: it is not the build under test ({path}).");
        process.Kill();
        Require(process.WaitForExit(15000), "The first Process Guardian did not end after Kill.");
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
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process ended while it was being inspected (or is not accessible); it is not a live child.
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

    private static Process[] WaitForChildren(string eventName, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var children = FindChildren(eventName);
            if (children.Length >= expected || DateTime.UtcNow > deadline)
            {
                return children;
            }

            foreach (var child in children)
            {
                child.Dispose();
            }

            Thread.Sleep(100);
        }
    }

    private static bool IsExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
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
                    ? ((ProcessGuardian.Services.Logging.LogLevel)property.Value.GetInt32()).ToString()
                    : property.Value.ToString();
            }
        }

        return string.Empty;
    }

    private static int CountLog(string dataDirectory, Func<LogLine, bool> predicate) => ReadLog(dataDirectory).Count(predicate);

    private static async Task WaitForLogAsync(string dataDirectory, Func<LogLine, bool> predicate, string description)
    {
        if (!await WaitUntilAsync(() => ReadLog(dataDirectory).Any(predicate), TimeSpan.FromSeconds(15)))
        {
            var recent = string.Join(Environment.NewLine, ReadLog(dataDirectory).TakeLast(15).Select(line => $"  [{line.Level}] {line.Module}: {line.Message}"));
            throw new InvalidOperationException($"Log entry not found ({description}) in {dataDirectory}. Recent entries:{Environment.NewLine}{recent}");
        }
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string[] ListFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
        // An OK-only MessageBox has its button as IDCANCEL (2); fall back to IDOK (1).
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
