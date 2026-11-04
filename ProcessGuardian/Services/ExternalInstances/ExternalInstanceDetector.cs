using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ProcessGuardian.Services.Logging;

namespace ProcessGuardian.Services.ExternalInstances;

/// <summary>
/// One entry of a process snapshot used by <see cref="ExternalInstanceDetector"/>.
/// </summary>
public readonly record struct ProcessSnapshotEntry(uint ProcessId, int SessionId, string ProcessName);

/// <summary>
/// Result of looking for instances of an executable that run outside Process Guardian.
/// </summary>
public sealed record ExternalInstanceCheckResult(IReadOnlyList<uint> ProcessIds, int UnreadableCandidateCount, Exception? Error)
{
    public static ExternalInstanceCheckResult None { get; } = new(Array.Empty<uint>(), 0, null);

    public bool Found => ProcessIds.Count > 0;
}

/// <summary>
/// Read-only check that runs before a launch: is the same executable (same normalized full path) already running in
/// this session, outside the processes Process Guardian manages? It never opens a process with more than
/// PROCESS_QUERY_LIMITED_INFORMATION, never changes a process, and never throws: on an unexpected failure it logs and
/// reports "not found", so the launch behaves exactly as it did before this check existed.
/// </summary>
public interface IExternalInstanceDetector
{
    ExternalInstanceCheckResult Find(string executablePath, IReadOnlyCollection<uint> excludedProcessIds);
}

public sealed class ExternalInstanceDetector : IExternalInstanceDetector
{
    internal const string LogModule = "ExternalInstance";
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private readonly Func<IEnumerable<ProcessSnapshotEntry>> _enumerateProcesses;
    private readonly Func<uint, string?> _queryImagePath;
    private readonly int _sessionId;
    private readonly IReadOnlyCollection<uint> _alwaysExcluded;

    /// <param name="enumerateProcesses">Process snapshot; defaults to <see cref="Process.GetProcesses()"/>.</param>
    /// <param name="queryImagePath">Full image path of a PID, or null / an exception when it cannot be read;
    /// defaults to QueryFullProcessImageNameW with PROCESS_QUERY_LIMITED_INFORMATION.</param>
    /// <param name="sessionId">Session to search; defaults to the session of this process.</param>
    /// <param name="alwaysExcludedProcessIds">PIDs never reported; defaults to this process and the process that
    /// started it (see <see cref="GetDefaultExcludedProcessIds"/>).</param>
    public ExternalInstanceDetector(
        Func<IEnumerable<ProcessSnapshotEntry>>? enumerateProcesses = null,
        Func<uint, string?>? queryImagePath = null,
        int? sessionId = null,
        IReadOnlyCollection<uint>? alwaysExcludedProcessIds = null)
    {
        _enumerateProcesses = enumerateProcesses ?? EnumerateSystemProcesses;
        _queryImagePath = queryImagePath ?? QueryImagePath;
        _sessionId = sessionId ?? GetCurrentSessionId();
        _alwaysExcluded = alwaysExcludedProcessIds ?? GetDefaultExcludedProcessIds();
    }

