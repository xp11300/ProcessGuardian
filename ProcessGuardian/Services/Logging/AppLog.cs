namespace ProcessGuardian.Services.Logging;

/// <summary>Small process-wide access point; file I/O remains isolated on FileLogService's worker.</summary>
public static class AppLog
{
    private static FileLogService? _current;
    public static FileLogService? Current => Volatile.Read(ref _current);
    public static void Initialize(FileLogService service) => Interlocked.Exchange(ref _current, service);
    /// <summary>Test seam: swaps the process-wide logger (null allowed) and returns the previous one.</summary>
    internal static FileLogService? Replace(FileLogService? service) => Interlocked.Exchange(ref _current, service);
    public static void Write(LogLevel level, string module, string message, string? applicationName = null, int? processId = null, Exception? exception = null) =>
        Current?.Write(level, module, message, applicationName, processId, exception);
}
