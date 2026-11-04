using ProcessGuardian.Interop;

namespace ProcessGuardian.Services.CommandLine;

/// <summary>
/// Brings an existing, visible top-level window forward: restore if minimized, then SetForegroundWindow. A hidden
/// window is never shown, and nothing else (style, position, owner, process) is changed. Only handles reported by
/// Process Guardian itself (a managed application's tracked main window, or Process Guardian's own window) are passed in.
/// </summary>
internal static class TrackedWindowActivator
{
    internal static bool TryBringToForeground(long handleValue)
    {
        if (handleValue == 0)
        {
            return false;
        }

        var handle = new IntPtr(handleValue);
        if (!ForegroundWindowNativeMethods.IsWindow(handle) || !ForegroundWindowNativeMethods.IsWindowVisible(handle))
        {
            return false;
        }

        if (ForegroundWindowNativeMethods.IsIconic(handle))
        {
            ForegroundWindowNativeMethods.ShowWindow(handle, ForegroundWindowNativeMethods.SwRestore);
        }

        return ForegroundWindowNativeMethods.SetForegroundWindow(handle);
    }
}
