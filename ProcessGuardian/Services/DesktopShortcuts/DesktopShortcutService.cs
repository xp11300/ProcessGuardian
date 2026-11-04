using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using ProcessGuardian.Services.CommandLine;
using ProcessGuardian.Services.Logging;

namespace ProcessGuardian.Services.DesktopShortcuts;

internal enum DesktopShortcutStatus
{
    Created,
    AlreadyExists,
    InvalidName,
    AmbiguousName,
    Failed,
}

/// <summary>
/// Outcome of <see cref="DesktopShortcutService.Create"/>. <see cref="Path"/> is the .lnk path for Created and
/// AlreadyExists; <see cref="IconUsed"/> is true only when the item's executable was set as the icon;
/// <see cref="Message"/> explains InvalidName / AmbiguousName / Failed and the icon fallback.
/// </summary>
internal sealed record DesktopShortcutResult(DesktopShortcutStatus Status, string? Path, bool IconUsed, string? Message)
{
    internal static DesktopShortcutResult Created(string path, bool iconUsed, string? note = null) => new(DesktopShortcutStatus.Created, path, iconUsed, note);
    internal static DesktopShortcutResult AlreadyExists(string path) => new(DesktopShortcutStatus.AlreadyExists, path, false, null);
    internal static DesktopShortcutResult InvalidName(string reason) => new(DesktopShortcutStatus.InvalidName, null, false, reason);
    internal static DesktopShortcutResult AmbiguousName(int count) => new(DesktopShortcutStatus.AmbiguousName, null, false, $"有 {count} 个条目使用这个名称（不区分大小写）");
    internal static DesktopShortcutResult Failed(string message, string? path = null) => new(DesktopShortcutStatus.Failed, path, false, message);
}

/// <summary>
/// Creates a "<c>&lt;name&gt;.lnk</c>" that starts Process Guardian with <c>--launch "&lt;name&gt;"</c>, so the
/// existing command-line path (selector → LaunchCommand → Job) is used. Additive only: it does not launch anything
/// and never overwrites an existing file (the .lnk is written to a temporary name in the same folder and moved into
/// place without overwrite). The destination folder is injectable so tests write only to a temporary directory;
/// the production default is the current user's Desktop. Uses IShellLinkW / IPersistFile COM interop declared here
/// (no COM reference or package).
/// </summary>
public sealed class DesktopShortcutService
{
    private const int MaxFileNameStemLength = 120;
    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private readonly string? _destinationFolder;
    private readonly string? _launcherPath;

    /// <param name="destinationFolder">Folder for the .lnk; null means the user's Desktop (resolved at Create time).</param>
    /// <param name="launcherPath">Shortcut target; null means the current Process Guardian exe (<see cref="Environment.ProcessPath"/>).</param>
    internal DesktopShortcutService(string? destinationFolder = null, string? launcherPath = null)
    {
        _destinationFolder = destinationFolder;
        _launcherPath = launcherPath;
    }

