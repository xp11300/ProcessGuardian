using System.IO;
using System.Security;
using Microsoft.Win32;
using ProcessGuardian.Services.Logging;

namespace ProcessGuardian.Services.Startup;

public enum AutoStartState
{
    /// <summary>No "ProcessGuardian" value under the Run key.</summary>
    Disabled,

    /// <summary>The Run value starts this executable with --autostart.</summary>
    Enabled,

    /// <summary>A Run value exists but points at another path (or carries other arguments).</summary>
    EnabledDifferentPath,
}

/// <summary>
/// Result of reading the Run value. <see cref="Error"/> is set when the registry could not be read; the state is then
/// <see cref="AutoStartState.Disabled"/> because nothing could be confirmed.
/// </summary>
public sealed record AutoStartStatus(
    AutoStartState State,
    string? RegisteredCommand,
    string? RegisteredExecutablePath,
    bool DisabledInTaskManager,
    string? Error)
{
    public bool IsRegistered => State != AutoStartState.Disabled;
}

public sealed record AutoStartOperationResult(bool Succeeded, string? Error, Exception? Exception)
{
    public static AutoStartOperationResult Success { get; } = new(true, null, null);
}

/// <summary>Registry access used by <see cref="AutoStartService"/>; replaceable in tests.</summary>
public interface IAutoStartStore
{
    /// <summary>Returns the raw Run value (null when absent).</summary>
    object? ReadRunValue(string valueName);

    void WriteRunValue(string valueName, string command);

    /// <summary>Deletes the Run value; no-op when it (or the key) is absent.</summary>
    void DeleteRunValue(string valueName);

    /// <summary>Returns the Explorer StartupApproved\Run data for the value (null when absent). Read-only.</summary>
    byte[]? ReadStartupApprovedValue(string valueName);
}

/// <summary><see cref="IAutoStartStore"/> on a root key plus sub key paths. The root key is not disposed here.</summary>
public sealed class RegistryAutoStartStore : IAutoStartStore
{
    private readonly RegistryKey _root;
    private readonly string _runSubKeyPath;
    private readonly string? _startupApprovedSubKeyPath;

    public RegistryAutoStartStore(RegistryKey root, string runSubKeyPath, string? startupApprovedSubKeyPath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(runSubKeyPath);
        _root = root;
        _runSubKeyPath = runSubKeyPath;
        _startupApprovedSubKeyPath = startupApprovedSubKeyPath;
    }

    public object? ReadRunValue(string valueName)
    {
        using var key = _root.OpenSubKey(_runSubKeyPath, writable: false);
        return key?.GetValue(valueName, null, RegistryValueOptions.None);
    }

