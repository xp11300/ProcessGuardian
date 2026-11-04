using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ProcessGuardian.Services.ApplicationLifecycle;
using ProcessGuardian.Services.ProcessManagement;

namespace ProcessGuardian.JobObjectTests;

/// <summary>
/// HANDOVER 7.18: the explicit process AppUserModelID. (1) A child copy of this test exe applies the ID through
/// <see cref="AppUserModelIdentity"/> and reads it back with GetCurrentProcessExplicitAppUserModelID; it also checks
/// that a rejected ID returns a failure without throwing. (2) The real ProcessGuardian.exe, started by this test in a
/// test-owned Job with its own %TEMP% data directory, logs that the ID was set. The taskbar icon itself is not
/// verifiable here (待验证).
/// </summary>
internal static class AppUserModelIdTests
{
    internal const string ChildMode = "--aumid-child";
    private const string DataDirectoryVariable = "PROCESSGUARDIAN_DATA_DIR";
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(30);

    public static async Task RunAsync(string guardianExecutable, string testExecutable)
    {
        RunChildProcessCheck(Path.GetFullPath(testExecutable));
        await RunRealGuardianCheckAsync(Path.GetFullPath(guardianExecutable));
    }

    /// <summary>Child mode: runs in a fresh process so the ID read back is the one this process set.</summary>
    internal static int RunChild(string resultPath)
    {
        var lines = new List<string> { "before=" + QueryExplicitId() };
        var applied = AppUserModelIdentity.Apply();
        lines.Add($"apply={applied.Succeeded}|0x{applied.HResult:X8}|{applied.Exception?.GetType().Name}");
        lines.Add("after=" + QueryExplicitId());
        var rejected = AppUserModelIdentity.Apply(new string('x', 200));
        lines.Add($"rejected={rejected.Succeeded}|0x{rejected.HResult:X8}|{rejected.Exception?.GetType().Name}");
        lines.Add("rejectedText=" + rejected.Describe());
        lines.Add("afterRejected=" + QueryExplicitId());
        File.WriteAllLines(resultPath, lines, new UTF8Encoding(false));
        return 0;
    }

    private static void RunChildProcessCheck(string testExecutable)
    {
        var resultPath = Path.Combine(Path.GetTempPath(), $"PG-AppUserModelIdTests-{Guid.NewGuid():N}.txt");
        var startInfo = new ProcessStartInfo(testExecutable) { UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add(ChildMode);
        startInfo.ArgumentList.Add(resultPath);
        try
        {
            using (var child = Process.Start(startInfo) ?? throw new InvalidOperationException("The AppUserModelID test child did not start."))
            {
                if (!child.WaitForExit(ChildTimeout))
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit(5000);
                    throw new InvalidOperationException("The AppUserModelID test child did not finish in time.");
                }

                Require(child.ExitCode == 0, $"The AppUserModelID test child exited with {child.ExitCode}.");
            }

            var values = File.ReadAllLines(resultPath)
                .Select(line => line.Split('=', 2))
                .ToDictionary(parts => parts[0], parts => parts.Length > 1 ? parts[1] : string.Empty);
            var report = string.Join(" ; ", values.Select(pair => $"{pair.Key}={pair.Value}"));
            Require(values["before"].StartsWith("hr:", StringComparison.Ordinal), $"A fresh process must have no explicit AppUserModelID: {report}");
            Require(values["apply"].StartsWith("True|", StringComparison.Ordinal), $"Applying the AppUserModelID must succeed: {report}");
            Require(values["after"] == "ok:" + AppUserModelIdentity.AppId, $"GetCurrentProcessExplicitAppUserModelID must return {AppUserModelIdentity.AppId}: {report}");
            Console.WriteLine($"PASS: AppUserModelIdentity.Apply set the process AppUserModelID to {AppUserModelIdentity.AppId} (read back with GetCurrentProcessExplicitAppUserModelID in a test child).");

            Require(values["rejected"].StartsWith("False|0x8", StringComparison.Ordinal) && values["rejected"].EndsWith("|", StringComparison.Ordinal),
                $"A rejected ID must return a failure HRESULT without an exception: {report}");
            Require(values["rejectedText"].Contains("failed (HRESULT 0x8", StringComparison.Ordinal), $"A failure must be described with its HRESULT: {report}");
            Require(values["afterRejected"] == "ok:" + AppUserModelIdentity.AppId, $"A rejected ID must leave the existing AppUserModelID unchanged: {report}");
            Console.WriteLine("PASS: a rejected AppUserModelID returns a failure HRESULT with a log text, throws nothing, and keeps the existing ID.");
        }
        finally
        {
            if (File.Exists(resultPath))
            {
                File.Delete(resultPath);
            }
        }
    }

