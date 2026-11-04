using System.IO;
using System.IO.Pipes;
using ProcessGuardian.Services.Logging;

namespace ProcessGuardian.Services.CommandLine;

/// <summary>
/// Named-pipe server of the first instance. One connection and one request at a time (maxInstances 1); the pipe is
/// created with PipeOptions.CurrentUserOnly. Every request is logged (module "CommandLine"): Information when it
/// succeeded, Warning when it was refused. Requests that arrive after <see cref="BeginStop"/> get Exiting.
/// </summary>
internal sealed class CommandPipeServer
{
    internal const string LogModule = "CommandLine";

    private static readonly TimeSpan CreateRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReplyWriteTimeout = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly Func<CommandRequest, CancellationToken, Task<CommandReply>> _dispatch;
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private int _started;

    internal CommandPipeServer(string pipeName, Func<CommandRequest, CancellationToken, Task<CommandReply>> dispatch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    internal TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal bool IsStopping => _stop.IsCancellationRequested;

    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The command pipe server was already started.");
        }

        var token = _stop.Token;
        _loop = Task.Run(() => RunAsync(token));
    }

    /// <summary>Stops accepting new connections and cancels a pending read. Idempotent and thread-safe.</summary>
    internal void BeginStop()
    {
        lock (_stop)
        {
            if (!_stop.IsCancellationRequested)
            {
                _stop.Cancel();
            }
        }
    }

    /// <summary>Stops the server and waits (bounded) for the accept loop to end. Throws TimeoutException if it does not.</summary>
    internal async Task StopAsync(TimeSpan timeout)
    {
        BeginStop();
        await _loop.WaitAsync(timeout).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken stopToken)
    {
        var creationFailureLogged = false;
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                }
                catch (IOException exception)
                {
                    if (!creationFailureLogged)
                    {
                        AppLog.Write(LogLevel.Error, LogModule, "The command pipe could not be created; forwarded requests cannot be received until it is. Retrying.", exception: exception);
                        creationFailureLogged = true;
                    }

                    await Task.Delay(CreateRetryDelay, stopToken).ConfigureAwait(false);
                    continue;
                }

                if (creationFailureLogged)
                {
                    AppLog.Write(LogLevel.Information, LogModule, "The command pipe was created after an earlier failure");
                    creationFailureLogged = false;
                }

                await using (server.ConfigureAwait(false))
                {
                    await server.WaitForConnectionAsync(stopToken).ConfigureAwait(false);
                    await HandleConnectionAsync(server, stopToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            // Normal stop: exit has begun.
        }
        catch (Exception exception)
        {
            AppLog.Write(LogLevel.Error, LogModule, "The command pipe server stopped unexpectedly; later starts with --launch cannot reach this instance", exception: exception);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken stopToken)
    {
        CommandRequest? request = null;
        CommandReply reply;
        using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(stopToken))
        {
            readTimeout.CancelAfter(ReadTimeout);
            LineReadResult line;
            try
            {
                line = await CommandProtocol.ReadLineAsync(server, CommandProtocol.MaxRequestBytes, readTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                line = default;
            }
            catch (OperationCanceledException)
            {
                line = new LineReadResult(LineReadStatus.EndOfStream, Array.Empty<byte>());
                reply = CommandReply.Create(CommandReplyCode.BadRequest, $"No complete request was received within {ReadTimeout.TotalSeconds:0} seconds.");
                LogRequest(null, reply);
                await WriteReplyAsync(server, reply).ConfigureAwait(false);
                return;
            }
            catch (IOException exception)
            {
                AppLog.Write(LogLevel.Warning, LogModule, "A command pipe client disconnected before sending a request", exception: exception);
                return;
            }

            if (stopToken.IsCancellationRequested)
            {
                reply = CommandReply.Create(CommandReplyCode.Exiting, "Process Guardian is exiting; the request was not processed.");
            }
            else if (line.Status == LineReadStatus.TooLong)
            {
                reply = CommandReply.Create(CommandReplyCode.BadRequest, $"The request is longer than {CommandProtocol.MaxRequestBytes} bytes.");
            }
            else if (line.Status == LineReadStatus.EndOfStream)
            {
                reply = CommandReply.Create(CommandReplyCode.BadRequest, "The request was not terminated by a line feed.");
            }
            else if (!CommandProtocol.TryParseRequest(line.Line, out request, out var error))
            {
                reply = CommandReply.Create(CommandReplyCode.BadRequest, "Bad request: " + error + ".");
            }
            else
            {
                reply = await DispatchAsync(request!, stopToken).ConfigureAwait(false);
            }
        }

        LogRequest(request, reply);
        await WriteReplyAsync(server, reply).ConfigureAwait(false);
    }

    private async Task<CommandReply> DispatchAsync(CommandRequest request, CancellationToken stopToken)
    {
        try
        {
            return await _dispatch(request, stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            return CommandReply.Create(CommandReplyCode.Exiting, "Process Guardian is exiting; the request was not processed.");
        }
        catch (Exception exception)
        {
            AppLog.Write(LogLevel.Error, LogModule, "A forwarded command failed inside Process Guardian", exception: exception);
            return CommandReply.Create(
                CommandReplyCode.NotLaunchable,
                $"Process Guardian could not handle the request: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void LogRequest(CommandRequest? request, CommandReply reply)
    {
        var what = request is null
            ? "unparsed request"
            : request.Selector is null
                ? $"'{request.Command}'"
                : $"'{request.Command}' selector \"{CommandProtocol.Echo(request.Selector)}\"";
        AppLog.Write(
            reply.Ok ? LogLevel.Information : LogLevel.Warning,
            LogModule,
            $"Forwarded {what}: {reply.Code} - {reply.Message}");
    }

    private static async Task WriteReplyAsync(NamedPipeServerStream server, CommandReply reply)
    {
        using var timeout = new CancellationTokenSource(ReplyWriteTimeout);
        try
        {
            var bytes = CommandProtocol.SerializeReply(reply);
            await server.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await server.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            AppLog.Write(LogLevel.Warning, LogModule, $"The reply ({reply.Code}) could not be sent to the command pipe client", exception: exception);
        }
    }
}
