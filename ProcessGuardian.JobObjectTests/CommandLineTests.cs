using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using ProcessGuardian.Services;
using ProcessGuardian.Services.CommandLine;

namespace ProcessGuardian.JobObjectTests;

/// <summary>
/// In-process checks of the --launch / single-instance building blocks: argument parsing, entry selection, the
/// data-directory key and names, the pipe protocol (including BadRequest cases), the forwarder's exit codes and
/// feedback, and a pipe round trip against a fake dispatcher. Nothing here starts ProcessGuardian.exe or touches
/// any configuration or log file.
/// </summary>
internal static class CommandLineTests
{
    private const string DataDirectoryVariable = "PROCESSGUARDIAN_DATA_DIR";

    public static async Task RunAsync()
    {
        TestParsing();
        TestSelection();
        TestIdentity();
        TestProtocol();
        await TestLineReaderAsync();
        TestExitCodesAndPresenter();
        await TestPipeRoundTripAsync();
        await TestForegroundGrantPidLookupAsync();
        Console.WriteLine("PASS: command-line parsing, selection, single-instance names, pipe protocol and forwarding checks passed.");
    }

    private sealed record Entry(Guid Id, string Name);

    private static void TestParsing()
    {
        ExpectValid(Array.Empty<string>(), null);
        ExpectValid(null, null);
        ExpectValid(new[] { "--launch", "WPS" }, "WPS");
        ExpectValid(new[] { "--LAUNCH", "WPS" }, "WPS");
        ExpectValid(new[] { "--Launch", "WPS Office" }, "WPS Office");
        ExpectValid(new[] { "--launch=WPS Office" }, "WPS Office");
        ExpectValid(new[] { "--LaUnCh=a=b" }, "a=b");
        ExpectValid(new[] { "--launch=--x" }, "--x");
        ExpectValid(new[] { "--launch", "  WPS  " }, "  WPS  ");
        ExpectValid(new[] { "--launch", "-x" }, "-x");

        ExpectInvalid("--launch");
        ExpectInvalid("--launch", "");
        ExpectInvalid("--launch", "   ");
        ExpectInvalid("--launch=");
        ExpectInvalid("--launch=   ");
        ExpectInvalid("--launch", "--other");
        ExpectInvalid("--launch", "a", "--launch", "b");
        ExpectInvalid("--launch=a", "--launch=b");
        ExpectInvalid("--launch=a", "--launch", "a");
        ExpectInvalid("--launch", "a", "extra");
        ExpectInvalid("--launch=a", "extra");
        ExpectInvalid("extra", "--launch", "a");
        ExpectInvalid("WPS");
        ExpectInvalid("--bogus");
        ExpectInvalid("--sample", "value");
        ExpectInvalid("/launch", "a");
        ExpectInvalid("-launch", "a");
        ExpectInvalid("--launchx=a");
        ExpectInvalid("--launch:a");
        ExpectInvalid("");

        var tooLong = CommandLineParser.Parse(new[] { new string('z', 500) });
        Assert(!tooLong.IsValid && tooLong.Error!.Length < 300, "A long unknown argument must be truncated in the usage error.");
        Assert(CommandLineParser.UsageErrorExitCode == 2 && CommandLineExitCodes.UsageError == 2, "The usage error exit code must be 2.");
        Assert(CommandLineParser.UsageText.Contains("--launch", StringComparison.Ordinal), "The usage text must describe --launch.");
    }

    private static void ExpectValid(string[]? args, string? selector)
    {
        var result = CommandLineParser.Parse(args);
        Assert(result.IsValid && result.Error is null, $"Expected [{Join(args)}] to parse, got error: {result.Error}");
        Assert(result.Options!.LaunchSelector == selector, $"Expected selector '{selector}' for [{Join(args)}], got '{result.Options.LaunchSelector}'.");
        Assert(result.Options.HasLaunch == (selector is not null), $"HasLaunch is wrong for [{Join(args)}].");
    }

    private static void ExpectInvalid(params string[] args)
    {
        var result = CommandLineParser.Parse(args);
        Assert(!result.IsValid && result.Options is null && !string.IsNullOrWhiteSpace(result.Error), $"Expected [{Join(args)}] to be a usage error.");
    }