    public ExternalInstanceCheckResult Find(string executablePath, IReadOnlyCollection<uint> excludedProcessIds)
    {
        try
        {
            var target = NormalizePath(executablePath);
            if (target is null)
            {
                return ExternalInstanceCheckResult.None;
            }

            var fileName = Path.GetFileName(target);
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(target);
            var found = new List<uint>();
            var unreadable = 0;
            foreach (var entry in _enumerateProcesses())
            {
                if (entry.ProcessId == 0 || entry.SessionId != _sessionId
                    || _alwaysExcluded.Contains(entry.ProcessId) || excludedProcessIds.Contains(entry.ProcessId))
                {
                    continue;
                }

                // Cheap pre-filter on the image name from the snapshot; only candidates are opened.
                if (!string.IsNullOrEmpty(entry.ProcessName)
                    && !string.Equals(entry.ProcessName, nameWithoutExtension, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(entry.ProcessName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? imagePath;
                try
                {
                    imagePath = _queryImagePath(entry.ProcessId);
                }
                catch (Exception exception)
                {
                    imagePath = null;
                    AppLog.Write(LogLevel.Debug, LogModule, $"Image path of PID {entry.ProcessId} could not be read ({exception.GetType().Name}: {exception.Message}); it is treated as not running.", processId: checked((int)entry.ProcessId));
                }

                var normalized = NormalizePath(imagePath);
                if (normalized is null)
                {
                    unreadable++;
                    continue;
                }

                if (string.Equals(normalized, target, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(entry.ProcessId);
                }
            }

            if (unreadable > 0)
            {
                AppLog.Write(LogLevel.Warning, LogModule, $"{unreadable} process(es) named like {fileName} could not be inspected (for example elevated or protected processes); they are treated as not running.");
            }

            found.Sort();
            return new ExternalInstanceCheckResult(found, unreadable, null);
        }
        catch (Exception exception)
        {
            AppLog.Write(LogLevel.Error, LogModule, "The external-instance check failed unexpectedly; the launch is allowed as before.", exception: exception);
            return new ExternalInstanceCheckResult(Array.Empty<uint>(), 0, exception);
        }
    }

    /// <summary>Full, case-preserving path without a \\?\ prefix or trailing separators; null when unusable.</summary>
    internal static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var value = path.Trim().Trim('"');
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            value = @"\\" + value[8..];
        }
        else if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            value = value[4..];
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// This process, and the process that started it when it is older than this one (so a reused PID is not
    /// excluded). Process Guardian never treats itself or its own launcher as an external instance of an entry.
    /// </summary>
    internal static IReadOnlyCollection<uint> GetDefaultExcludedProcessIds()
    {
        var result = new HashSet<uint> { (uint)Environment.ProcessId };
        try
        {
            using var current = Process.GetCurrentProcess();
            var information = new ProcessBasicInformation();
            if (NtQueryInformationProcess(current.Handle, 0, ref information, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0)
            {
                var parentId = (uint)information.InheritedFromUniqueProcessId.ToInt64();
                if (parentId != 0)
                {
                    try
                    {
                        using var parent = Process.GetProcessById(checked((int)parentId));
                        if (parent.StartTime <= current.StartTime)
                        {
                            result.Add(parentId);
                        }
                    }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
                    {
                        // The parent has exited or cannot be inspected; it is not excluded.
                    }
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.Write(LogLevel.Debug, LogModule, $"The parent process could not be determined ({exception.GetType().Name}).");
        }

        return result;
    }

    private static int GetCurrentSessionId()
    {
        using var current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    private static IEnumerable<ProcessSnapshotEntry> EnumerateSystemProcesses()
    {
        var entries = new List<ProcessSnapshotEntry>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    entries.Add(new ProcessSnapshotEntry((uint)process.Id, process.SessionId, process.ProcessName));
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // The process ended while the snapshot was read.
                }
            }
        }

        return entries;
    }

    /// <summary>QueryFullProcessImageNameW with PROCESS_QUERY_LIMITED_INFORMATION; null when it cannot be read.</summary>
    internal static string? QueryImagePath(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            AppLog.Write(LogLevel.Debug, LogModule, $"OpenProcess(PID {processId}) failed with Windows error {error}; it is treated as not running.", processId: checked((int)processId));
            return null;
        }

        try
        {
            var capacity = 1024;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = new StringBuilder(capacity);
                var size = capacity;
                if (QueryFullProcessImageName(handle, 0, buffer, ref size))
                {
                    return buffer.ToString(0, size);
                }

                var error = Marshal.GetLastWin32Error();
                if (error != 122)
                {
                    AppLog.Write(LogLevel.Debug, LogModule, $"QueryFullProcessImageNameW(PID {processId}) failed with Windows error {error}; it is treated as not running.", processId: checked((int)processId));
                    return null;
                }

                capacity *= 4;
            }

            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref ProcessBasicInformation information, int length, out int returnLength);
}

/// <summary>User-facing text for a refused launch (shared by the Launch button and --launch).</summary>
public static class ExternalInstanceMessages
{
    public const string Headline = "该应用已在 Process Guardian 之外运行";

    public const string Title = "Process Guardian - 无法纳管";

    private const int MaxListedProcessIds = 10;

    public static string Format(string softwareName, IReadOnlyList<uint> processIds)
    {
        var listed = string.Join(", ", processIds.Take(MaxListedProcessIds));
        if (processIds.Count > MaxListedProcessIds)
        {
            listed += $" 等 {processIds.Count} 个";
        }

        return $"{Headline}。\n\n应用：{softwareName}\n进程 PID：{listed}\n\n请先关闭已有进程，再从 Process Guardian 启动。";
    }
}