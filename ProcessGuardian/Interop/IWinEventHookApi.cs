namespace ProcessGuardian.Interop;

/// <summary>
/// WinEvent hook install/remove calls used by WindowMonitor. Production code always uses
/// <see cref="Win32WinEventHookApi.Instance"/>, a direct forward to the existing P/Invoke declarations.
/// Tests can wrap it to make SetWinEventHook fail (stage-12 fault injection). A failing implementation
/// must leave the Win32 error in the last P/Invoke error, as the real call does.
/// </summary>
internal interface IWinEventHookApi
{
    IntPtr SetWinEventHook(
        uint eventMinimum,
        uint eventMaximum,
        IntPtr eventModule,
        WindowNativeMethods.WinEventCallback callback,
        uint processId,
        uint threadId,
        uint flags);

    bool UnhookWinEvent(IntPtr hook);
}

/// <summary>Default implementation: a direct forward to <see cref="WindowNativeMethods"/>.</summary>
internal sealed class Win32WinEventHookApi : IWinEventHookApi
{
    internal static readonly Win32WinEventHookApi Instance = new();

    private Win32WinEventHookApi()
    {
    }

    public IntPtr SetWinEventHook(
        uint eventMinimum,
        uint eventMaximum,
        IntPtr eventModule,
        WindowNativeMethods.WinEventCallback callback,
        uint processId,
        uint threadId,
        uint flags) =>
        WindowNativeMethods.SetWinEventHook(eventMinimum, eventMaximum, eventModule, callback, processId, threadId, flags);

    public bool UnhookWinEvent(IntPtr hook) => WindowNativeMethods.UnhookWinEvent(hook);
}