    private static string Join(IEnumerable<string>? args) => args is null ? "null" : string.Join(" | ", args);

    private static void TestSelection()
    {
        var idWps = Guid.NewGuid();
        var idWpsOffice = Guid.NewGuid();
        var idDup1 = Guid.NewGuid();
        var idDup2 = Guid.NewGuid();
        var idSpaced = Guid.NewGuid();
        var idGuidNamed = Guid.NewGuid();
        var unusedGuid = Guid.NewGuid();
        var items = new List<Entry>
        {
            new(idWps, "WPS"),
            new(idWpsOffice, "wps office"),
            new(idDup1, "Dup"),
            new(idDup2, "dup"),
            new(idSpaced, " Spaced "),
            new(idGuidNamed, idWps.ToString()),
            new(Guid.NewGuid(), @"C:\Tools\tool.exe"),
            new(Guid.NewGuid(), ""),
        };

        ExpectFound(items, "WPS", idWps, byId: false);
        ExpectFound(items, "wps", idWps, byId: false);
        ExpectFound(items, "  WPS  ", idWps, byId: false);
        ExpectFound(items, "WPS OFFICE", idWpsOffice, byId: false);
        ExpectFound(items, "spaced", idSpaced, byId: false);
        ExpectFound(items, idWpsOffice.ToString(), idWpsOffice, byId: true);
        ExpectFound(items, idWpsOffice.ToString("B").ToUpperInvariant(), idWpsOffice, byId: true);
        ExpectFound(items, idWpsOffice.ToString("N"), idWpsOffice, byId: true);
        ExpectFound(items, " " + idWpsOffice + " ", idWpsOffice, byId: true);
        // A GUID selector matches ids only: an entry whose *name* is that GUID text is not chosen.
        ExpectFound(items, idWps.ToString(), idWps, byId: true);

        ExpectNotFound(items, "WPS Off");
        ExpectNotFound(items, "office");
        ExpectNotFound(items, "W");
        ExpectNotFound(items, "tool");
        ExpectNotFound(items, "tool.exe");
        ExpectNotFound(items, "");
        ExpectNotFound(items, "   ");
        var byUnusedId = ApplicationSelector.Select(items, item => item.Id, item => item.Name, unusedGuid.ToString());
        Assert(byUnusedId.Status == ApplicationSelectionStatus.NotFound && byUnusedId.SelectedById, "An unknown GUID must be NotFound by id.");
        // A name that is a GUID never matches by name.
        var guidNameOnly = new List<Entry> { new(Guid.NewGuid(), unusedGuid.ToString()) };
        var guidName = ApplicationSelector.Select(guidNameOnly, item => item.Id, item => item.Name, unusedGuid.ToString());
        Assert(guidName.Status == ApplicationSelectionStatus.NotFound, "A GUID selector must not match an entry by its name.");

        var dup = ApplicationSelector.Select(items, item => item.Id, item => item.Name, "DUP");
        Assert(dup.Status == ApplicationSelectionStatus.Ambiguous && dup.Item is null && dup.MatchCount == 2, "Two entries with the same name must be Ambiguous.");
        var dupById = ApplicationSelector.Select(items, item => item.Id, item => item.Name, idDup2.ToString());
        Assert(dupById.Status == ApplicationSelectionStatus.Found && dupById.Item!.Id == idDup2, "A duplicate name must still be selectable by id.");
        var empty = ApplicationSelector.Select(new List<Entry>(), item => item.Id, item => item.Name, "WPS");
        Assert(empty.Status == ApplicationSelectionStatus.NotFound, "An empty list must give NotFound.");
    }

    private static void ExpectFound(List<Entry> items, string selector, Guid expected, bool byId)
    {
        var selection = ApplicationSelector.Select(items, item => item.Id, item => item.Name, selector);
        Assert(selection.Status == ApplicationSelectionStatus.Found && selection.Item!.Id == expected && selection.SelectedById == byId,
            $"Selector '{selector}' must select {expected} (byId={byId}); got {selection.Status} {selection.Item?.Id} byId={selection.SelectedById}.");
    }

