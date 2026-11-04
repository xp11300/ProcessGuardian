using System.Runtime.InteropServices;

namespace ProcessGuardian.JobObjectTests;

internal static class TestWindowNativeMethods
{
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const int SwShow = 5;
    private const int SwHide = 0;
    private const uint WmClose = 0x0010;
    private const uint WmTimer = 0x0113;
    internal const string ReplacementWindowTitle = "Process Guardian replacement main window";

    // Task 16: a main window shown by a non-launched process of the same Job (like BaiduNetdiskUnite.exe).
    internal const string JobChildWindowTitle = "Process Guardian Job child main window";
    private const uint WmAppShowTestWindow = 0x8000 + 0x51;

    // Replacement-window simulation (one per child process): the first WM_CLOSE hides the window and,
    // after the configured delay, the same process creates and shows a new main window.
    private static int s_replacementDelayMs = -1;
    private static UIntPtr s_replacementTimer;
    internal const int SwMinimize = 6;
    internal const int SwRestore = 9;
    internal const int SwShowCommand = 5;
    internal const int SwHideCommand = 0;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        internal uint Size;
        internal uint Style;
        internal WindowProcedure Procedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal IntPtr Instance;
        internal IntPtr Icon;
        internal IntPtr Cursor;
        internal IntPtr Background;
        internal IntPtr MenuName;
        internal string ClassName;
        internal IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal IntPtr Window;
        internal uint Message;
        internal UIntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal int X;
        internal int Y;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern UIntPtr SetTimer(IntPtr window, UIntPtr eventId, uint elapse, IntPtr timerProcedure);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr window, UIntPtr eventId);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    internal static bool ShowTestWindow(IntPtr window) => ShowWindow(window, SwShow);

    // Returns the previous visibility, not success; callers verify the resulting state instead.
    internal static void SetTestWindowShowState(IntPtr window, int command) => ShowWindow(window, command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr window);

    // Close-to-tray simulation: WM_CLOSE only hides the window; the process keeps running.
    private static IntPtr HideOnCloseProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmClose)
        {
            ShowWindow(window, SwHide);
            if (s_replacementDelayMs >= 0 && s_replacementTimer == UIntPtr.Zero)
            {
                s_replacementTimer = SetTimer(IntPtr.Zero, UIntPtr.Zero, (uint)s_replacementDelayMs, IntPtr.Zero);
                s_replacementDelayMs = -1;
            }

            return IntPtr.Zero;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    internal static void RunWindowChild(
        string readyEventName,
        int windowCount = 1,
        bool varyWindowSizes = false,
        string? hiddenHelperHandleFile = null,
        bool hideOnClose = false,
        int replacementDelayMs = -1,
        string? showEventName = null)
    {
        s_replacementDelayMs = hideOnClose ? replacementDelayMs : -1;
        var instance = GetModuleHandle(null);
        var className = $"ProcessGuardianTestWindow_{Guid.NewGuid():N}";
        WindowProcedure procedure = hideOnClose ? HideOnCloseProcedure : DefWindowProc;
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = procedure,
            Instance = instance,
            ClassName = className,
        };

        if (RegisterClassEx(ref windowClass) == 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassExW failed.");
        }

        try
        {
            for (var index = 0; index < windowCount; index++)
            {
                var width = varyWindowSizes ? 560 - index * 100 : 480;
                var height = varyWindowSizes ? 360 - index * 50 : 320;
                var title = $"Process Guardian dedicated test window {index}";
                if (showEventName is not null)
                {
                    // Job child main window: larger than the launched window, created hidden and shown
                    // only when the test runner signals showEventName.
                    (width, height, title) = (600, 400, JobChildWindowTitle);
                }

                var window = CreateWindowEx(0, className, title, WsOverlappedWindow,
                    100 + index * 30, 100 + index * 30, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                if (window == IntPtr.Zero)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW failed.");
                }

                if (showEventName is null)
                {
                    ShowWindow(window, SwShow);
                }
                else
                {
                    var showEvent = EventWaitHandle.OpenExisting(showEventName);
                    var windowToShow = window;
                    new Thread(() =>
                    {
                        using (showEvent)
                        {
                            showEvent.WaitOne();
                            PostMessage(windowToShow, WmAppShowTestWindow, UIntPtr.Zero, IntPtr.Zero);
                        }
                    })
                    { IsBackground = true }.Start();
                }

                if (hiddenHelperHandleFile is not null)
                {
                    // An unowned, titled, never-shown top-level window: counted by the selector
                    // but not eligible, like helper windows real applications keep alive.
                    var helper = CreateWindowEx(0, className, "Process Guardian hidden helper window", WsOverlappedWindow,
                        140, 140, 420, 280, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                    if (helper == IntPtr.Zero)
                    {
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW failed for the hidden helper.");
                    }

                    System.IO.File.WriteAllLines(hiddenHelperHandleFile, new[] { window.ToInt64().ToString(), helper.ToInt64().ToString() });
                }
            }

            using var readySignal = EventWaitHandle.OpenExisting(readyEventName);
            readySignal.Set();

            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message == WmTimer && message.Window == IntPtr.Zero
                    && s_replacementTimer != UIntPtr.Zero && message.WParam == s_replacementTimer)
                {
                    KillTimer(IntPtr.Zero, s_replacementTimer);
                    s_replacementTimer = UIntPtr.Zero;
                    var replacement = CreateWindowEx(0, className, ReplacementWindowTitle, WsOverlappedWindow,
                        160, 160, 520, 340, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                    if (replacement == IntPtr.Zero)
                    {
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW failed for the replacement window.");
                    }

                    ShowWindow(replacement, SwShow);
                    continue;
                }

                if (message.Message == WmAppShowTestWindow && message.Window != IntPtr.Zero)
                {
                    ShowWindow(message.Window, SwShow);
                    continue;
                }

                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            UnregisterClass(className, instance);
            GC.KeepAlive(procedure);
        }
    }
}
