using ProcessGuardian.Interop;

namespace ProcessGuardian.Services.ApplicationLifecycle;

/// <summary>
/// Outcome of <see cref="AppUserModelIdentity.Apply"/>. It is produced before the logger exists, so the caller keeps it
/// and writes it to the log once logging is up.
/// </summary>
internal sealed record AppUserModelIdResult(string AppId, int HResult, Exception? Exception)
{
    public bool Succeeded => Exception is null && HResult >= 0;

    public string Describe()
    {
        if (Succeeded)
        {
            return $"Process AppUserModelID set to \"{AppId}\"";
        }

        var reason = Exception is not null
            ? $"{Exception.GetType().Name}: {Exception.Message}"
            : $"HRESULT 0x{HResult:X8}";
        return $"SetCurrentProcessExplicitAppUserModelID(\"{AppId}\") failed ({reason}); continuing, but the taskbar may show the launching shortcut's icon for Process Guardian";
    }
}

/// <summary>
/// Gives the Process Guardian process a fixed, explicit AppUserModelID so its taskbar button is no longer tied to the
/// .lnk that started it (a generated desktop shortcut carries the managed application's icon, e.g. wps.exe).
/// Only the process is tagged: generated shortcuts deliberately do not carry this ID, because a .lnk with Process
/// Guardian's ID and another application's icon could become the shell's icon source for Process Guardian again
/// (HANDOVER 7.18).
/// </summary>
internal static class AppUserModelIdentity
{
    internal const string AppId = "ProcessGuardian.App";

    /// <summary>Never throws: a failure HRESULT or interop exception is returned for the caller to log.</summary>
    internal static AppUserModelIdResult Apply(string appId = AppId)
    {
        try
        {
            var hresult = ShellAppUserModelNativeMethods.SetCurrentProcessExplicitAppUserModelID(appId);
            return new AppUserModelIdResult(appId, hresult, null);
        }
        catch (Exception exception)
        {
            return new AppUserModelIdResult(appId, exception.HResult, exception);
        }
    }
}