    public void WriteRunValue(string valueName, string command)
    {
        using var key = _root.CreateSubKey(_runSubKeyPath, writable: true)
            ?? throw new IOException($"The registry key \"{_runSubKeyPath}\" could not be opened for writing.");
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    public void DeleteRunValue(string valueName)
    {
        using var key = _root.OpenSubKey(_runSubKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public byte[]? ReadStartupApprovedValue(string valueName)
    {
        if (string.IsNullOrEmpty(_startupApprovedSubKeyPath))
        {
            return null;
        }

        using var key = _root.OpenSubKey(_startupApprovedSubKeyPath, writable: false);
        return key?.GetValue(valueName) as byte[];
    }
}

/// <summary>
/// Per-user "start with Windows": the value "ProcessGuardian" under HKCU\Software\Microsoft\Windows\CurrentVersion\Run
/// with the data <c>"&lt;exe path&gt;" --autostart</c>. No administrator rights; HKLM is never touched. The Explorer
/// StartupApproved\Run value (Task Manager's enable/disable switch) is only read, never written.
/// Registry failures come back as results and are logged; nothing is thrown to the caller.
/// </summary>
public sealed class AutoStartService
{
    public const string ValueName = "ProcessGuardian";
    public const string RunSubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupApprovedSubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string LogModule = "AutoStart";

    private readonly IAutoStartStore _store;
    private readonly string? _executablePath;

    /// <summary>The real per-user Run key and the current executable.</summary>
    public AutoStartService()
        : this(Registry.CurrentUser, RunSubKeyPath, StartupApprovedSubKeyPath, Environment.ProcessPath)
    {
    }

    public AutoStartService(RegistryKey root, string runSubKeyPath, string? startupApprovedSubKeyPath, string? executablePath)
        : this(new RegistryAutoStartStore(root, runSubKeyPath, startupApprovedSubKeyPath), executablePath)
    {
    }

    public AutoStartService(IAutoStartStore store, string? executablePath)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _executablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath;
    }

    public string? ExecutablePath => _executablePath;

    /// <summary>The Run value data for the current executable, or null when its path is unknown.</summary>
    public string? ExpectedCommand => _executablePath is null ? null : BuildCommand(_executablePath);

    public static string BuildCommand(string executablePath) =>
        $"\"{executablePath}\" {CommandLine.CommandLineOptions.AutoStartOption}";

    public AutoStartStatus GetStatus()
    {
        object? raw;
        try
        {
            raw = _store.ReadRunValue(ValueName);
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            var message = Describe(exception);
            AppLog.Write(LogLevel.Warning, LogModule, $"Could not read the Run value \"{ValueName}\": {message}", exception: exception);
            return new AutoStartStatus(AutoStartState.Disabled, null, null, false, message);
        }

        if (raw is null)
        {
            return new AutoStartStatus(AutoStartState.Disabled, null, null, false, null);
        }

        var command = raw as string ?? raw.ToString() ?? string.Empty;
        var registeredPath = ExtractExecutablePath(command);
        var state = raw is string && IsCurrentCommand(command, registeredPath)
            ? AutoStartState.Enabled
            : AutoStartState.EnabledDifferentPath;
        return new AutoStartStatus(state, command, registeredPath, ReadDisabledInTaskManager(), null);
    }

    /// <summary>Writes (or overwrites) the Run value for the current executable.</summary>
    public AutoStartOperationResult Enable()
    {
        var command = ExpectedCommand;
        if (command is null)
        {
            const string message = "无法确定当前程序的路径（Environment.ProcessPath 为空）。";
            AppLog.Write(LogLevel.Warning, LogModule, "Could not enable start with Windows: the current executable path is unknown.");
            return new AutoStartOperationResult(false, message, null);
        }

        try
        {
            _store.WriteRunValue(ValueName, command);
            AppLog.Write(LogLevel.Information, LogModule, $"Start with Windows enabled: Run value \"{ValueName}\" = {command}");
            return AutoStartOperationResult.Success;
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            var message = Describe(exception);
            AppLog.Write(LogLevel.Warning, LogModule, $"Could not write the Run value \"{ValueName}\": {message}", exception: exception);
            return new AutoStartOperationResult(false, message, exception);
        }
    }

    /// <summary>Deletes the Run value if it exists.</summary>
    public AutoStartOperationResult Disable()
    {
        try
        {
            _store.DeleteRunValue(ValueName);
            AppLog.Write(LogLevel.Information, LogModule, $"Start with Windows disabled: Run value \"{ValueName}\" removed (if present).");
            return AutoStartOperationResult.Success;
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            var message = Describe(exception);
            AppLog.Write(LogLevel.Warning, LogModule, $"Could not delete the Run value \"{ValueName}\": {message}", exception: exception);
            return new AutoStartOperationResult(false, message, exception);
        }
    }

    /// <summary>
    /// The executable path of a Run command: the text between the leading quotes, otherwise everything up to the
    /// first ".exe" (case-insensitive), otherwise up to the first space.
    /// </summary>
    internal static string ExtractExecutablePath(string command)
    {
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end < 0 ? text[1..] : text[1..end];
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0)
        {
            return text[..(exe + 4)];
        }

        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    private bool IsCurrentCommand(string command, string registeredPath)
    {
        if (_executablePath is null || !PathsEqual(registeredPath, _executablePath))
        {
            return false;
        }

        // The path matches; the arguments must be exactly --autostart (e.g. a bare path would start the window).
        var text = command.Trim();
        var rest = text.StartsWith('"')
            ? text[Math.Min(text.Length, registeredPath.Length + 2)..]
            : text[Math.Min(text.Length, registeredPath.Length)..];
        return string.Equals(rest.Trim(), CommandLine.CommandLineOptions.AutoStartOption, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Task Manager's Startup tab stores its switch in Explorer\StartupApproved\Run: the first byte is even (0x02,
    /// 0x06) when enabled and odd (0x03, 0x07) when disabled. Read-only; a read failure is logged and reported as
    /// "not disabled".
    /// </summary>
    private bool ReadDisabledInTaskManager()
    {
        try
        {
            var data = _store.ReadStartupApprovedValue(ValueName);
            return data is { Length: > 0 } && (data[0] & 0x01) != 0;
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            AppLog.Write(LogLevel.Warning, LogModule, $"Could not read the StartupApproved value \"{ValueName}\": {Describe(exception)}", exception: exception);
            return false;
        }
    }

    private static bool IsRegistryFailure(Exception exception) =>
        exception is SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException;

    private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";
}
