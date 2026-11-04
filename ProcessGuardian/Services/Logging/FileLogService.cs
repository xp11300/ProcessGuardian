using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.IO;

namespace ProcessGuardian.Services.Logging;

/// <summary>Non-blocking structured JSON-lines logger. File access is serialized by one background worker.</summary>
public sealed class FileLogService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly Channel<QueueItem> _entries = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(5000)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly Task _worker;
    private readonly long _maximumFileBytes;
    private readonly TimeSpan _retention;
    private Exception? _lastWriteException;
    private int _disposed;
    private long _writeFailureCount;

    public FileLogService(string? logDirectory = null, long maximumFileBytes = 5 * 1024 * 1024, TimeSpan? retention = null)
    {
        if (maximumFileBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        LogDirectory = logDirectory ?? Path.Combine(AppDataPaths.GetDataDirectory(), "Logs");
        _maximumFileBytes = maximumFileBytes;
        _retention = retention ?? TimeSpan.FromDays(14);
        _worker = Task.Run(ConsumeAsync);
        _ = Task.Run(CleanupExpiredFilesSafeAsync);
    }

    public string LogDirectory { get; }
    public Exception? LastWriteException => Volatile.Read(ref _lastWriteException);

    /// <summary>Number of log entries that could not be written (queue refused or file write failed). Lets a caller
    /// confirm its own entry reached disk: compare the value before Write with the value after FlushAsync.</summary>
    public long WriteFailureCount => Interlocked.Read(ref _writeFailureCount);

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Write(LogLevel level, string module, string message, string? applicationName = null, int? processId = null, Exception? exception = null)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var entry = new LogEntry(DateTimeOffset.Now, level, module, message, applicationName, processId, exception?.ToString());
        if (!_entries.Writer.TryWrite(new QueueItem(entry, null)))
        {
            Interlocked.Increment(ref _writeFailureCount);
            Volatile.Write(ref _lastWriteException, new IOException("The log queue is not accepting entries."));
        }
    }

    public async Task<IReadOnlyList<LogEntry>> ReadRecentAsync(int limit = 1000, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 10000);
        string[] paths;
        try
        {
            paths = Directory.Exists(LogDirectory)
                ? Directory.EnumerateFiles(LogDirectory, "processguardian-*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(32).ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Volatile.Write(ref _lastWriteException, exception);
            return [];
        }
        var lines = new Queue<string>(limit);
        foreach (var path in paths.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (lines.Count == limit) lines.Dequeue();
                    lines.Enqueue(line);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return lines.Select(line => { try { return JsonSerializer.Deserialize<LogEntry>(line, JsonOptions); } catch (JsonException) { return null; } })
            .Where(entry => entry is not null).Cast<LogEntry>().OrderByDescending(entry => entry.Timestamp).ToArray();
    }

    /// <summary>Removes existing log files only after queued records are flushed; subsequent writes create a fresh file.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _entries.Writer.WriteAsync(new QueueItem(null, completion, Clear: true), cancellationToken);
        await completion.Task.WaitAsync(cancellationToken);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) { await _worker.WaitAsync(cancellationToken); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await _entries.Writer.WriteAsync(new QueueItem(null, completion), cancellationToken); }
        catch (ChannelClosedException) { return; }
        await completion.Task.WaitAsync(cancellationToken);
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var item in _entries.Reader.ReadAllAsync())
            {
                var entry = item.Entry;
                if (entry is null)
                {
                    if (item.Clear)
                    {
                        try
                        {
                            if (Directory.Exists(LogDirectory))
                            {
                                foreach (var path in Directory.EnumerateFiles(LogDirectory, "processguardian-*.log"))
                                {
                                    try { File.Delete(path); }
                                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Volatile.Write(ref _lastWriteException, exception); }
                                }
                            }
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Volatile.Write(ref _lastWriteException, exception); }
                    }
                    item.Completion?.TrySetResult();
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(LogDirectory);
                    var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
                    var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                    var path = GetAvailableFile(entry.Timestamp);
                    await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                    Volatile.Write(ref _lastWriteException, null);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    Interlocked.Increment(ref _writeFailureCount);
                    Volatile.Write(ref _lastWriteException, exception);
                }
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _writeFailureCount);
            Volatile.Write(ref _lastWriteException, exception);
        }
    }

    private string GetAvailableFile(DateTimeOffset timestamp)
    {
        var stem = $"processguardian-{timestamp:yyyy-MM-dd}";
        for (var index = 0; ; index++)
        {
            var path = Path.Combine(LogDirectory, index == 0 ? stem + ".log" : $"{stem}-{index}.log");
            if (!File.Exists(path) || new FileInfo(path).Length < _maximumFileBytes) return path;
        }
    }

    private Task CleanupExpiredFilesSafeAsync() => Task.Run(() =>
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;
            var cutoff = DateTime.UtcNow - _retention;
            foreach (var path in Directory.EnumerateFiles(LogDirectory, "processguardian-*.log"))
            {
                if (Path.GetFileName(path).StartsWith($"processguardian-{DateTime.Now:yyyy-MM-dd}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try { if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Volatile.Write(ref _lastWriteException, exception); }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Volatile.Write(ref _lastWriteException, exception); }
    });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { await _worker; return; }
        _entries.Writer.TryComplete();
        await _worker;
    }

    private sealed record QueueItem(LogEntry? Entry, TaskCompletionSource? Completion, bool Clear = false);
}
