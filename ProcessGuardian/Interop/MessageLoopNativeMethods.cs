using System.Runtime.InteropServices;

namespace ProcessGuardian.Interop;

internal static class MessageLoopNativeMethods
{
    internal const uint WmQuit = 0x0012;
    internal const uint WmAppRefreshWindows = 0x8001;
    internal const uint WmAppWindowDestroyed = 0x8002;
    internal const uint WmAppProcessExited = 0x8003;
    internal const uint WmAppConfirmWindowClosed = 0x8004;
    internal const uint WmAppSelectedWindowHidden = 0x8005;
    internal const uint PmNoRemove = 0x0000;
    internal const uint PmRemove = 0x0001;
    internal const uint QsAllInput = 0x04FF;
    internal const uint WaitObject0 = 0x00000000;
    internal const uint WaitFailed = 0xFFFFFFFF;

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "PeekMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(ref NativeMessage message, IntPtr window, uint minimum, uint maximum, uint remove);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    internal static extern int GetMessage(ref NativeMessage message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll", EntryPoint = "MsgWaitForMultipleObjects", SetLastError = true)]
    internal static extern uint MsgWaitForMultipleObjects(
        uint count,
        [In] IntPtr[] handles,
        [MarshalAs(UnmanagedType.Bool)] bool waitAll,
        uint milliseconds,
        uint wakeMask);

    [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMessage
{
    internal IntPtr Window;
    internal uint Message;
    internal UIntPtr WParam;
    internal IntPtr LParam;
    internal uint Time;
    internal NativePoint Point;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    internal int X;
    internal int Y;
}
