using System.IO;
using System.Text;
using ProcessGuardian;
using ProcessGuardian.Services;
using ProcessGuardian.Services.ApplicationLifecycle;
using ProcessGuardian.Services.Logging;

namespace ProcessGuardian.JobObjectTests;

/// <summary>
/// Stage-12 startup / fatal-error reporting (F1-F6) exercised in-process: no message box is ever shown (the box is
/// an injected delegate), every file goes to a folder under this run's isolated PROCESSGUARDIAN_DATA_DIR, and the
/// process-wide AppLog / FatalErrorReporter state is restored afterwards. The real-exe crash paths are covered by
/// the separate FaultInjection-build harness, not here.
/// </summary>
internal static class StartupFailureTests
{
    public static async Task RunAsync()
    {
        var originalOverride = Environment.GetEnvironmentVariable(AppDataPaths.OverrideVariable)
            ?? throw new InvalidOperationException("The test run must set an isolated data directory before these tests.");
        var root = Path.Combine(originalOverride, "startup-failure-tests");
        var previousLogger = AppLog.Replace(null);
        try
        {
            Directory.CreateDirectory(root);
            RunFallbackLogTests(root);
            await RunWriteFailureCountTestAsync(root);
            await RunReportWithHealthyLoggerTestAsync(root);
            await RunReportWithBrokenLoggerTestAsync(root);
            RunReportWithoutLoggerTest(root);
            RunMessageTruncationTest();
            await RunUnobservedTaskHandlerTestAsync(root);
            await RunTrayExitFailureTestsAsync(root);
        }
        finally
        {
            FatalErrorReporter.ResetForTests();
            AppLog.Replace(previousLogger);
            Environment.SetEnvironmentVariable(AppDataPaths.OverrideVariable, originalOverride);
        }
    }