    internal string DestinationFolder => _destinationFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <param name="softwareName">The entry's software name (what <c>--launch</c> will select).</param>
    /// <param name="executablePath">The entry's executable; used only as the icon source.</param>
    /// <param name="configuredNames">Names of all configured entries (including this one), for the uniqueness check.</param>
    internal DesktopShortcutResult Create(string? softwareName, string? executablePath, IEnumerable<string?> configuredNames)
    {
        ArgumentNullException.ThrowIfNull(configuredNames);
        var name = softwareName?.Trim() ?? string.Empty;
        var nameError = ValidateName(name);
        if (nameError is not null)
        {
            AppLog.Write(LogLevel.Warning, "DesktopShortcut", $"Shortcut rejected: {nameError}", name);
            return DesktopShortcutResult.InvalidName(nameError);
        }

        var sameName = configuredNames.Count(other => string.Equals(other?.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (sameName > 1)
        {
            AppLog.Write(LogLevel.Warning, "DesktopShortcut", $"Shortcut rejected: {sameName} entries share this name", name);
            return DesktopShortcutResult.AmbiguousName(sameName);
        }

        string? finalPath = null;
        string? temporaryPath = null;
        try
        {
            var launcher = _launcherPath ?? Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(launcher) || !File.Exists(launcher))
            {
                return Fail(name, $"找不到 Process Guardian 程序本身的路径（{launcher ?? "未知"}）。", null);
            }

            launcher = System.IO.Path.GetFullPath(launcher);
            var folder = DestinationFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return Fail(name, $"目标文件夹不存在：{folder}", null);
            }

            finalPath = System.IO.Path.Combine(folder, SanitizeFileName(name) + ".lnk");
            if (File.Exists(finalPath) || Directory.Exists(finalPath))
            {
                AppLog.Write(LogLevel.Information, "DesktopShortcut", $"Shortcut already exists and was not overwritten: {finalPath}", name);
                return DesktopShortcutResult.AlreadyExists(finalPath);
            }

            var iconUsed = !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath);
            temporaryPath = System.IO.Path.Combine(folder, $".pg-shortcut-{Guid.NewGuid():N}.tmp");
            WriteShortcut(
                temporaryPath,
                launcher,
                BuildArguments(name),
                System.IO.Path.GetDirectoryName(launcher) ?? string.Empty,
                iconUsed ? System.IO.Path.GetFullPath(executablePath!) : null,
                $"通过 Process Guardian 启动 {name}");

            try
            {
                File.Move(temporaryPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath) || Directory.Exists(finalPath))
            {
                // Created by someone else between the check and the move: keep theirs.
                AppLog.Write(LogLevel.Information, "DesktopShortcut", $"Shortcut appeared concurrently and was not overwritten: {finalPath}", name);
                return DesktopShortcutResult.AlreadyExists(finalPath);
            }

            temporaryPath = null;
            var note = iconUsed ? null : $"程序文件不存在（{executablePath}），快捷方式未设置自定义图标。";
            AppLog.Write(LogLevel.Information, "DesktopShortcut",
                $"Desktop shortcut created: {finalPath}" + (iconUsed ? string.Empty : " (icon file missing; no custom icon)"), name);
            return DesktopShortcutResult.Created(finalPath, iconUsed, note);
        }
        catch (Exception exception)
        {
            return Fail(name, $"{exception.GetType().Name}: {exception.Message}", exception, finalPath);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDeleteTemporary(temporaryPath, name);
            }
        }
    }

    /// <summary>
    /// Returns null when <c>--launch "&lt;name&gt;"</c> round-trips to the same entry, otherwise a Chinese reason.
    /// </summary>
    internal static string? ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "名称为空或只有空白";
        }

        if (trimmed.Contains('"'))
        {
            return "名称包含英文双引号 (\")，命令行无法原样传递";
        }

        if (trimmed.Any(char.IsControl))
        {
            return "名称包含控制字符（例如换行）";
        }

        if (trimmed.StartsWith("--", StringComparison.Ordinal))
        {
            return "名称以 \"--\" 开头，--launch \"<名称>\" 会被当作缺少值";
        }

        if (Guid.TryParse(trimmed, out _))
        {
            return "名称本身是一个 GUID，--launch 会把它当作条目 Id 而不是名称";
        }

        return null;
    }

    /// <summary>
    /// <c>--launch "&lt;name&gt;"</c> quoted for the Windows command-line rules (CommandLineToArgvW / .NET): the name
    /// has no double quotes (validated), so only backslashes right before the closing quote need doubling.
    /// </summary>
    internal static string BuildArguments(string name)
    {
        var trimmed = name.Trim();
        var trailing = 0;
        while (trailing < trimmed.Length && trimmed[trimmed.Length - 1 - trailing] == '\\')
        {
            trailing++;
        }

        return $"{CommandLineOptions.LaunchOption} \"{trimmed}{new string('\\', trailing)}\"";
    }

    /// <summary>Replaces invalid file-name characters with '_' and avoids reserved device names and trailing dots.</summary>
    internal static string SanitizeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Trim())
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        var stem = builder.ToString().TrimEnd('.', ' ');
        if (stem.Length > MaxFileNameStemLength)
        {
            stem = stem[..MaxFileNameStemLength].TrimEnd('.', ' ');
        }

        if (stem.Length == 0)
        {
            stem = "_";
        }

        var firstPart = stem.Split('.')[0].TrimEnd(' ');
        if (ReservedDeviceNames.Contains(firstPart, StringComparer.OrdinalIgnoreCase))
        {
            stem = "_" + stem;
        }

        return stem;
    }

    private static DesktopShortcutResult Fail(string name, string message, Exception? exception, string? path = null)
    {
        AppLog.Write(LogLevel.Error, "DesktopShortcut", $"Desktop shortcut creation failed: {message}", name, exception: exception);
        return DesktopShortcutResult.Failed(message, path);
    }

    private static void TryDeleteTemporary(string path, string name)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            AppLog.Write(LogLevel.Warning, "DesktopShortcut", $"Could not delete temporary shortcut file {path}", name, exception: exception);
        }
    }

    private static void WriteShortcut(string path, string target, string arguments, string workingDirectory, string? iconPath, string description)
    {
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription(description);
            if (iconPath is not null)
            {
                link.SetIconLocation(iconPath, 0);
            }

            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}

/// <summary>Chinese MessageBox text for a <see cref="DesktopShortcutResult"/> (pure; tested).</summary>
internal static class DesktopShortcutMessages
{
    internal const string Title = "创建桌面快捷方式";

    /// <param name="dataDirectoryOverride">Current PROCESSGUARDIAN_DATA_DIR value (null/empty when not set).</param>
    internal static (string Text, bool IsError) Format(DesktopShortcutResult result, string? softwareName, string? dataDirectoryOverride)
    {
        var name = softwareName?.Trim() ?? string.Empty;
        switch (result.Status)
        {
            case DesktopShortcutStatus.Created:
                var text = new StringBuilder()
                    .Append("已创建桌面快捷方式：\n").Append(result.Path)
                    .Append("\n\n双击它会通过 Process Guardian 启动“").Append(name).Append("”。");
                if (!result.IconUsed)
                {
                    text.Append("\n\n注意：").Append(result.Message ?? "未设置自定义图标。");
                }

                if (!string.IsNullOrWhiteSpace(dataDirectoryOverride))
                {
                    text.Append("\n\n注意：当前进程设置了 PROCESSGUARDIAN_DATA_DIR（").Append(dataDirectoryOverride)
                        .Append("），快捷方式不会带上这个数据目录。双击快捷方式时使用的是默认数据目录（%LOCALAPPDATA%\\ProcessGuardian）中的配置，那里需要有同名条目。");
                }

                return (text.ToString(), false);
            case DesktopShortcutStatus.AlreadyExists:
                return ($"同名快捷方式已存在，未覆盖：\n{result.Path}\n\n如需重新创建，请先手动删除或重命名这个文件。", true);
            case DesktopShortcutStatus.InvalidName:
                return ($"无法为“{name}”创建快捷方式：{result.Message}。\n\n请修改软件名称后重试。", true);
            case DesktopShortcutStatus.AmbiguousName:
                return ($"无法为“{name}”创建快捷方式：{result.Message}，--launch 无法确定要启动哪一个。\n\n请先给这些条目改成不同的名称。", true);
            default:
                return ($"创建快捷方式失败：{result.Message}", true);
        }
    }
}
