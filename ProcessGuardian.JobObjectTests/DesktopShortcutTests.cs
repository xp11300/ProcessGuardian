using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using ProcessGuardian.Services.CommandLine;
using ProcessGuardian.Services.DesktopShortcuts;

namespace ProcessGuardian.JobObjectTests;

/// <summary>
/// In-process checks of <see cref="DesktopShortcutService"/>. Every shortcut is written into a unique
/// %TEMP%\PG-DesktopShortcutTests-&lt;guid&gt; folder that is deleted afterwards; the real Desktop is never written.
/// The .lnk files are read back through an independent IShellLinkW declaration. Nothing is launched: the shortcut
/// target is only a path string (this test exe) and the "item executables" are empty placeholder files.
/// </summary>
internal static class DesktopShortcutTests
{
    private const uint StgmRead = 0;
    private const uint SlgpRawPath = 0x4;

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"PG-DesktopShortcutTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var launcher = Environment.ProcessPath ?? throw new InvalidOperationException("Environment.ProcessPath is unavailable.");
            var appFolder = Path.Combine(root, "app");
            Directory.CreateDirectory(appFolder);
            var itemExe = Path.Combine(appFolder, "wps.exe");
            File.WriteAllBytes(itemExe, Array.Empty<byte>());

            TestCreatedAndReadBack(root, launcher, itemExe);
            TestAlreadyExists(root, launcher, itemExe);
            TestInvalidNames(root, launcher, itemExe);
            TestAmbiguousName(root, launcher, itemExe);
            TestSanitizedFileNames(root, launcher, itemExe);
            TestMissingIconFallsBack(root, launcher);
            TestArgumentsRoundTrip(root, launcher, itemExe);
            TestFailuresAndMessages(root, launcher, itemExe);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        if (Directory.Exists(root))
        {
            throw new InvalidOperationException($"Desktop shortcut test folder was not removed: {root}");
        }
    }

    private static void TestCreatedAndReadBack(string root, string launcher, string itemExe)
    {
        var folder = NewFolder(root, "created");
        var service = new DesktopShortcutService(folder, launcher);
        var result = service.Create("WPS", itemExe, new[] { "WPS", "Other" });
        Expect(result.Status == DesktopShortcutStatus.Created, $"Created expected, got {result}");
        var expectedPath = Path.Combine(folder, "WPS.lnk");
        Expect(string.Equals(result.Path, expectedPath, StringComparison.OrdinalIgnoreCase), $"Unexpected path {result.Path}");
        Expect(result.IconUsed && result.Message is null, "The existing item executable should be used as the icon.");
        Expect(Directory.GetFileSystemEntries(folder).Length == 1, "Only WPS.lnk may be left in the folder (no temporary file).");

        var link = ReadShortcut(expectedPath);
        Expect(PathsEqual(link.Target, launcher), $"Target {link.Target} != {launcher}");
        Expect(link.Arguments == "--launch \"WPS\"", $"Arguments were {link.Arguments}");
        Expect(PathsEqual(link.WorkingDirectory, Path.GetDirectoryName(launcher)!), $"Working directory was {link.WorkingDirectory}");
        Expect(PathsEqual(link.IconPath, itemExe) && link.IconIndex == 0, $"Icon was {link.IconPath},{link.IconIndex}");
        Expect(link.Description == "通过 Process Guardian 启动 WPS", $"Description was {link.Description}");
        Expect(string.Equals(new DesktopShortcutService().DestinationFolder,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), StringComparison.OrdinalIgnoreCase),
            "The production default folder must be the user's Desktop (resolved only, nothing written).");
        Console.WriteLine("PASS: desktop shortcut created in a temp folder; target, arguments, working directory, icon and description read back via IShellLinkW");
    }

    private static void TestAlreadyExists(string root, string launcher, string itemExe)
    {
        var folder = NewFolder(root, "exists");
        var service = new DesktopShortcutService(folder, launcher);
        var first = service.Create("WPS", itemExe, new[] { "WPS" });
        Expect(first.Status == DesktopShortcutStatus.Created, $"Setup failed: {first}");
        var lnkPath = first.Path!;
        var foreignPath = Path.Combine(folder, "Foreign.lnk");
        File.WriteAllBytes(foreignPath, Encoding.UTF8.GetBytes("not a shortcut, must stay unchanged"));
        var bytesBefore = File.ReadAllBytes(lnkPath);
        var foreignBefore = File.ReadAllBytes(foreignPath);
        var timeBefore = File.GetLastWriteTimeUtc(lnkPath);
        var listingBefore = Listing(folder);

        var second = service.Create("wps", Path.Combine(root, "different.exe"), new[] { "wps" });
        var foreign = service.Create("Foreign", itemExe, new[] { "Foreign" });

        Expect(second.Status == DesktopShortcutStatus.AlreadyExists && PathsEqual(second.Path!, lnkPath), $"AlreadyExists expected, got {second}");
        Expect(foreign.Status == DesktopShortcutStatus.AlreadyExists, $"AlreadyExists expected for a foreign file, got {foreign}");
        Expect(File.ReadAllBytes(lnkPath).AsSpan().SequenceEqual(bytesBefore), "The existing .lnk changed.");
        Expect(File.GetLastWriteTimeUtc(lnkPath) == timeBefore, "The existing .lnk was rewritten.");
        Expect(File.ReadAllBytes(foreignPath).AsSpan().SequenceEqual(foreignBefore), "The existing foreign file changed.");
        Expect(Listing(folder) == listingBefore, "AlreadyExists must not write any file.");
        Console.WriteLine("PASS: an existing <name>.lnk (or any file with that name) is reported AlreadyExists and left byte-for-byte unchanged");
    }

    private static void TestInvalidNames(string root, string launcher, string itemExe)
    {
        var folder = NewFolder(root, "invalid");
        var service = new DesktopShortcutService(folder, launcher);
        foreach (var name in new string?[] { null, "", "   ", "\t", "a\"b", "--x", "line\nbreak", "3F2504E0-4F89-11D3-9A0C-0305E82C3301" })
        {
            var result = service.Create(name, itemExe, new[] { name ?? string.Empty });
            Expect(result.Status == DesktopShortcutStatus.InvalidName && !string.IsNullOrWhiteSpace(result.Message),
                $"InvalidName expected for {Show(name)}, got {result}");
        }

        Expect(Directory.GetFileSystemEntries(folder).Length == 0, "Invalid names must not write anything.");
        Console.WriteLine("PASS: empty/whitespace names and names that cannot round-trip through --launch (quote, leading --, control char, GUID) are refused without writing");
    }

    private static void TestAmbiguousName(string root, string launcher, string itemExe)
    {
        var folder = NewFolder(root, "ambiguous");
        var service = new DesktopShortcutService(folder, launcher);
        var result = service.Create("WPS", itemExe, new[] { "WPS", "wps ", "Other" });
        Expect(result.Status == DesktopShortcutStatus.AmbiguousName, $"AmbiguousName expected, got {result}");
        Expect(Directory.GetFileSystemEntries(folder).Length == 0, "AmbiguousName must not write anything.");
        var unique = service.Create("WPS", itemExe, new[] { "WPS", "WPS Office" });
        Expect(unique.Status == DesktopShortcutStatus.Created, $"A unique name must still be accepted, got {unique}");
        Console.WriteLine("PASS: a name shared by several entries (case-insensitive, trimmed) is refused as AmbiguousName without writing");
    }

    private static void TestSanitizedFileNames(string root, string launcher, string itemExe)
    {
        var folder = NewFolder(root, "sanitize");
        var service = new DesktopShortcutService(folder, launcher);
        var cases = new (string Name, string FileName)[]
        {
            ("A/B:C*D?E<F>G|H\\I", "A_B_C_D_E_F_G_H_I.lnk"),
            ("CON", "_CON.lnk"),
            ("nul.txt", "_nul.txt.lnk"),
            ("Trailing.", "Trailing.lnk"),
            ("中文 软件", "中文 软件.lnk"),
        };
        foreach (var (name, fileName) in cases)
        {
            var result = service.Create(name, itemExe, new[] { name });
            var expected = Path.Combine(folder, fileName);
            Expect(result.Status == DesktopShortcutStatus.Created && PathsEqual(result.Path!, expected), $"{Show(name)}: expected {expected}, got {result}");
            Expect(File.Exists(expected), $"{expected} was not written.");
            var link = ReadShortcut(expected);
            Expect(link.Arguments == DesktopShortcutService.BuildArguments(name), $"{Show(name)}: arguments {link.Arguments} must keep the original name");
            Expect(ParseSelector(link.Arguments) == name.Trim(), $"{Show(name)}: arguments do not parse back to the name");
        }

        Expect(Directory.GetFileSystemEntries(folder).Length == cases.Length, "Unexpected extra files after sanitizing.");
        Console.WriteLine("PASS: invalid file-name characters and reserved device names are sanitized in the .lnk file name while --launch keeps the original name");
    }

    private static void TestMissingIconFallsBack(string root, string launcher)
    {
        var folder = NewFolder(root, "noicon");
        var service = new DesktopShortcutService(folder, launcher);
        var missing = Path.Combine(root, "missing", "app.exe");
        var result = service.Create("NoIcon", missing, new[] { "NoIcon" });
        Expect(result.Status == DesktopShortcutStatus.Created && !result.IconUsed, $"Created without icon expected, got {result}");
        Expect(result.Message?.Contains(missing, StringComparison.Ordinal) == true, $"The fallback must be explained: {result.Message}");
        var link = ReadShortcut(result.Path!);
        Expect(link.IconPath.Length == 0, $"No custom icon expected, got {link.IconPath}");
        Expect(link.Arguments == "--launch \"NoIcon\"" && PathsEqual(link.Target, launcher), "Fallback shortcut has wrong target or arguments.");
        var (text, isError) = DesktopShortcutMessages.Format(result, "NoIcon", null);
        Expect(!isError && text.Contains("未设置自定义图标", StringComparison.Ordinal), "The success message must mention the icon fallback.");
        Console.WriteLine("PASS: a missing item executable falls back to no custom icon and the result says so");
    }

    private static void TestArgumentsRoundTrip(string root, string launcher, string itemExe)
    {
        var names = new[] { "WPS", "WPS Office", "a=b", "-x", "C:\\Tools\\", "back\\slash", "trail\\\\", "中文 软件", "  spaced  ", "Tom & Jerry", "100%", "x^y" };
        var entries = names.Select(name => new SelectorEntry(Guid.NewGuid(), name)).Append(new SelectorEntry(Guid.NewGuid(), "Unrelated")).ToArray();
        foreach (var name in names)
        {
            Expect(DesktopShortcutService.ValidateName(name) is null, $"{Show(name)} should be accepted.");
            var arguments = DesktopShortcutService.BuildArguments(name);
            var selector = ParseSelector(arguments);
            Expect(selector == name.Trim(), $"{Show(name)}: {arguments} parsed to {Show(selector)}");
            var selection = ApplicationSelector.Select(entries, entry => entry.Id, entry => entry.Name, selector!);
            Expect(selection.Status == ApplicationSelectionStatus.Found && selection.Item!.Name == name, $"{Show(name)}: selection {selection.Status}");
        }

        var folder = NewFolder(root, "roundtrip");
        var created = new DesktopShortcutService(folder, launcher).Create("C:\\Tools\\", itemExe, new[] { "C:\\Tools\\" });
        Expect(created.Status == DesktopShortcutStatus.Created, $"Setup failed: {created}");
        Expect(ParseSelector(ReadShortcut(created.Path!).Arguments) == "C:\\Tools\\", "Stored arguments with a trailing backslash do not round-trip.");
        Console.WriteLine("PASS: shortcut arguments round-trip through Windows command-line splitting, CommandLineParser and ApplicationSelector to the same entry");
    }

    private static void TestFailuresAndMessages(string root, string launcher, string itemExe)
    {
        var missingFolder = Path.Combine(root, "does-not-exist");
        var noFolder = new DesktopShortcutService(missingFolder, launcher).Create("WPS", itemExe, new[] { "WPS" });
        Expect(noFolder.Status == DesktopShortcutStatus.Failed && !string.IsNullOrWhiteSpace(noFolder.Message), $"Failed expected, got {noFolder}");
        Expect(!Directory.Exists(missingFolder), "A missing destination folder must not be created.");
        var folder = NewFolder(root, "failures");
        var noLauncher = new DesktopShortcutService(folder, Path.Combine(root, "missing-launcher.exe")).Create("WPS", itemExe, new[] { "WPS" });
        Expect(noLauncher.Status == DesktopShortcutStatus.Failed && Directory.GetFileSystemEntries(folder).Length == 0, $"Failed without writing expected, got {noLauncher}");

        var created = DesktopShortcutResult.Created(Path.Combine(folder, "WPS.lnk"), iconUsed: true);
        var (plain, plainError) = DesktopShortcutMessages.Format(created, "WPS", null);
        var (withDir, _) = DesktopShortcutMessages.Format(created, "WPS", "C:\\isolated");
        Expect(!plainError && plain.Contains("WPS.lnk", StringComparison.Ordinal) && !plain.Contains("PROCESSGUARDIAN_DATA_DIR", StringComparison.Ordinal),
            "Plain success message is wrong.");
        Expect(withDir.Contains("PROCESSGUARDIAN_DATA_DIR", StringComparison.Ordinal) && withDir.Contains("C:\\isolated", StringComparison.Ordinal),
            "The success message must mention that the data directory is not carried.");
        Expect(DesktopShortcutMessages.Format(DesktopShortcutResult.AlreadyExists("x.lnk"), "x", null) is (var exists, true) && exists.Contains("未覆盖", StringComparison.Ordinal),
            "AlreadyExists message is wrong.");
        Expect(DesktopShortcutMessages.Format(DesktopShortcutResult.AmbiguousName(2), "x", null).IsError
            && DesktopShortcutMessages.Format(DesktopShortcutResult.InvalidName("名称为空或只有空白"), "", null).IsError
            && DesktopShortcutMessages.Format(noFolder, "WPS", null) is (var failed, true) && failed.Contains(noFolder.Message!, StringComparison.Ordinal),
            "Rejection/failure messages must be errors that carry the reason.");
        Console.WriteLine("PASS: missing destination folder or launcher reports Failed with a message and writes nothing; Chinese result messages cover every outcome");
    }

    private static string NewFolder(string root, string name)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Listing(string folder) => string.Join("|",
        Directory.GetFileSystemEntries(folder).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => $"{Path.GetFileName(path)}:{new FileInfo(path).Length}"));

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string Show(string? value) => value is null ? "(null)" : "\"" + value.Replace("\n", "\\n") + "\"";

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Desktop shortcut test failed: " + message);
        }
    }

    /// <summary>Splits like the Windows loader (CommandLineToArgvW) and parses with the real CommandLineParser.</summary>
    private static string? ParseSelector(string arguments)
    {
        var argv = SplitCommandLine("\"C:\\PG\\ProcessGuardian.exe\" " + arguments);
        var parsed = CommandLineParser.Parse(argv.Skip(1).ToArray());
        Expect(parsed.IsValid, $"{arguments} is not valid: {parsed.Error}");
        return parsed.Options!.LaunchSelector;
    }

    private static string[] SplitCommandLine(string commandLine)
    {
        var pointer = CommandLineToArgvW(commandLine, out var count);
        if (pointer == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            return Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size))!).ToArray();
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    private sealed record SelectorEntry(Guid Id, string Name);

    private sealed record ShortcutData(string Target, string Arguments, string WorkingDirectory, string IconPath, int IconIndex, string Description);

    private static ShortcutData ReadShortcut(string path)
    {
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            ((IPersistFile)link).Load(path, (int)StgmRead);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, SlgpRawPath);
            var arguments = new StringBuilder(1024);
            link.GetArguments(arguments, arguments.Capacity);
            var directory = new StringBuilder(1024);
            link.GetWorkingDirectory(directory, directory.Capacity);
            var icon = new StringBuilder(1024);
            link.GetIconLocation(icon, icon.Capacity, out var iconIndex);
            var description = new StringBuilder(1024);
            link.GetDescription(description, description.Capacity);
            return new ShortcutData(target.ToString(), arguments.ToString(), directory.ToString(), icon.ToString(), iconIndex, description.ToString());
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

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
