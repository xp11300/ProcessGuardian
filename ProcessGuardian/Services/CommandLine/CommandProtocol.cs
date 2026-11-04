using System.IO;
using System.Text;
using System.Text.Json;

namespace ProcessGuardian.Services.CommandLine;

internal enum CommandReplyCode
{
    Started,
    AlreadyRunning,
    NotFound,
    Ambiguous,
    NotLaunchable,
    ConfigUnavailable,
    Exiting,
    BadRequest,
    Shown,
    ExternalInstanceRunning,
}

/// <summary>
/// A forwarded request. It deliberately carries only a command and a selector: no executable path, arguments or
/// working directory can be sent, so a second instance can only ask for an entry that is already configured.
/// </summary>
internal sealed record CommandRequest(string Command, string? Selector)
{
    internal const string LaunchCommandName = "launch";
    internal const string ShowCommandName = "show";

    internal static CommandRequest Launch(string selector) => new(LaunchCommandName, selector);

    internal static CommandRequest Show() => new(ShowCommandName, null);
}

/// <summary>Reply to a request. <see cref="WindowHandle"/> is an HWND the client may bring forward (0 = none).</summary>
internal sealed record CommandReply(bool Ok, CommandReplyCode Code, string Message, long WindowHandle = 0)
{
    internal static CommandReply Create(CommandReplyCode code, string message, long windowHandle = 0) =>
        new(code is CommandReplyCode.Started or CommandReplyCode.Shown, code, message, windowHandle);
}

internal enum LineReadStatus
{
    Line,
    TooLong,
    EndOfStream,
}

internal readonly record struct LineReadResult(LineReadStatus Status, byte[] Line);

/// <summary>
/// Wire format: one UTF-8 JSON object per line, LF-terminated. Request (at most 4096 bytes before the LF):
/// <c>{"v":1,"cmd":"launch"|"show","selector":"..."}</c>. Reply:
/// <c>{"ok":bool,"code":"...","message":"...","windowHandle":n}</c> (windowHandle optional).
/// Request parsing is strict: unknown fields, duplicates, wrong types, an unknown version or command are rejected.
/// </summary>
internal static class CommandProtocol
{
    internal const int Version = 1;
    internal const int MaxRequestBytes = 4096;
    internal const int MaxReplyBytes = 16384;
    internal const int MaxEchoLength = 200;

    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static byte[] SerializeRequest(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteString("cmd", request.Command);
            if (request.Selector is not null)
            {
                writer.WriteString("selector", request.Selector);
            }

            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    internal static byte[] SerializeReply(CommandReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", reply.Ok);
            writer.WriteString("code", reply.Code.ToString());
            writer.WriteString("message", reply.Message);
            if (reply.WindowHandle != 0)
            {
                writer.WriteNumber("windowHandle", reply.WindowHandle);
            }

            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    internal static bool TryParseRequest(ReadOnlySpan<byte> line, out CommandRequest? request, out string error)
    {
        request = null;
        if (line.Length > MaxRequestBytes)
        {
            error = $"request is longer than {MaxRequestBytes} bytes";
            return false;
        }

        try
        {
            StrictUtf8.GetCharCount(line);
        }
        catch (DecoderFallbackException)
        {
            error = "request is not valid UTF-8";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "request is not a JSON object";
                return false;
            }

            int? version = null;
            string? command = null;
            string? selector = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    error = $"duplicate field '{Echo(property.Name)}'";
                    return false;
                }

                switch (property.Name)
                {
                    case "v":
                        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var parsedVersion))
                        {
                            error = "field 'v' must be an integer";
                            return false;
                        }

                        version = parsedVersion;
                        break;
                    case "cmd":
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            error = "field 'cmd' must be a string";
                            return false;
                        }

                        command = property.Value.GetString();
                        break;
                    case "selector":
                        if (property.Value.ValueKind == JsonValueKind.Null)
                        {
                            selector = null;
                        }
                        else if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            selector = property.Value.GetString();
                        }
                        else
                        {
                            error = "field 'selector' must be a string";
                            return false;
                        }

                        break;
                    default:
                        error = $"unknown field '{Echo(property.Name)}'";
                        return false;
                }
            }

            if (version is null)
            {
                error = "field 'v' is missing";
                return false;
            }

            if (version != Version)
            {
                error = $"unsupported protocol version {version}";
                return false;
            }

            switch (command)
            {
                case CommandRequest.LaunchCommandName:
                    if (string.IsNullOrWhiteSpace(selector))
                    {
                        error = "'launch' needs a non-empty selector";
                        return false;
                    }

                    request = CommandRequest.Launch(selector);
                    error = string.Empty;
                    return true;
                case CommandRequest.ShowCommandName:
                    if (selector is not null)
                    {
                        error = "'show' does not take a selector";
                        return false;
                    }

                    request = CommandRequest.Show();
                    error = string.Empty;
                    return true;
                case null:
                    error = "field 'cmd' is missing";
                    return false;
                default:
                    error = $"unknown command '{Echo(command)}'";
                    return false;
            }
        }
        catch (JsonException exception)
        {
            error = "request is not valid JSON: " + exception.Message;
            return false;
        }
    }

    internal static bool TryParseReply(ReadOnlySpan<byte> line, out CommandReply? reply, out string error)
    {
        reply = null;
        try
        {
            using var document = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String)
            {
                error = "reply is missing 'ok', 'code' or 'message'";
                return false;
            }

            var codeText = code.GetString() ?? string.Empty;
            if (!Enum.GetNames<CommandReplyCode>().Contains(codeText, StringComparer.Ordinal))
            {
                error = $"reply has an unknown code '{Echo(codeText)}'";
                return false;
            }

            var parsedCode = Enum.Parse<CommandReplyCode>(codeText);
            long windowHandle = 0;
            if (root.TryGetProperty("windowHandle", out var handle)
                && (handle.ValueKind != JsonValueKind.Number || !handle.TryGetInt64(out windowHandle)))
            {
                error = "reply field 'windowHandle' must be an integer";
                return false;
            }

            var okValue = ok.GetBoolean();
            if (okValue != (parsedCode is CommandReplyCode.Started or CommandReplyCode.Shown))
            {
                error = $"reply 'ok' does not match code {codeText}";
                return false;
            }

            reply = new CommandReply(okValue, parsedCode, message.GetString() ?? string.Empty, windowHandle);
            error = string.Empty;
            return true;
        }
        catch (JsonException exception)
        {
            error = "reply is not valid JSON: " + exception.Message;
            return false;
        }
    }

    /// <summary>Reads one LF-terminated line (a trailing CR is dropped) of at most <paramref name="maxBytes"/> bytes.</summary>
    internal static async Task<LineReadResult> ReadLineAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[maxBytes + 1];
        var count = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, buffer.Length - count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return new LineReadResult(LineReadStatus.EndOfStream, buffer[..count]);
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', count, read);
            count += read;
            if (newline >= 0)
            {
                var length = newline;
                if (length > maxBytes)
                {
                    return new LineReadResult(LineReadStatus.TooLong, Array.Empty<byte>());
                }

                if (length > 0 && buffer[length - 1] == (byte)'\r')
                {
                    length--;
                }

                return new LineReadResult(LineReadStatus.Line, buffer[..length]);
            }

            if (count > maxBytes)
            {
                return new LineReadResult(LineReadStatus.TooLong, Array.Empty<byte>());
            }
        }
    }

    /// <summary>Shortens user-supplied text before it is echoed into a message or log line.</summary>
    internal static string Echo(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return value.Length > MaxEchoLength ? value[..MaxEchoLength] + "…" : value;
    }
}