    private static void RunFallbackLogTests(string root)
    {
        var dataDirectory = UseDataDirectory(root, "fallback-candidates");
        var candidates = FallbackLog.GetCandidateDirectories();
        if (candidates.Count != 2 || !string.Equals(candidates[0], dataDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidates[1], Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fallback log candidates must be the absolute data-directory override first, then %TEMP%: " + string.Join(" | ", candidates));

        Environment.SetEnvironmentVariable(AppDataPaths.OverrideVariable, "relative\\data");
        if (FallbackLog.GetCandidateDirectories().Count != 1)
            throw new InvalidOperationException("A relative data-directory override must not be used for the fallback log.");
        Environment.SetEnvironmentVariable(AppDataPaths.OverrideVariable, dataDirectory);

        var exception = CreateThrownException("fallback content check");
        var path = FallbackLog.Write("Fallback content check", exception, new[] { dataDirectory })
            ?? throw new InvalidOperationException("The fallback log was not written to a writable folder.");
        AssertFallbackFile(path, dataDirectory, "Fallback content check", exception);

        var notADirectory = Path.Combine(root, "fallback-blocker");
        File.WriteAllText(notADirectory, "not a directory");
        var secondChoice = Path.Combine(root, "fallback-second-choice");
        var secondPath = FallbackLog.Write("Second choice check", exception, new[] { Path.Combine(notADirectory, "sub"), secondChoice })
            ?? throw new InvalidOperationException("The fallback log did not move on to the next folder.");
        AssertFallbackFile(secondPath, secondChoice, "Second choice check", exception);

        if (FallbackLog.Write("Nowhere check", exception, new[] { Path.Combine(notADirectory, "sub") }) is not null)
            throw new InvalidOperationException("The fallback log reported success although no folder was writable.");
        Console.WriteLine("PASS: stage-12 startup: fallback log uses the isolated data folder first, moves to the next folder on failure, and reports when nothing could be written");
    }

    private static async Task RunWriteFailureCountTestAsync(string root)
    {
        var blocker = Path.Combine(root, "count-blocker");
        await File.WriteAllTextAsync(blocker, "not a directory");
        var logger = new FileLogService(Path.Combine(blocker, "Logs"));
        if (logger.WriteFailureCount != 0 || logger.IsDisposed)
            throw new InvalidOperationException("A new logger must start with no write failures and not disposed.");
        logger.Write(LogLevel.Error, "test", "count failure 1");
        logger.Write(LogLevel.Error, "test", "count failure 2");
        await logger.FlushAsync();
        if (logger.WriteFailureCount != 2 || logger.LastWriteException is null)
            throw new InvalidOperationException($"Expected 2 counted write failures, got {logger.WriteFailureCount}.");
        await logger.DisposeAsync();
        if (!logger.IsDisposed)
            throw new InvalidOperationException("IsDisposed was not set after DisposeAsync.");
        Console.WriteLine("PASS: stage-12 startup: FileLogService counts every failed write and exposes IsDisposed");
    }

    private static async Task RunReportWithHealthyLoggerTestAsync(string root)
    {
        var dataDirectory = UseDataDirectory(root, "healthy");
        var logDirectory = Path.Combine(dataDirectory, "Logs");
        await using var logger = new FileLogService(logDirectory);
        AppLog.Replace(logger);
        FatalErrorReporter.ResetForTests();
        var messages = new List<(string Text, string Title)>();
        var trayDisposals = 0;
        var exception = CreateThrownException("healthy logger startup failure");

        var first = FatalErrorReporter.Report("startup", exception, () => trayDisposals++, (text, title) => messages.Add((text, title)));
        if (first.Duplicate || !first.Persisted || first.FallbackPath is not null || !first.MessageShown)
            throw new InvalidOperationException($"Unexpected first report outcome: {first}.");
        if (Directory.GetFiles(dataDirectory, FallbackLog.FilePrefix + "*").Length != 0)
            throw new InvalidOperationException("A fallback file was written although the normal log write was confirmed.");
        var logText = ReadLogText(logDirectory);
        if (!logText.Contains("Process Guardian startup failed", StringComparison.Ordinal)
            || !logText.Contains("healthy logger startup failure", StringComparison.Ordinal)
            || !logText.Contains(nameof(CreateThrownException), StringComparison.Ordinal))
            throw new InvalidOperationException("The startup failure (message and stack) is missing from the log file.");

        // The startup path rethrows; the dispatcher and AppDomain handlers then see the same exception object.
        var second = FatalErrorReporter.Report("UI-thread", exception, () => trayDisposals++, (text, title) => messages.Add((text, title)));
        var third = FatalErrorReporter.Report("terminating", exception, () => trayDisposals++, (text, title) => messages.Add((text, title)));
        if (!second.Duplicate || !third.Duplicate || trayDisposals != 1 || messages.Count != 1)
            throw new InvalidOperationException("The same exception reaching later handlers was reported again.");
        if (CountOccurrences(ReadLogText(logDirectory), "healthy logger startup failure") != CountOccurrences(logText, "healthy logger startup failure"))
            throw new InvalidOperationException("The same exception was logged twice.");

        // A different exception later is logged, but no second message box is shown.
        var other = FatalErrorReporter.Report("terminating", CreateThrownException("later different failure"), null, (text, title) => messages.Add((text, title)));
        if (other.Duplicate || other.MessageShown || messages.Count != 1 || !ReadLogText(logDirectory).Contains("later different failure", StringComparison.Ordinal))
            throw new InvalidOperationException("A different later exception must be logged once without a second message box.");

        var (text, title) = messages[0];
        if (title != FatalErrorReporter.MessageTitle || !text.Contains("could not start", StringComparison.Ordinal)
            || !text.Contains("InvalidOperationException: healthy logger startup failure", StringComparison.Ordinal)
            || !text.Contains("Details: log folder " + logDirectory, StringComparison.Ordinal)
            || text.Contains(" at ", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected fatal message: " + text);
        Console.WriteLine("PASS: stage-12 startup: confirmed log write, no fallback, tray removed once, one message box without stack, same exception not reported twice");
        AppLog.Replace(null);
    }

    private static async Task RunReportWithBrokenLoggerTestAsync(string root)
    {
        var dataDirectory = UseDataDirectory(root, "logs-is-a-file");
        var logPath = Path.Combine(dataDirectory, "Logs");
        await File.WriteAllTextAsync(logPath, "a file where the Logs folder should be");
        await using var logger = new FileLogService(logPath);
        AppLog.Replace(logger);
        FatalErrorReporter.ResetForTests();
        string? shown = null;
        var exception = CreateThrownException("broken logger startup failure");

        var outcome = FatalErrorReporter.Report("startup", exception, null, (text, _) => shown = text);
        if (outcome.Persisted || outcome.FallbackPath is null || shown is null)
            throw new InvalidOperationException($"A failed log write must produce a fallback file and a message: {outcome}.");
        AssertFallbackFile(outcome.FallbackPath, dataDirectory, "Process Guardian startup failed", exception);
        if (!shown.Contains("Details: " + outcome.FallbackPath, StringComparison.Ordinal))
            throw new InvalidOperationException("The message does not point to the fallback file: " + shown);
        Console.WriteLine("PASS: stage-12 startup: with Logs blocked by a file the fatal error goes to a fallback file in the data folder and the message names it");
        AppLog.Replace(null);
    }

    private static void RunReportWithoutLoggerTest(string root)
    {
        var dataDirectory = UseDataDirectory(root, "no-logger");
        AppLog.Replace(null);
        FatalErrorReporter.ResetForTests();
        var exception = CreateThrownException("failure before the logger exists");
        string? shown = null;
        var outcome = FatalErrorReporter.Report("startup", exception, null, (text, _) => shown = text);
        if (outcome.Persisted || outcome.FallbackPath is null || shown is null)
            throw new InvalidOperationException($"A failure before the logger exists must use the fallback file: {outcome}.");
        AssertFallbackFile(outcome.FallbackPath, dataDirectory, "Process Guardian startup failed", exception);

        FatalErrorReporter.ResetForTests();
        var failingBox = FatalErrorReporter.Report("UI-thread", CreateThrownException("message box failure"), () => throw new InvalidOperationException("tray dispose failure"), (_, _) => throw new InvalidOperationException("message box failure"));
        if (failingBox.FallbackPath is null || !failingBox.MessageShown)
            throw new InvalidOperationException("A failing tray removal or message box must not stop the report.");
        Console.WriteLine("PASS: stage-12 startup: failure before the logger exists is written to the fallback file; tray/message failures do not escape the reporter");
    }

    private static void RunMessageTruncationTest()
    {
        var longMessage = new string('x', 1000);
        var text = FatalErrorReporter.BuildMessage("UI-thread", new InvalidOperationException(longMessage), null);
        var summaryLine = text.Split('\n').Single(line => line.StartsWith("InvalidOperationException: ", StringComparison.Ordinal));
        if (summaryLine.Length != FatalErrorReporter.MaxSummaryLength || !summaryLine.EndsWith("...", StringComparison.Ordinal)
            || !text.Contains("encountered a fatal error and will close", StringComparison.Ordinal)
            || !text.Contains("No log could be written", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected truncated fatal message: " + text);
        Console.WriteLine("PASS: stage-12 startup: fatal message summary is truncated to 300 characters and says when no log could be written");
    }

    private static async Task RunUnobservedTaskHandlerTestAsync(string root)
    {
        var logDirectory = Path.Combine(UseDataDirectory(root, "unobserved"), "Logs");
        await using var logger = new FileLogService(logDirectory);
        AppLog.Replace(logger);
        FatalErrorReporter.ResetForTests();
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(CreateThrownException("unobserved task failure")));
        App.LogUnobservedTaskException(null, args);
        await logger.FlushAsync();
        var logText = ReadLogText(logDirectory);
        if (args.Observed || !logText.Contains("Unobserved task exception", StringComparison.Ordinal)
            || !logText.Contains("unobserved task failure", StringComparison.Ordinal) || !logText.Contains("\"Error\"", StringComparison.Ordinal))
            throw new InvalidOperationException("The unobserved task handler must log an Error entry and leave the exception unobserved.");
        Console.WriteLine("PASS: stage-12 startup: unobserved task exception is logged as Error only (not marked observed, no message box)");
        AppLog.Replace(null);
    }

    private static async Task RunTrayExitFailureTestsAsync(string root)
    {
        var logDirectory = Path.Combine(UseDataDirectory(root, "tray-exit-active"), "Logs");
        await using (var logger = new FileLogService(logDirectory))
        {
            AppLog.Replace(logger);
            RaiseFailingTrayExit("tray exit failure with active logger");
            if (!ReadLogText(logDirectory).Contains("tray exit failure with active logger", StringComparison.Ordinal))
                throw new InvalidOperationException("A tray exit failure was not written to the active log.");
        }

        var disposedDirectory = UseDataDirectory(root, "tray-exit-disposed");
        var disposedLogger = new FileLogService(Path.Combine(disposedDirectory, "Logs"));
        await disposedLogger.DisposeAsync();
        AppLog.Replace(disposedLogger);
        RaiseFailingTrayExit("tray exit failure after logger disposal");
        var fallback = Directory.GetFiles(disposedDirectory, FallbackLog.FilePrefix + "*.log");
        if (fallback.Length != 1 || !File.ReadAllText(fallback[0]).Contains("tray exit failure after logger disposal", StringComparison.Ordinal))
            throw new InvalidOperationException("A tray exit failure after logger disposal was not written to a fallback file.");
        Console.WriteLine("PASS: stage-12 startup: tray exit failure is logged, or written to a fallback file once the logger is disposed");
        AppLog.Replace(null);
    }

    private static void RaiseFailingTrayExit(string message)
    {
        var tray = new FakeTrayIconService();
        using var router = new TrayCommandRouter(tray, () => { }, () => { }, () => Task.FromException(CreateThrownException(message)));
        tray.RaiseExit();
    }

    private static string UseDataDirectory(string root, string name)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable(AppDataPaths.OverrideVariable, directory);
        return directory;
    }

    private static InvalidOperationException CreateThrownException(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    private static void AssertFallbackFile(string path, string expectedDirectory, string context, Exception exception)
    {
        if (!string.Equals(Path.GetDirectoryName(path), expectedDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith(FallbackLog.FilePrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Fallback file {path} is not in {expectedDirectory}.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            throw new InvalidOperationException("The fallback file must not start with a BOM.");
        var text = Encoding.UTF8.GetString(bytes);
        if (!text.Contains(context, StringComparison.Ordinal) || !text.Contains(exception.GetType().FullName!, StringComparison.Ordinal)
            || !text.Contains(exception.Message, StringComparison.Ordinal) || !text.Contains(nameof(CreateThrownException), StringComparison.Ordinal))
            throw new InvalidOperationException("The fallback file lacks the context, exception type, message or stack: " + text);
    }

    private static string ReadLogText(string logDirectory) =>
        string.Concat(Directory.GetFiles(logDirectory, "processguardian-*.log").Select(path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
