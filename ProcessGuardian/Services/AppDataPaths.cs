using System.IO;

namespace ProcessGuardian.Services;

/// <summary>
/// Root folder for config.json and Logs. Defaults to %LOCALAPPDATA%\ProcessGuardian.
/// An absolute path in PROCESSGUARDIAN_DATA_DIR overrides it (used by the test suite to isolate runs);
/// unset, empty, or relative values keep the default.
/// </summary>
public static class AppDataPaths
{
    public const string OverrideVariable = "PROCESSGUARDIAN_DATA_DIR";

    public static string GetDataDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideDirectory) && Path.IsPathFullyQualified(overrideDirectory))
        {
            return Path.GetFullPath(overrideDirectory);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessGuardian");
    }
}