    private static async Task RunRealGuardianCheckAsync(string guardianExecutable)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "PG-AppUserModelIdTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, "config.json"), "[]", new UTF8Encoding(false));
        var launcher = new ProcessLauncher();
        ProcessLaunchResult? guardian = null;
        try
        {
            // ProcessLauncher passes this process's environment on, so the variable is set only around the launch.
            var previous = Environment.GetEnvironmentVariable(DataDirectoryVariable);
            try
            {
                Environment.SetEnvironmentVariable(DataDirectoryVariable, dataDirectory);
                guardian = launcher.Launch(guardianExecutable, null, Path.GetDirectoryName(guardianExecutable));
            }
            finally
            {
                Environment.SetEnvironmentVariable(DataDirectoryVariable, previous);
            }

            var expected = $"Process AppUserModelID set to \"{AppUserModelIdentity.AppId}\"";
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            List<(string Level, string Module, string Message)> entries;
            while (true)
            {
                entries = ReadLog(dataDirectory);
                if (entries.Any(entry => entry.Message == expected) || DateTime.UtcNow > deadline)
                {
                    break;
                }

                await Task.Delay(200);
            }

            var recent = string.Join(" | ", entries.TakeLast(10).Select(entry => $"[{entry.Level}] {entry.Module}: {entry.Message}"));
            var started = entries.FindIndex(entry => entry.Message == "Process Guardian started");
            var applied = entries.FindIndex(entry => entry.Message == expected);
            Require(started >= 0 && applied > started, $"The real Process Guardian must log '{expected}' after 'Process Guardian started': {recent}");
            Require(entries[applied].Module == "Application" && entries[applied].Level == "Information", $"Unexpected AppUserModelID log entry: {recent}");
            Require(!entries.Any(entry => entry.Message.Contains("SetCurrentProcessExplicitAppUserModelID", StringComparison.Ordinal)), $"No AppUserModelID failure may be logged: {recent}");
            Console.WriteLine("PASS: the real ProcessGuardian.exe (isolated data directory, test-owned Job) logged that its AppUserModelID was set at startup.");
        }
        finally
        {
            if (guardian is not null)
            {
                var processId = (int)guardian.ProcessId;
                guardian.JobObject.Terminate();
                guardian.Dispose();
                for (var attempt = 0; attempt < 50 && IsRunning(processId); attempt++)
                {
                    await Task.Delay(100);
                }
            }

            for (var attempt = 0; attempt < 20 && Directory.Exists(dataDirectory); attempt++)
            {
                try
                {
                    Directory.Delete(dataDirectory, recursive: true);
                }
                catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && attempt < 19)
                {
                    await Task.Delay(250);
                }
            }
        }

        Require(!Directory.Exists(dataDirectory), "The AppUserModelID test data directory was not removed.");
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static List<(string Level, string Module, string Message)> ReadLog(string dataDirectory)
    {
        var entries = new List<(string Level, string Module, string Message)>();
        var logDirectory = Path.Combine(dataDirectory, "Logs");
        if (!Directory.Exists(logDirectory))
        {
            return entries;
        }

        foreach (var path in Directory.EnumerateFiles(logDirectory, "processguardian-*.log").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
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
                    entries.Add((Property(document.RootElement, "Level"), Property(document.RootElement, "Module"), Property(document.RootElement, "Message")));
                }
                catch (JsonException)
                {
                    // A line still being written is incomplete; it is read again on the next poll.
                }
            }
        }

        return entries;
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

    private static string QueryExplicitId()
    {
        var hresult = GetCurrentProcessExplicitAppUserModelID(out var pointer);
        try
        {
            return hresult >= 0 ? "ok:" + Marshal.PtrToStringUni(pointer) : $"hr:0x{hresult:X8}";
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int GetCurrentProcessExplicitAppUserModelID(out IntPtr appId);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
