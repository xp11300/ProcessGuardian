using System.Diagnostics;
using System.IO;
using System.Text;

namespace ProcessGuardian.Services.Logging;

/// <summary>
/// Last-resort plain-text record for a fatal error that could not be confirmed in the normal log (logger missing,
/// disposed, or its write failed). With an absolute PROCESSGUARDIAN_DATA_DIR the file is written to that folder
/// first, so isolated test runs never write elsewhere; otherwise (or if that fails) it goes to %TEMP%. The
/// Windows Event Log is not used because registering an event source requires administrator rights.
/// </summary>
internal static class FallbackLog
{
    internal const string FilePrefix = "ProcessGuardian-fallback-";

    internal static IReadOnlyList<string> GetCandidateDirectories()
    {
        var directories = new List<string>();
        var overrideDirectory = Environment.GetEnvironmentVariable(AppDataPaths.OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideDirectory) && Path.IsPathFullyQualified(overrideDirectory))
        {
            directories.Add(Path.GetFullPath(overrideDirectory));
        }

        directories.Add(Path.GetTempPath());
        return directories;
    }

    /// <summary>Writes the record to the first directory that accepts it.</summary>
    /// <returns>The file path, or null when no location could be written (the failure is reported through Trace).</returns>
    internal static string? Write(string context, Exception? exception, IReadOnlyList<string>? directories = null)
    {
        var now = DateTimeOffset.Now;
        var text = new StringBuilder()
            .Append(now.ToString("O")).Append(" Critical Application ").AppendLine(context)
            .Append("Process ").Append(Environment.ProcessId)
            .Append(", Process Guardian ").AppendLine(typeof(FallbackLog).Assembly.GetName().Version?.ToString())
            .AppendLine(exception?.ToString() ?? "(no exception object)")
            .AppendLine()
            .ToString();
        var fileName = $"{FilePrefix}{now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log";
        foreach (var directory in directories ?? GetCandidateDirectories())
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, fileName);
                File.AppendAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return path;
            }
            catch (Exception writeException)
            {
                Trace.WriteLine($"Process Guardian fallback log could not be written to {directory}: {writeException}");
            }
        }

        Trace.WriteLine("Process Guardian fallback log could not be written anywhere. Record: " + text);
        return null;
    }
}
