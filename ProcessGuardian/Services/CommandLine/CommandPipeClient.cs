using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using ProcessGuardian.Interop;

namespace ProcessGuardian.Services.CommandLine;

internal enum ForwardStatus
{
    Replied,
    Unreachable,
}

/// <summary>Outcome of handing foreground rights to the pipe server before a request is sent.</summary>
internal enum ForegroundGrantResult
{
    /// <summary>AllowSetForegroundWindow succeeded for the server's PID.</summary>
    Granted,

    /// <summary>
    /// AllowSetForegroundWindow failed, normally because this process has no foreground rights to give
    /// (for example it was started from a background shell). The request is still sent unchanged.
    /// </summary>
    NotGranted,

    /// <summary>GetNamedPipeServerProcessId failed or returned 0; nothing was granted.</summary>
    ServerProcessIdUnavailable,

    /// <summary>The server end belongs to this process (in-process tests); nothing to grant.</summary>
    SameProcess,
}

internal sealed record ForwardResult(ForwardStatus Status, CommandReply? Reply, string? FailureMessage);

/// <summary>
/// Client side used by a second instance. It connects with PipeOptions.CurrentUserOnly (the server must belong to
/// the same user), retrying for up to <see cref="ConnectTimeout"/>, sends exactly one request and waits for one reply.
/// It never touches files.
/// </summary>
internal static class CommandPipeClient
{
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMilliseconds(250);

    internal static async Task<ForwardResult> SendAsync(string pipeName, CommandRequest request, TimeSpan connectTimeout, TimeSpan replyTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(request);
        var payload = CommandProtocol.SerializeRequest(request);
        var connectWatch = Stopwatch.StartNew();
        Exception? lastConnectError = null;
        NamedPipeClientStream? client = null;
        while (client is null)
        {
            var remaining = connectTimeout - connectWatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return Unreachable(
                    $"Process Guardian is running for this data folder but did not accept the request within {connectTimeout.TotalSeconds:0} seconds."
                    + (lastConnectError is null ? string.Empty : $" ({lastConnectError.GetType().Name}: {lastConnectError.Message})"));
            }

            var candidate = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await candidate.ConnectAsync((int)Math.Min(remaining.TotalMilliseconds, 1000), CancellationToken.None).ConfigureAwait(false);
                client = candidate;
            }
            catch (TimeoutException exception)
            {
                lastConnectError = exception;
                await candidate.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                // All instances busy (another forwarder is being served) or a transient pipe error: retry until the deadline.
                lastConnectError = exception;
                await candidate.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(BusyRetryDelay).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException exception)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                return Unreachable($"The Process Guardian command pipe is not owned by the current user and was not used ({exception.Message}).");
            }
        }

        await using (client.ConfigureAwait(false))
        {
            // Foreground hand-off: Windows only lets a process that currently has foreground rights (this forwarder,
            // when the user double-clicked it or used a shortcut) bring a window forward. Give that right to the
            // running instance - and only to it, identified by the PID owning the server end of this connected
            // pipe - before asking it to show or launch, so its own ShowMainWindow activation is allowed.
            // Best effort by design: if it cannot be granted, the request is still sent and handled exactly as before.
            _ = GrantForegroundRightsToServer(client);

            using var timeout = new CancellationTokenSource(replyTimeout);
            try
            {
                await client.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
                await client.FlushAsync(timeout.Token).ConfigureAwait(false);
                var line = await CommandProtocol.ReadLineAsync(client, CommandProtocol.MaxReplyBytes, timeout.Token).ConfigureAwait(false);
                if (line.Status != LineReadStatus.Line)
                {
                    return Unreachable($"Process Guardian closed the connection without a complete reply ({line.Status}).");
                }

                if (!CommandProtocol.TryParseReply(line.Line, out var reply, out var error))
                {
                    return Unreachable("Process Guardian sent an invalid reply: " + error + ".");
                }

                return new ForwardResult(ForwardStatus.Replied, reply, null);
            }
            catch (OperationCanceledException)
            {
                return Unreachable($"Process Guardian did not reply within {replyTimeout.TotalSeconds:0} seconds.");
            }
            catch (IOException exception)
            {
                return Unreachable($"The connection to Process Guardian failed: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Calls AllowSetForegroundWindow for the specific PID that owns the server end of <paramref name="connectedClient"/>
    /// (never ASFW_ANY). The pipe was opened with CurrentUserOnly, so that PID is a same-user process.
    /// </summary>
    internal static ForegroundGrantResult GrantForegroundRightsToServer(NamedPipeClientStream connectedClient)
    {
        ArgumentNullException.ThrowIfNull(connectedClient);
        if (!ForegroundWindowNativeMethods.GetNamedPipeServerProcessId(connectedClient.SafePipeHandle, out var serverProcessId)
            || serverProcessId == 0)
        {
            return ForegroundGrantResult.ServerProcessIdUnavailable;
        }

        if (serverProcessId == (uint)Environment.ProcessId)
        {
            return ForegroundGrantResult.SameProcess;
        }

        return ForegroundWindowNativeMethods.AllowSetForegroundWindow(serverProcessId)
            ? ForegroundGrantResult.Granted
            : ForegroundGrantResult.NotGranted;
    }

    private static ForwardResult Unreachable(string message) => new(ForwardStatus.Unreachable, null, message);
}

/// <summary>Exit codes of a forwarding (second) instance and of a usage error.</summary>
internal static class CommandLineExitCodes
{
    internal const int Success = 0;
    internal const int Refused = 1;
    internal const int UsageError = 2;
    internal const int Unreachable = 3;

    internal static int ForReply(CommandReplyCode code) =>
        code is CommandReplyCode.Started or CommandReplyCode.Shown ? Success : Refused;
}

/// <summary>
/// What a forwarding instance does with the result: silent on Started/Shown, an information box on AlreadyRunning,
/// an error box otherwise. For AlreadyRunning the tracked window is brought forward before the box and again after
/// it is dismissed (the box would otherwise cover it); each attempt re-validates the handle.
/// </summary>
internal static class ForwardResultPresenter
{
    internal const string Title = "Process Guardian";

    internal static int Present(
        ForwardResult result,
        Action<string, string> showInformation,
        Action<string, string> showError,
        Func<long, bool> bringToForeground)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status == ForwardStatus.Unreachable || result.Reply is null)
        {
            showError(result.FailureMessage ?? "Process Guardian could not be reached.", Title);
            return CommandLineExitCodes.Unreachable;
        }

        var reply = result.Reply;
        switch (reply.Code)
        {
            case CommandReplyCode.Started:
                break;
            case CommandReplyCode.Shown:
                if (reply.WindowHandle != 0)
                {
                    bringToForeground(reply.WindowHandle);
                }

                break;
            case CommandReplyCode.AlreadyRunning:
                if (reply.WindowHandle != 0)
                {
                    bringToForeground(reply.WindowHandle);
                }

                showInformation(reply.Message, Title);
                if (reply.WindowHandle != 0)
                {
                    bringToForeground(reply.WindowHandle);
                }

                break;
            default:
                showError(reply.Message, Title + " - " + reply.Code);
                break;
        }

        return CommandLineExitCodes.ForReply(reply.Code);
    }
}
