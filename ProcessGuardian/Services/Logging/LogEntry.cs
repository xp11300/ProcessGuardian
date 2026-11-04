namespace ProcessGuardian.Services.Logging;

public enum LogLevel { Trace, Debug, Information, Warning, Error, Critical }

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Module,
    string Message,
    string? ApplicationName = null,
    int? ProcessId = null,
    string? Exception = null);
