using System.Runtime.InteropServices;

namespace ProcessGuardian.Interop;

/// <summary>
/// shell32 process-wide AppUserModelID (HANDOVER 7.18). An explicit ID makes the taskbar identify and group this
/// process's windows by that ID instead of by the shortcut that launched it (Process Guardian desktop shortcuts show
/// the managed application's icon). It has to be set before the process shows any window.
/// </summary>
internal static class ShellAppUserModelNativeMethods
{
    /// <summary>Returns an HRESULT; negative values are failures (for example E_INVALIDARG for an ID over 128 characters).</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);
}