    private static void ExpectNotFound(List<Entry> items, string selector)
    {
        var selection = ApplicationSelector.Select(items, item => item.Id, item => item.Name, selector);
        Assert(selection.Status == ApplicationSelectionStatus.NotFound && selection.Item is null, $"Selector '{selector}' must be NotFound, got {selection.Status}.");
    }

    private static void TestIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "PG-KeyCheck-" + Guid.NewGuid().ToString("N"));
        var key = SingleInstanceIdentity.ComputeKey(root);
        Assert(key.Length == 16 && key.All(Uri.IsHexDigit), $"The key must be 16 hex characters, got '{key}'.");
        foreach (var variant in new[]
                 {
                     root + Path.DirectorySeparatorChar,
                     root + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar,
                     root + Path.AltDirectorySeparatorChar,
                     root.ToUpperInvariant(),
                     root.ToLowerInvariant() + Path.DirectorySeparatorChar,
                     root.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                     Path.Combine(root, "sub", ".."),
                 })
        {
            Assert(SingleInstanceIdentity.ComputeKey(variant) == key, $"The key must not depend on case or trailing separators: '{variant}'.");
        }

        Assert(SingleInstanceIdentity.ComputeKey(root + "2") != key, "Different data directories must have different keys.");
        Assert(SingleInstanceIdentity.ComputeKey(Path.Combine(root, "child")) != key, "A child directory must have a different key.");
        Assert(!Directory.Exists(root), "Computing a key must not create the directory.");

        Assert(SingleInstanceIdentity.GetMutexName(key) == @"Local\ProcessGuardian." + key, "Mutex name format is wrong.");
        Assert(SingleInstanceIdentity.GetPipeName(key, "S-1-5-21-1", 7) == "ProcessGuardian.S-1-5-21-1.7." + key, "Pipe name format is wrong.");

        var previous = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(DataDirectoryVariable, root + Path.DirectorySeparatorChar);
            var names = SingleInstanceNames.ForCurrentProcess();
            string sid;
            using (var identity = WindowsIdentity.GetCurrent())
            {
                sid = identity.User!.Value;
            }

            int session;
            using (var current = System.Diagnostics.Process.GetCurrentProcess())
            {
                session = current.SessionId;
            }

            Assert(names.Key == key, "ForCurrentProcess must use PROCESSGUARDIAN_DATA_DIR for the key.");
            Assert(names.MutexName == @"Local\ProcessGuardian." + key, "ForCurrentProcess mutex name is wrong.");
            Assert(names.PipeName == $"ProcessGuardian.{sid}.{session}.{key}", $"The pipe name must contain the user SID and session id: {names.PipeName}");
            Assert(names.PipeName.Contains(sid, StringComparison.Ordinal) && names.PipeName.Contains("." + session + ".", StringComparison.Ordinal), "Pipe name must contain SID and session.");
            Assert(!Directory.Exists(root), "Building single-instance names must not create the data directory.");

            Environment.SetEnvironmentVariable(DataDirectoryVariable, null);
            var defaultKey = SingleInstanceNames.ForCurrentProcess().Key;
            var expectedDefault = SingleInstanceIdentity.ComputeKey(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessGuardian"));
            Assert(defaultKey == expectedDefault && defaultKey != key, "Without the override the key must follow %LOCALAPPDATA%\\ProcessGuardian.");
            Assert(AppDataPaths.GetDataDirectory() is not null, "AppDataPaths must still resolve.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(DataDirectoryVariable, previous);
        }

        // Mutex: the first acquire owns it, a second acquire on another thread does not, release is idempotent.
        var mutexName = @"Local\ProcessGuardian.Test." + Guid.NewGuid().ToString("N");
        using (var first = SingleInstanceMutex.Acquire(mutexName))
        {
            Assert(first.IsOwner && !first.WasAbandoned, "The first acquire must own the mutex.");
            // A dedicated thread: Task.Wait may inline a Task.Run body on this thread, and a
            // Win32 mutex is re-entrant for its owning thread.
            var secondOwned = true;
            var secondThread = new Thread(() =>
            {
                using var second = SingleInstanceMutex.Acquire(mutexName);
                secondOwned = second.IsOwner;
            });
            secondThread.Start();
            secondThread.Join();
            Assert(!secondOwned, "A second acquire must not own a held mutex.");
            first.Release();
            first.Release();
            Assert(!first.IsOwner, "Release must clear ownership.");
        }

        // Abandoned: a thread that ends while owning the mutex leaves it abandoned; that counts as acquired.
        var abandonedName = @"Local\ProcessGuardian.Test." + Guid.NewGuid().ToString("N");
        var ownerThread = new Thread(() =>
        {
            var owner = new Mutex(false, abandonedName);
            owner.WaitOne();
            GC.KeepAlive(owner);
        });
        Mutex? keepAlive = new Mutex(false, abandonedName);
        try
        {
            ownerThread.Start();
            ownerThread.Join();
            using var acquired = SingleInstanceMutex.Acquire(abandonedName);
            Assert(acquired.IsOwner && acquired.WasAbandoned, "An abandoned mutex must count as acquired and be reported as abandoned.");
        }
        finally
        {
            keepAlive.Dispose();
        }
    }

    private static void TestProtocol()
    {
        var launchBytes = CommandProtocol.SerializeRequest(CommandRequest.Launch("WPS \"Office\" 中文"));
        var launchText = Encoding.UTF8.GetString(launchBytes);
        Assert(launchText.EndsWith('\n') && launchText.IndexOf('\n') == launchText.Length - 1, "A request must be exactly one line.");
        Assert(launchBytes.Length > 0 && launchBytes[0] == (byte)'{', "A request must not start with a BOM.");
        Assert(CommandProtocol.TryParseRequest(launchBytes.AsSpan(0, launchBytes.Length - 1), out var parsedLaunch, out _)
            && parsedLaunch == CommandRequest.Launch("WPS \"Office\" 中文"), "A launch request must round-trip.");
        var showBytes = CommandProtocol.SerializeRequest(CommandRequest.Show());
        Assert(Encoding.UTF8.GetString(showBytes) == "{\"v\":1,\"cmd\":\"show\"}\n", $"Unexpected show request: {Encoding.UTF8.GetString(showBytes)}");
        Assert(CommandProtocol.TryParseRequest(showBytes.AsSpan(0, showBytes.Length - 1), out var parsedShow, out _) && parsedShow == CommandRequest.Show(), "A show request must round-trip.");
        ExpectRequestOk("{\"v\":1,\"cmd\":\"launch\",\"selector\":\"x\"}");
        ExpectRequestOk("  {\"selector\":\"x\",\"cmd\":\"launch\",\"v\":1}  ");
        ExpectRequestOk("{\"v\":1,\"cmd\":\"show\",\"selector\":null}");

        ExpectBadRequest("");
        ExpectBadRequest("not json");
        ExpectBadRequest("[]");
        ExpectBadRequest("\"launch\"");
        ExpectBadRequest("null");
        ExpectBadRequest("{");
        ExpectBadRequest("{\"v\":1,\"cmd\":\"show\"} trailing");
        ExpectBadRequest("{\"cmd\":\"show\"}");
        ExpectBadRequest("{\"v\":2,\"cmd\":\"show\"}");
        ExpectBadRequest("{\"v\":0,\"cmd\":\"show\"}");
        ExpectBadRequest("{\"v\":\"1\",\"cmd\":\"show\"}");
        ExpectBadRequest("{\"v\":1.5,\"cmd\":\"show\"}");
        ExpectBadRequest("{\"v\":1}");
        ExpectBadRequest("{\"v\":1,\"cmd\":null}");
        ExpectBadRequest("{\"v\":1,\"cmd\":5}");
        ExpectBadRequest("{\"v\":1,\"cmd\":\"run\",\"selector\":\"x\"}");
        ExpectBadRequest("{\"v\":1,\"cmd\":\"LAUNCH\",\"selector\":\"x\"}");
        ExpectBadRequest("{\"v\":1,\"cmd\":\"exit\"}");
        ExpectBadRequest("{\"v\":1,\"cmd\":\"launch\"}");
        ExpectBadRequest("{\"v\":1,\"cmd\":\"launch\",\"selector\":null}");
        ExpectBadRequest("{\"v\":1,\"cmd\":\"launch\",\"selector\":\"   \"}");
        ExpectBadRequest("{\"v\":1,\"cmd\":\"launch\",\"selector\":5}");
        ExpectBadRequest("{\"v\":1,\"cmd\":\"launch\",\"selector\":[\"x\"]}");
        ExpectBadRequest("{\"v\":1,\"cmd\":\"show\",\"selector\":\"x\"}");
        ExpectBadRequest("{\"v\":1,\"v\":1,\"cmd\":\"show\"}");
        ExpectBadRequest("{\"v\":1,\"cmd\":\"launch\",\"cmd\":\"show\",\"selector\":\"x\"}");
        ExpectBadRequest("{\"v\":1,\"cmd\":\"launch\",\"selector\":\"x\",\"path\":\"C:\\\\Windows\\\\System32\\\\cmd.exe\"}");
        ExpectBadRequest("{\"v\":1,\"cmd\":\"launch\",\"selector\":\"x\",\"executablePath\":\"C:\\\\x.exe\"}");
        ExpectBadRequest("{\"v\":1,\"cmd\":\"launch\",\"selector\":\"x\",\"arguments\":\"/c calc\"}");
        ExpectBadRequest("{\"v\":1,\"cmd\":\"launch\",\"selector\":\"x\",\"workingDirectory\":\"C:\\\\\"}");
        ExpectBadRequest("{\"v\":1,\"cmd\":\"launch\",\"selector\":\"x\",\"extra\":{\"a\":{\"b\":{\"c\":{\"d\":1}}}}}");
        ExpectBadRequestBytes(new byte[] { (byte)'{', 0xFF, 0xFE, (byte)'}' }, "invalid UTF-8");
        var overSize = Encoding.UTF8.GetBytes("{\"v\":1,\"cmd\":\"launch\",\"selector\":\"" + new string('a', CommandProtocol.MaxRequestBytes) + "\"}");
        ExpectBadRequestBytes(overSize, "oversized");

        // The request type has no field for a path, arguments or a working directory.
        var properties = typeof(CommandRequest).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(property => property.Name != "EqualityContract")
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert(properties.SequenceEqual(new[] { "Command", "Selector" }), "CommandRequest must only have Command and Selector: " + string.Join(",", properties));
        foreach (var member in typeof(CommandRequest).GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            Assert(!member.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)
                && !member.Name.Contains("Argument", StringComparison.OrdinalIgnoreCase)
                && !member.Name.Contains("Directory", StringComparison.OrdinalIgnoreCase),
                "CommandRequest must not expose a path, argument or directory member: " + member.Name);
        }

        // Replies.
        foreach (var code in Enum.GetValues<CommandReplyCode>())
        {
            var reply = CommandReply.Create(code, "message \"" + code + "\"", code == CommandReplyCode.AlreadyRunning ? 0x1234 : 0);
            Assert(reply.Ok == (code is CommandReplyCode.Started or CommandReplyCode.Shown), $"Ok is wrong for {code}.");
            var bytes = CommandProtocol.SerializeReply(reply);
            var text = Encoding.UTF8.GetString(bytes);
            Assert(text.EndsWith('\n') && text.Contains("\"code\":\"" + code + "\"", StringComparison.Ordinal), $"Reply must be one line with the code name: {text}");
            Assert(CommandProtocol.TryParseReply(bytes.AsSpan(0, bytes.Length - 1), out var parsed, out var error) && parsed == reply, $"Reply {code} must round-trip: {error}");
        }

        ExpectBadReply("{\"ok\":true,\"code\":\"Bogus\",\"message\":\"m\"}");
        ExpectBadReply("{\"ok\":true,\"code\":0,\"message\":\"m\"}");
        ExpectBadReply("{\"ok\":true,\"code\":\"NotFound\",\"message\":\"m\"}");
        ExpectBadReply("{\"ok\":false,\"code\":\"Started\",\"message\":\"m\"}");
        ExpectBadReply("{\"ok\":false,\"code\":\"NotFound\"}");
        ExpectBadReply("{\"code\":\"NotFound\",\"message\":\"m\"}");
        ExpectBadReply("not json");

        var echo = CommandProtocol.Echo(new string('q', 1000));
        Assert(echo.Length <= CommandProtocol.MaxEchoLength + 1, "Echo must truncate long selectors.");
    }

    private static void ExpectRequestOk(string json)
    {
        Assert(CommandProtocol.TryParseRequest(Encoding.UTF8.GetBytes(json), out var request, out var error) && request is not null,
            $"Expected a valid request: {json} ({error})");
    }

    private static void ExpectBadRequest(string json) => ExpectBadRequestBytes(Encoding.UTF8.GetBytes(json), json);

    private static void ExpectBadRequestBytes(byte[] bytes, string description)
    {
        var ok = CommandProtocol.TryParseRequest(bytes, out var request, out var error);
        Assert(!ok && request is null && !string.IsNullOrWhiteSpace(error), $"Expected a BadRequest for: {Shorten(description)}");
    }

    private static void ExpectBadReply(string json)
    {
        Assert(!CommandProtocol.TryParseReply(Encoding.UTF8.GetBytes(json), out var reply, out _) && reply is null, $"Expected an invalid reply: {json}");
    }

    private static string Shorten(string value) => value.Length > 160 ? value[..160] + "..." : value;

    private static async Task TestLineReaderAsync()
    {
        var max = CommandProtocol.MaxRequestBytes;
        var exact = await ReadAsync(new string('a', max) + "\n", max);
        Assert(exact.Status == LineReadStatus.Line && exact.Line.Length == max, "A line of exactly the limit must be accepted.");
        var over = await ReadAsync(new string('a', max + 1) + "\n", max);
        Assert(over.Status == LineReadStatus.TooLong, "A line over the limit must be TooLong.");
        var noNewline = await ReadAsync("{\"v\":1,\"cmd\":\"show\"}", max);
        Assert(noNewline.Status == LineReadStatus.EndOfStream, "A request without a newline must not be accepted.");
        var crlf = await ReadAsync("abc\r\nrest", max);
        Assert(crlf.Status == LineReadStatus.Line && Encoding.UTF8.GetString(crlf.Line) == "abc", "A trailing CR must be stripped.");
    }

    private static async Task<LineReadResult> ReadAsync(string text, int max)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await CommandProtocol.ReadLineAsync(stream, max, CancellationToken.None);
    }

    private static void TestExitCodesAndPresenter()
    {
        var expected = new Dictionary<CommandReplyCode, int>
        {
            [CommandReplyCode.Started] = 0,
            [CommandReplyCode.Shown] = 0,
            [CommandReplyCode.AlreadyRunning] = 1,
            [CommandReplyCode.NotFound] = 1,
            [CommandReplyCode.Ambiguous] = 1,
            [CommandReplyCode.NotLaunchable] = 1,
            [CommandReplyCode.ConfigUnavailable] = 1,
            [CommandReplyCode.Exiting] = 1,
            [CommandReplyCode.BadRequest] = 1,
            [CommandReplyCode.ExternalInstanceRunning] = 1,
        };
        Assert(expected.Count == Enum.GetValues<CommandReplyCode>().Length, "Every reply code needs an expected exit code.");
        foreach (var (code, exitCode) in expected)
        {
            Assert(CommandLineExitCodes.ForReply(code) == exitCode, $"Exit code for {code} must be {exitCode}.");
            var events = new List<string>();
            var result = ForwardResultPresenter.Present(
                new ForwardResult(ForwardStatus.Replied, CommandReply.Create(code, "msg", 0x55), null),
                (message, _) => events.Add("info:" + message),
                (message, _) => events.Add("error:" + message),
                handle => { events.Add("fg:" + handle); return true; });
            Assert(result == exitCode, $"Presenter exit code for {code} must be {exitCode}.");
            var sequence = string.Join(",", events);
            var expectedSequence = code switch
            {
                CommandReplyCode.Started => "",
                CommandReplyCode.Shown => "fg:85",
                CommandReplyCode.AlreadyRunning => "fg:85,info:msg,fg:85",
                _ => "error:msg",
            };
            Assert(sequence == expectedSequence, $"Presenter feedback for {code} must be '{expectedSequence}', got '{sequence}'.");
        }

        var noWindow = new List<string>();
        ForwardResultPresenter.Present(
            new ForwardResult(ForwardStatus.Replied, CommandReply.Create(CommandReplyCode.AlreadyRunning, "msg"), null),
            (message, _) => noWindow.Add("info"),
            (message, _) => noWindow.Add("error"),
            handle => { noWindow.Add("fg"); return true; });
        Assert(string.Join(",", noWindow) == "info", "AlreadyRunning without a tracked window must only show the information box.");

        var unreachable = new List<string>();
        var unreachableCode = ForwardResultPresenter.Present(
            new ForwardResult(ForwardStatus.Unreachable, null, "nobody there"),
            (message, _) => unreachable.Add("info"),
            (message, _) => unreachable.Add("error:" + message),
            handle => { unreachable.Add("fg"); return true; });
        Assert(unreachableCode == 3 && string.Join(",", unreachable) == "error:nobody there", "Unreachable must show an error and return 3.");

        Assert(!TrackedWindowActivator.TryBringToForeground(0), "Handle 0 must never be activated.");
        Assert(!TrackedWindowActivator.TryBringToForeground(0x7FFF_FFF0), "An invalid handle must not be activated.");
    }

    private static async Task TestPipeRoundTripAsync()
    {
        var pipeName = "ProcessGuardian.Test." + Guid.NewGuid().ToString("N");
        var dispatched = new List<CommandRequest>();
        var gate = new object();
        var server = new CommandPipeServer(pipeName, async (request, stopToken) =>
        {
            lock (gate)
            {
                dispatched.Add(request);
            }

            await Task.Yield();
            return request.Selector switch
            {
                "throw" => throw new InvalidOperationException("dispatcher failure (expected by the test)"),
                "wait-for-stop" => await WaitForStopAsync(stopToken),
                "dup" => CommandReply.Create(CommandReplyCode.Ambiguous, "two"),
                _ when request.Command == CommandRequest.ShowCommandName => CommandReply.Create(CommandReplyCode.Shown, "shown", 0x77),
                _ => CommandReply.Create(CommandReplyCode.Started, "started " + request.Selector),
            };
        })
        {
            ReadTimeout = TimeSpan.FromSeconds(1),
        };

        server.Start();
        try
        {
            var started = await CommandPipeClient.SendAsync(pipeName, CommandRequest.Launch("Target"), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
            Assert(started.Status == ForwardStatus.Replied && started.Reply!.Code == CommandReplyCode.Started && started.Reply.Message == "started Target",
                $"Launch round trip failed: {started.Status} {started.Reply?.Code} {started.FailureMessage}");
            var shown = await CommandPipeClient.SendAsync(pipeName, CommandRequest.Show(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
            Assert(shown.Reply?.Code == CommandReplyCode.Shown && shown.Reply.WindowHandle == 0x77, "Show round trip failed.");
            var ambiguous = await CommandPipeClient.SendAsync(pipeName, CommandRequest.Launch("dup"), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
            Assert(ambiguous.Reply?.Code == CommandReplyCode.Ambiguous && !ambiguous.Reply.Ok, "Refusal round trip failed.");
            var thrown = await CommandPipeClient.SendAsync(pipeName, CommandRequest.Launch("throw"), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
            Assert(thrown.Reply?.Code == CommandReplyCode.NotLaunchable, "A dispatcher exception must be reported as NotLaunchable.");

            var dispatchedBefore = DispatchedCount();
            await ExpectRawBadRequestAsync(pipeName, Encoding.UTF8.GetBytes("not json\n"), "malformed");
            await ExpectRawBadRequestAsync(pipeName, Encoding.UTF8.GetBytes("{\"v\":9,\"cmd\":\"show\"}\n"), "unknown version");
            await ExpectRawBadRequestAsync(pipeName, Encoding.UTF8.GetBytes("{\"v\":1,\"cmd\":\"run\",\"selector\":\"x\"}\n"), "unknown cmd");
            await ExpectRawBadRequestAsync(pipeName, Encoding.UTF8.GetBytes("{\"v\":1,\"cmd\":\"launch\",\"selector\":\"x\",\"path\":\"C:\\\\x.exe\"}\n"), "path field");
            await ExpectRawBadRequestAsync(pipeName, Encoding.UTF8.GetBytes(new string('a', 5000) + "\n"), "oversized");
            await ExpectRawBadRequestAsync(pipeName, Array.Empty<byte>(), "read timeout (no data)");
            Assert(DispatchedCount() == dispatchedBefore, "Bad requests must never reach the dispatcher.");

            // A request still being handled when the server stops is answered with Exiting.
            var inFlight = CommandPipeClient.SendAsync(pipeName, CommandRequest.Launch("wait-for-stop"), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!Dispatched("wait-for-stop") && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert(Dispatched("wait-for-stop"), "The in-flight request was not dispatched.");
            server.BeginStop();
            var exiting = await inFlight;
            Assert(exiting.Reply?.Code == CommandReplyCode.Exiting, $"An in-flight request must get Exiting, got {exiting.Reply?.Code} {exiting.FailureMessage}.");
            await server.StopAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            server.BeginStop();
            await server.StopAsync(TimeSpan.FromSeconds(5));
        }

        var afterStop = await CommandPipeClient.SendAsync(pipeName, CommandRequest.Show(), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        Assert(afterStop.Status == ForwardStatus.Unreachable, "A stopped server must be unreachable.");
        var exitCode = ForwardResultPresenter.Present(afterStop, (_, _) => { }, (_, _) => { }, _ => true);
        Assert(exitCode == CommandLineExitCodes.Unreachable, "Unreachable must map to exit code 3.");

        int DispatchedCount()
        {
            lock (gate)
            {
                return dispatched.Count;
            }
        }

        bool Dispatched(string selector)
        {
            lock (gate)
            {
                return dispatched.Any(request => request.Selector == selector);
            }
        }
    }

    // The forwarder hands foreground rights to the PID owning the server end of the connected pipe. In-process the
    // server end is ours, so the lookup must resolve to this PID (SameProcess) and nothing is granted.
    private static async Task TestForegroundGrantPidLookupAsync()
    {
        var pipeName = "ProcessGuardian.Test.Grant." + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await accept.WaitAsync(TimeSpan.FromSeconds(5));
        var result = CommandPipeClient.GrantForegroundRightsToServer(client);
        Assert(result == ForegroundGrantResult.SameProcess,
            $"GetNamedPipeServerProcessId on a connected client must resolve the server PID (expected SameProcess, got {result}).");
        Console.WriteLine("PASS: forwarding client resolves the pipe server PID for the foreground hand-off (in-process: SameProcess, nothing granted)");
    }

    private static async Task<CommandReply> WaitForStopAsync(CancellationToken stopToken)
    {
        await Task.Delay(Timeout.Infinite, stopToken);
        throw new InvalidOperationException("unreachable");
    }

    private static async Task ExpectRawBadRequestAsync(string pipeName, byte[] payload, string description)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        // Written concurrently with the read: for an oversized payload the server answers before it has read
        // everything, so the write may only complete (or fail) after the reply.
        var write = payload.Length == 0 ? Task.CompletedTask : WriteIgnoringBrokenPipeAsync(client, payload);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var line = await CommandProtocol.ReadLineAsync(client, CommandProtocol.MaxReplyBytes, timeout.Token);
        Assert(line.Status == LineReadStatus.Line, $"No reply line for {description}.");
        Assert(CommandProtocol.TryParseReply(line.Line, out var reply, out var error) && reply!.Code == CommandReplyCode.BadRequest,
            $"Expected BadRequest for {description}, got {reply?.Code} ({error}).");
        await write.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task WriteIgnoringBrokenPipeAsync(Stream stream, byte[] payload)
    {
        try
        {
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }
        catch (IOException)
        {
            // Expected only when the server has already answered and closed the pipe (oversized request).
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
