using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using ProcessGuardian.Interop;
using ProcessGuardian.Services.Logging;

namespace ProcessGuardian.Services.WindowMonitoring;

/// <summary>
/// Watches the selected visible top-level window for one process. Events are delivered
/// through the synchronization context captured by the constructor, when one is available.
/// </summary>
public sealed class WindowMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, int> _callbackThreads = new();
    private readonly Queue<WindowMonitorStatusChangedEventArgs> _pendingNotifications = new();
    private readonly uint _processId;
    private readonly bool _treatMainWindowHideAsClose;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly WindowNativeMethods.WinEventCallback _winEventCallback;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AutoResetEvent _stopSignal = new(false);
    private readonly IWinEventHookApi _winEventHookApi;
    private Process? _process;
    private Thread? _monitorThread;

    private uint _monitorThreadId;
    private long _hookHandleValue;
    private long _selectedWindowValue;
    private WindowMonitorStatus _status = WindowMonitorStatus.WaitingForWindow;
    private string? _windowTitle;
    private int _processExited;
    private int _disposed;
    private Exception? _unhookException;
    private Exception? _callbackException;
    private Exception? _monitoringException;
    private System.Threading.Timer? _windowCloseConfirmationTimer;
    private bool _notificationDrainScheduled;

    // Monitor-thread-only state. Set when the selected window was destroyed while hidden or
    // otherwise unreliable top-level windows of the process remained (NoReliableWindow), so
    // later DESTROY events can re-evaluate whether the application window really closed.
    private IntPtr _destroyedWindowAwaitingHelpers;
    private string? _destroyedWindowTitle;
    private bool _helperRefreshQueued;

    // Monitor-thread-only state for the opt-in hide-as-close path. Never set when the option is off.
    private IntPtr _hiddenWindowAwaitingConfirmation;
    private long _hiddenWindowHiddenAtTimestamp;
    private bool _replacementGraceLogged;

    // Opt-in hide-as-close only: a selected window that hid before it was armed (a startup or
    // transition window such as a splash/welcome window). Kept selected until the next reliable
    // main window of the launched process is selected, instead of falling to NoReliableWindow.
    private IntPtr _unarmedHiddenWindow;

    // Stopwatch timestamp since which the selected window has been continuously visible
    // (set on selection and on a SHOW of the selected window). Only read when the option is on.
    private long _selectedVisibleSinceTimestamp;
    private readonly HideAsCloseTimings _hideAsCloseTimings;
    private readonly Func<IReadOnlyList<uint>>? _jobProcessIdsProvider;

    // Task 16 (opt-in only): true when TreatMainWindowHideAsClose is on AND a Job process list is
    // available. Only then may main-window selection fall back to windows of other processes in the
    // same Job Object. When false, every code path below is the pre-Task-16 launched-PID-only path.
    private readonly bool _considerJobProcessWindows;

    // Monitor-thread-only (cross-Job path). Set once the launched process itself has had a
    // reliable main window selected; the cross-Job fallback is used only after that.
    private bool _launchedWindowSeen;

    // Monitor-thread-only cache of the Job's process IDs, refreshed from the provider on a miss.
    private HashSet<uint> _jobProcessIdCache = new();
    private bool _jobProcessListWarningLogged;

    // Cross-Job path: qualifying windows of non-launched Job processes that were already visible when
    // the selected window hid (armed). During the replacement grace only windows NOT in this set count
    // as a replacement; pre-existing ones go to the unchanged block check. Cleared when the selected
    // handle changes (reference write is atomic; read on the monitor thread only).
    private HashSet<IntPtr>? _preexistingJobWindows;

    public WindowMonitor(
        uint processId,
        SynchronizationContext? synchronizationContext = null,
        bool treatMainWindowHideAsClose = false)
        : this(processId, synchronizationContext, treatMainWindowHideAsClose, HideAsCloseTimings.Default, null)
    {
    }

    /// <param name="jobProcessIdsProvider">Read-only list of the launch Job's process IDs, used by the
    /// opt-in hide-as-close path only: to block a close while another Job process shows a window, and
    /// (Task 16) as a fallback for main-window selection when the launched process has no reliable window.</param>
    internal WindowMonitor(
        uint processId,
        SynchronizationContext? synchronizationContext,
        bool treatMainWindowHideAsClose,
        HideAsCloseTimings hideAsCloseTimings,
        Func<IReadOnlyList<uint>>? jobProcessIdsProvider,
        IWinEventHookApi? winEventHookApi = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WindowMonitor is available only on Windows.");
        }

        if (processId == 0 || processId > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        _processId = processId;
        _treatMainWindowHideAsClose = treatMainWindowHideAsClose;
        _hideAsCloseTimings = hideAsCloseTimings;
        _jobProcessIdsProvider = jobProcessIdsProvider;
        _considerJobProcessWindows = treatMainWindowHideAsClose && jobProcessIdsProvider is not null;
        _synchronizationContext = synchronizationContext ?? SynchronizationContext.Current;
        _winEventCallback = OnWinEvent;
        // Test seam (stage-12 fault injection); null means the real SetWinEventHook/UnhookWinEvent.
        _winEventHookApi = winEventHookApi ?? Win32WinEventHookApi.Instance;

        try
        {
            _process = Process.GetProcessById((int)processId);
        }
        catch (ArgumentException)
        {
            _status = WindowMonitorStatus.ProcessExited;
            return;
        }

        try
        {
            // Inside the try so a failure here also releases _process through Dispose.
            _process.Exited += OnProcessExited;
            _process.EnableRaisingEvents = true;
            if (_process.HasExited)
            {
                Interlocked.Exchange(ref _processExited, 1);
            }

            _monitorThread = new Thread(MonitorThreadMain)
            {
                IsBackground = true,
                Name = $"Process Guardian window monitor ({processId})",
            };
            _monitorThread.Start();

            _started.Task.GetAwaiter().GetResult();
        }
        catch (Exception startupException)
        {
            try
            {
                Dispose();
            }
            catch (Exception cleanupException)
            {
                // Keep the original startup error (for example SetWinEventHook failed) visible.
                throw new AggregateException(
                    "The window monitor could not start, and its cleanup also reported an error.",
                    startupException,
                    cleanupException);
            }

            throw;
        }
    }

    public event EventHandler<WindowMonitorStatusChangedEventArgs>? StatusChanged;

    public uint ProcessId => _processId;

    public WindowMonitorStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public IntPtr MainWindowHandle => new(Interlocked.Read(ref _selectedWindowValue));

    public string? MainWindowTitle
    {
        get
        {
            lock (_gate)
            {
                return _windowTitle;
            }
        }
    }

    public bool MainWindowIsVisible
    {
        get
        {
            var window = MainWindowHandle;
            return window != IntPtr.Zero && WindowNativeMethods.IsWindow(window) && WindowNativeMethods.IsWindowVisible(window);
        }
    }

    /// <summary>True while SetWinEventHook is installed.</summary>
    public bool IsHookInstalled => Interlocked.Read(ref _hookHandleValue) != 0;

    /// <summary>Contains the most recent exception thrown by a StatusChanged handler, if any.</summary>
    public Exception? CallbackException
    {
        get
        {
            lock (_gate)
            {
                return _callbackException;
            }
        }
    }

    /// <summary>Contains an exception that caused active window monitoring to stop.</summary>
    public Exception? MonitoringException
    {
        get
        {
            lock (_gate)
            {
                return _monitoringException;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed != 0)
            {
                return;
            }

            Volatile.Write(ref _disposed, 1);
            var currentThreadId = Environment.CurrentManagedThreadId;
            var callbacksOnCurrentThread = _callbackThreads.GetValueOrDefault(currentThreadId);
            while (_callbackThreads.Values.Sum() > callbacksOnCurrentThread)
            {
                Monitor.Wait(_gate);
                callbacksOnCurrentThread = _callbackThreads.GetValueOrDefault(currentThreadId);
            }
        }

        if (_process is not null)
        {
            _process.Exited -= OnProcessExited;
        }

        var monitorThread = _monitorThread;
        Exception? stopSignalException = null;
        if (monitorThread is { IsAlive: true } thread)
        {
            // An event handle wakes MsgWaitForMultipleObjects even if the thread-message
            // queue is full. Posting WM_QUIT here could fail and leave Join blocked forever.
            _stopSignal.Set();

            if (Thread.CurrentThread != thread)
            {
                if (!thread.Join(TimeSpan.FromSeconds(5)))
                {
                    stopSignalException = new TimeoutException("The window monitor thread did not stop within five seconds.");
                }
            }
        }
        else
        {
            _stopSignal.Set();
        }

        var remainingHookValue = Interlocked.Read(ref _hookHandleValue);
        if (remainingHookValue != 0 && _winEventHookApi.UnhookWinEvent(new IntPtr(remainingHookValue)))
        {
            Interlocked.Exchange(ref _hookHandleValue, 0);
            _unhookException = null;
        }

        _windowCloseConfirmationTimer?.Dispose();
        if (monitorThread is null || !monitorThread.IsAlive)
        {
            _stopSignal.Dispose();
            _monitorThread = null;
            _process?.Dispose();
            _process = null;
        }
        lock (_gate)
        {
            _status = WindowMonitorStatus.Stopped;
            _windowTitle = null;
            Interlocked.Exchange(ref _selectedWindowValue, 0);
        }

        if (_unhookException is not null)
        {
            throw new InvalidOperationException("Could not release the WinEvent hook.", _unhookException);
        }

        if (stopSignalException is not null)
        {
            throw new InvalidOperationException("Could not stop the window monitor thread cleanly.", stopSignalException);
        }
    }

    private void MonitorThreadMain()
    {
        _monitorThreadId = MessageLoopNativeMethods.GetCurrentThreadId();
        var message = new NativeMessage();

        // Force creation of this thread's message queue before callbacks or Dispose can post to it.
        MessageLoopNativeMethods.PeekMessage(ref message, IntPtr.Zero, 0, 0, MessageLoopNativeMethods.PmNoRemove);

        try
        {
            if (Volatile.Read(ref _processExited) != 0)
            {
                SetStatus(WindowMonitorStatus.ProcessExited, IntPtr.Zero, null);
                _started.TrySetResult();
                return;
            }

            // Option off: exactly CREATE..SHOW for the launched PID as before. Option on: the same single
            // hook also covers EVENT_OBJECT_HIDE (0x8003, adjacent to SHOW). Option on with a Job process
            // list (Task 16): the hook is not limited to one PID (child processes of the Job are created
            // later and can show the real main window); OnWinEvent filters to the launched PID and the
            // Job's processes, and Process Guardian's own events are skipped.
            var hook = _winEventHookApi.SetWinEventHook(
                WindowNativeMethods.EventObjectCreate,
                _treatMainWindowHideAsClose ? WindowNativeMethods.EventObjectHide : WindowNativeMethods.EventObjectShow,
                IntPtr.Zero,
                _winEventCallback,
                _considerJobProcessWindows ? 0 : _processId,
                0,
                _considerJobProcessWindows
                    ? WindowNativeMethods.WineventOutOfContext | WindowNativeMethods.WineventSkipOwnProcess
                    : WindowNativeMethods.WineventOutOfContext);

            if (hook == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWinEventHook failed.");
            }

            Interlocked.Exchange(ref _hookHandleValue, hook.ToInt64());
            RefreshWindowSelection();
            _started.TrySetResult();

            var stopHandles = new[] { _stopSignal.SafeWaitHandle.DangerousGetHandle() };
            while (true)
            {
                var waitResult = MessageLoopNativeMethods.MsgWaitForMultipleObjects(
                    1,
                    stopHandles,
                    waitAll: false,
                    milliseconds: uint.MaxValue,
                    MessageLoopNativeMethods.QsAllInput);
                if (waitResult == MessageLoopNativeMethods.WaitObject0)
                {
                    break;
                }

                if (waitResult == MessageLoopNativeMethods.WaitFailed)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "MsgWaitForMultipleObjects failed on the window monitor thread.");
                }

                if (waitResult != MessageLoopNativeMethods.WaitObject0 + 1)
                {
                    throw new InvalidOperationException($"The window monitor wait returned unexpected result 0x{waitResult:X8}.");
                }

                while (MessageLoopNativeMethods.PeekMessage(
                    ref message,
                    IntPtr.Zero,
                    0,
                    0,
                    MessageLoopNativeMethods.PmRemove))
                {
                    if (HandleMonitorMessage(ref message))
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (_started.Task.IsCompletedSuccessfully)
            {
                lock (_gate)
                {
                    _monitoringException = exception;
                }

                SetStatus(WindowMonitorStatus.MonitoringFailed, IntPtr.Zero, null);
            }
            else
            {
                _started.TrySetException(exception);
            }
        }
        finally
        {
            var hookValue = Interlocked.Read(ref _hookHandleValue);
            if (hookValue != 0)
            {
                var hook = new IntPtr(hookValue);
                if (!_winEventHookApi.UnhookWinEvent(hook))
                {
                    var error = Marshal.GetLastWin32Error();
                    _unhookException = new Win32Exception(error, $"UnhookWinEvent failed (Windows error {error}).");
                }
                else
                {
                    Interlocked.Exchange(ref _hookHandleValue, 0);
                }
            }

            _started.TrySetResult();
        }
    }

    private bool HandleMonitorMessage(ref NativeMessage message)
    {
        switch (message.Message)
        {
            case MessageLoopNativeMethods.WmQuit:
                return true;

            case MessageLoopNativeMethods.WmAppRefreshWindows:
                if (Volatile.Read(ref _processExited) != 0)
                {
                    SetStatus(WindowMonitorStatus.ProcessExited, IntPtr.Zero, null);
                    return true;
                }

                RefreshWindowSelection();
                return false;

            case MessageLoopNativeMethods.WmAppWindowDestroyed:
                HandleSelectedWindowDestroyed(message.LParam);
                return false;

            case MessageLoopNativeMethods.WmAppConfirmWindowClosed:
                ConfirmSelectedWindowClosed(message.LParam);
                return false;

            case MessageLoopNativeMethods.WmAppSelectedWindowHidden:
                HandleSelectedWindowHidden(message.LParam);
                return false;

            case MessageLoopNativeMethods.WmAppProcessExited:
                SetStatus(WindowMonitorStatus.ProcessExited, IntPtr.Zero, null);
                return true;

            default:
                MessageLoopNativeMethods.TranslateMessage(ref message);
                MessageLoopNativeMethods.DispatchMessage(ref message);
                return false;
        }
    }

    private void RefreshWindowSelection()
    {
        _helperRefreshQueued = false;
        if (Volatile.Read(ref _processExited) != 0)
        {
            SetStatus(WindowMonitorStatus.ProcessExited, IntPtr.Zero, null);
            return;
        }

        var selection = SelectMainWindow();
        if (_hiddenWindowAwaitingConfirmation != IntPtr.Zero
            && selection.Status is WindowMonitorStatus.NoReliableWindow or WindowMonitorStatus.WaitingForWindow)
        {
            // Opt-in hide-as-close confirmation pending: keep the hidden selected window until the
            // 400 ms confirmation decides. A new reliable window (Monitoring) still switches below.
            return;
        }

        var unarmedWindow = _unarmedHiddenWindow;
        if (unarmedWindow != IntPtr.Zero)
        {
            if (selection.Status is WindowMonitorStatus.NoReliableWindow or WindowMonitorStatus.WaitingForWindow
                && unarmedWindow == MainWindowHandle && WindowNativeMethods.IsWindow(unarmedWindow))
            {
                // Opt-in only: an unarmed (startup/transition) window hid and no reliable main window
                // of the launched process is visible yet. Keep waiting; do not report NoReliableWindow.
                return;
            }

            _unarmedHiddenWindow = IntPtr.Zero;
            if (selection.Status == WindowMonitorStatus.Monitoring && selection.Handle != unarmedWindow)
            {
                LogHideAsClose($"Hide-as-close: selected the next main window after an unarmed hide (old hwnd=0x{unarmedWindow.ToInt64():X}, new hwnd=0x{selection.Handle.ToInt64():X}, pid={selection.ProcessId}, title={selection.Title})");
            }
        }

        var awaitingWindow = _destroyedWindowAwaitingHelpers;
        if (awaitingWindow != IntPtr.Zero)
        {
            if (selection.Status == WindowMonitorStatus.WaitingForWindow)
            {
                // The remaining unreliable windows are gone. Confirm with the usual delay;
                // the status stays NoReliableWindow until the confirmation re-checks.
                ScheduleWindowCloseConfirmation(awaitingWindow);
                return;
            }

            if (selection.Status != WindowMonitorStatus.NoReliableWindow)
            {
                ClearDestroyedWindowAwaitingHelpers();
            }
        }

        SetStatus(selection.Status, selection.Handle, selection.Title);
    }

    private void ClearDestroyedWindowAwaitingHelpers()
    {
        _destroyedWindowAwaitingHelpers = IntPtr.Zero;
        _destroyedWindowTitle = null;
    }

    private void HandleSelectedWindowDestroyed(IntPtr destroyedWindow)
    {
        if (destroyedWindow != MainWindowHandle)
        {
            return;
        }

        if (Volatile.Read(ref _processExited) != 0)
        {
            SetStatus(WindowMonitorStatus.ProcessExited, IntPtr.Zero, null);
            return;
        }

        var selection = SelectMainWindow();
        if (selection.Status == WindowMonitorStatus.Monitoring)
        {
            // A different qualifying top-level window remains. Do not treat one
            // window closing as proof that the application's main window closed.
            SetStatus(selection.Status, selection.Handle, selection.Title);
        }
        else if (selection.Status == WindowMonitorStatus.WaitingForWindow)
        {
            ScheduleWindowCloseConfirmation(destroyedWindow);
        }
        else
        {
            // Hidden, owned, tool, titleless, or ambiguous windows are not reliable
            // evidence that the application has closed. Preserve the process tree.
            if (selection.Status == WindowMonitorStatus.NoReliableWindow)
            {
                _destroyedWindowAwaitingHelpers = destroyedWindow;
                _destroyedWindowTitle = MainWindowTitle;
            }

            SetStatus(selection.Status, selection.Handle, selection.Title);
            if (selection.Status == WindowMonitorStatus.NoReliableWindow)
            {
                // Stage-12 fix: the helper windows' DESTROY events can be dispatched before the
                // awaiting state above is set (observed under CPU load: all windows gone, status stuck
                // at NoReliableWindow). Re-check once with the usual 400 ms confirmation; it reports
                // WindowClosed only if no window of the process remains, otherwise keeps NoReliableWindow.
                ScheduleWindowCloseConfirmation(destroyedWindow);
            }
        }
    }

    private void ScheduleWindowCloseConfirmation(IntPtr destroyedWindow, TimeSpan? delay = null)
    {
        lock (_gate)
        {
            if (_disposed != 0)
            {
                return;
            }

            _windowCloseConfirmationTimer?.Dispose();
            _windowCloseConfirmationTimer = new System.Threading.Timer(
                static state =>
                {
                    var request = (WindowCloseRequest)state!;
                    request.Monitor.PostMonitorMessage(
                        MessageLoopNativeMethods.WmAppConfirmWindowClosed,
                        request.Window);
                },
                new WindowCloseRequest(this, destroyedWindow),
                delay ?? TimeSpan.FromMilliseconds(400),
                Timeout.InfiniteTimeSpan);
        }
    }

    private void ConfirmSelectedWindowClosed(IntPtr destroyedWindow)
    {
        lock (_gate)
        {
            _windowCloseConfirmationTimer?.Dispose();
            _windowCloseConfirmationTimer = null;
        }

        if (_hiddenWindowAwaitingConfirmation != IntPtr.Zero && destroyedWindow == _hiddenWindowAwaitingConfirmation)
        {
            _hiddenWindowAwaitingConfirmation = IntPtr.Zero;
            if (WindowNativeMethods.IsWindow(destroyedWindow))
            {
                ConfirmSelectedWindowHidden(destroyedWindow);
                return;
            }

            // The hidden window was destroyed meanwhile: continue with the unchanged destroy confirmation.
        }

        var awaitingHelpers = _destroyedWindowAwaitingHelpers != IntPtr.Zero
            && destroyedWindow == _destroyedWindowAwaitingHelpers;
        if ((destroyedWindow != MainWindowHandle && !awaitingHelpers) || Volatile.Read(ref _processExited) != 0)
        {
            return;
        }

        var selection = SelectMainWindow();
        if (selection.Status == WindowMonitorStatus.WaitingForWindow)
        {
            var title = awaitingHelpers ? _destroyedWindowTitle : MainWindowTitle;
            ClearDestroyedWindowAwaitingHelpers();
            SetStatus(WindowMonitorStatus.WindowClosed, destroyedWindow, title);
        }
        else
        {
            if (selection.Status != WindowMonitorStatus.NoReliableWindow)
            {
                ClearDestroyedWindowAwaitingHelpers();
            }

            SetStatus(selection.Status, selection.Handle, selection.Title);
        }
    }

    // Opt-in only (TreatMainWindowHideAsClose). Runs on the monitor thread.
    private void HandleSelectedWindowHidden(IntPtr hiddenWindow)
    {
        if (!_treatMainWindowHideAsClose || hiddenWindow != MainWindowHandle
            || Volatile.Read(ref _processExited) != 0 || !WindowNativeMethods.IsWindow(hiddenWindow))
        {
            return;
        }

        var title = MainWindowTitle;
        if (WindowNativeMethods.IsWindowVisible(hiddenWindow))
        {
            LogHideAsClose($"Hide-as-close cancelled: selected main window is visible again (hwnd=0x{hiddenWindow.ToInt64():X}, title={title})");
            return;
        }

        if (WindowNativeMethods.IsIconic(hiddenWindow))
        {
            LogHideAsClose($"Hide-as-close cancelled: selected main window is minimized (hwnd=0x{hiddenWindow.ToInt64():X}, title={title})");
            return;
        }

        var visibleFor = Stopwatch.GetElapsedTime(Interlocked.Read(ref _selectedVisibleSinceTimestamp));
        if (visibleFor < _hideAsCloseTimings.ArmDelay)
        {
            // Not armed: the window was not continuously visible long enough to be the real main
            // window (e.g. a welcome/splash window). Not a close; wait for the next main window.
            _unarmedHiddenWindow = hiddenWindow;
            LogHideAsClose($"Hide-as-close not armed: selected window hid after {visibleFor.TotalSeconds:F1} s visible (< {_hideAsCloseTimings.ArmDelay.TotalSeconds:F1} s); not a close, waiting for the next main window (hwnd=0x{hiddenWindow.ToInt64():X}, pid={GetWindowProcessId(hiddenWindow)}, title={title})");
            RefreshWindowSelection();
            return;
        }

        _hiddenWindowAwaitingConfirmation = hiddenWindow;
        _hiddenWindowHiddenAtTimestamp = Stopwatch.GetTimestamp();
        _replacementGraceLogged = false;
        SnapshotPreexistingJobWindows();
        ScheduleWindowCloseConfirmation(hiddenWindow);
    }

    // Opt-in only. Called by the shared 400 ms confirmation for a still-existing hidden window.
    private void ConfirmSelectedWindowHidden(IntPtr hiddenWindow)
    {
        if (hiddenWindow != MainWindowHandle || Volatile.Read(ref _processExited) != 0)
        {
            LogHideAsClose($"Hide-as-close cancelled: window selection changed or the process exited (hwnd=0x{hiddenWindow.ToInt64():X})");
            return;
        }

        var title = MainWindowTitle;
        if (WindowNativeMethods.IsWindowVisible(hiddenWindow))
        {
            LogHideAsClose($"Hide-as-close cancelled: selected main window was shown again (hwnd=0x{hiddenWindow.ToInt64():X}, title={title})");
            return;
        }

        if (WindowNativeMethods.IsIconic(hiddenWindow))
        {
            LogHideAsClose($"Hide-as-close cancelled: selected main window is minimized (hwnd=0x{hiddenWindow.ToInt64():X}, title={title})");
            return;
        }

        var selection = SelectMainWindow();
        if (selection.Status == WindowMonitorStatus.Monitoring && selection.Handle != hiddenWindow)
        {
            LogHideAsClose($"Hide-as-close cancelled: switched to another reliable main window (old hwnd=0x{hiddenWindow.ToInt64():X}, new hwnd=0x{selection.Handle.ToInt64():X}, title={selection.Title})");
            SetStatus(selection.Status, selection.Handle, selection.Title);
            return;
        }

        if (selection.Status == WindowMonitorStatus.AmbiguousWindows)
        {
            LogHideAsClose($"Hide-as-close cancelled: other eligible windows remain (hwnd=0x{hiddenWindow.ToInt64():X}, title={title})");
            SetStatus(selection.Status, selection.Handle, selection.Title);
            return;
        }

        if (TryFindVisibleWindowInOtherJobProcess(out var blockingProcessId, out var blockingTitle))
        {
            // Block only: the selection is never switched to another process's window.
            LogHideAsClose($"Hide-as-close blocked: Job process {blockingProcessId} still has a visible top-level window (title={blockingTitle}); not treating the hide as a close (hwnd=0x{hiddenWindow.ToInt64():X}, pid={GetWindowProcessId(hiddenWindow)}, title={title})");
            return;
        }

        var sinceHide = Stopwatch.GetElapsedTime(_hiddenWindowHiddenAtTimestamp);
        if (sinceHide < _hideAsCloseTimings.ReplacementGrace)
        {
            // Replacement grace: a new reliable window of the launched process appearing now
            // (e.g. login -> main UI) is selected by RefreshWindowSelection and cancels the close.
            // Cross-Job path: a NEW reliable window of another Job process counts the same way.
            if (!_replacementGraceLogged)
            {
                _replacementGraceLogged = true;
                LogHideAsClose($"Hide-as-close pending: waiting up to {_hideAsCloseTimings.ReplacementGrace.TotalSeconds:F1} s after the hide for a replacement main window (hwnd=0x{hiddenWindow.ToInt64():X}, title={title})");
            }

            _hiddenWindowAwaitingConfirmation = hiddenWindow;
            ScheduleWindowCloseConfirmation(hiddenWindow, _hideAsCloseTimings.ReplacementGrace - sinceHide);
            return;
        }

        LogHideAsClose($"Hide-as-close triggered: selected main window stayed hidden (hwnd=0x{hiddenWindow.ToInt64():X}, pid={GetWindowProcessId(hiddenWindow)}, title={title})");
        SetStatus(WindowMonitorStatus.WindowClosed, hiddenWindow, title);
    }

    // Opt-in only. Read-only check of the launch Job's other processes; never changes the selection.
    private bool TryFindVisibleWindowInOtherJobProcess(out uint processId, out string? title)
    {
        processId = 0;
        title = null;
        if (_jobProcessIdsProvider is null)
        {
            return false;
        }

        IReadOnlyList<uint> processIds;
        try
        {
            processIds = _jobProcessIdsProvider();
        }
        catch (Exception exception)
        {
            // E.g. the Job was already closed by cleanup. Fall back to the unchanged hide-as-close decision.
            AppLog.Write(LogLevel.Warning, "WindowMonitor", "Hide-as-close could not read the Job process list; continuing without the block check",
                processId: checked((int)_processId), exception: exception);
            return false;
        }

        foreach (var candidateProcessId in processIds)
        {
            if (candidateProcessId == 0 || candidateProcessId == _processId)
            {
                continue;
            }

            var selection = WindowSelector.Select(candidateProcessId);
            if (selection.Status is WindowMonitorStatus.Monitoring or WindowMonitorStatus.AmbiguousWindows)
            {
                processId = candidateProcessId;
                title = selection.Title ?? "(several equally sized windows)";
                return true;
            }
        }

        return false;
    }

    // Main-window selection. Option off (or no Job process list): exactly WindowSelector.Select(_processId).
    // Task 16, option on: the launched PID is always preferred (Monitoring or AmbiguousWindows of the
    // launched PID is returned unchanged). Only when the launched PID has no reliable window AND it has
    // shown one before (so the initial selection is still the launched process's own window) are the
    // other processes of the same Job considered, with the same rules (visible, unowned, not a tool
    // window, titled, non-zero area, largest wins). An exact size tie among them is not used (the
    // launched PID's result is returned), so a cross-process guess is never made.
    private WindowSelector.WindowSelection SelectMainWindow()
    {
        var own = WindowSelector.Select(_processId);
        if (!_considerJobProcessWindows)
        {
            return own;
        }

        if (own.Status == WindowMonitorStatus.Monitoring)
        {
            _launchedWindowSeen = true;
            return own;
        }

        if (own.Status == WindowMonitorStatus.AmbiguousWindows || !_launchedWindowSeen)
        {
            return own;
        }

        if (!TryReadJobProcessIds(out var jobProcessIds))
        {
            return own;
        }

        var otherProcessIds = new HashSet<uint>(jobProcessIds);
        otherProcessIds.Remove(0);
        otherProcessIds.Remove(_processId);
        var jobSelection = WindowSelector.SelectAmong(otherProcessIds, _preexistingJobWindows);
        return jobSelection.Status == WindowMonitorStatus.Monitoring ? jobSelection : own;
    }

    // Cross-Job path only: remember which Job windows were already visible when the selected window hid.
    private void SnapshotPreexistingJobWindows()
    {
        if (!_considerJobProcessWindows || !TryReadJobProcessIds(out var jobProcessIds))
        {
            _preexistingJobWindows = null;
            return;
        }

        var otherProcessIds = new HashSet<uint>(jobProcessIds);
        otherProcessIds.Remove(0);
        otherProcessIds.Remove(_processId);
        _preexistingJobWindows = WindowSelector.CollectCandidateWindows(otherProcessIds);
    }

    // Read-only Job process list for the cross-Job path; refreshes the membership cache.
    private bool TryReadJobProcessIds(out IReadOnlyList<uint> processIds)
    {
        processIds = Array.Empty<uint>();
        if (_jobProcessIdsProvider is null)
        {
            return false;
        }

        try
        {
            processIds = _jobProcessIdsProvider();
        }
        catch (Exception exception)
        {
            // E.g. the Job was already closed by cleanup: fall back to the launched-PID-only selection.
            if (!_jobProcessListWarningLogged)
            {
                _jobProcessListWarningLogged = true;
                AppLog.Write(LogLevel.Warning, "WindowMonitor", "Hide-as-close could not read the Job process list for main-window selection; using the launched process only",
                    processId: checked((int)_processId), exception: exception);
            }

            return false;
        }

        _jobProcessIdCache = new HashSet<uint>(processIds);
        return true;
    }

    // Monitor thread (out-of-context WinEvent callback). True for a non-launched Job process.
    private bool IsJobProcess(uint processId)
    {
        if (processId == 0 || processId == _processId)
        {
            return false;
        }

        if (_jobProcessIdCache.Contains(processId))
        {
            return true;
        }

        return TryReadJobProcessIds(out _) && _jobProcessIdCache.Contains(processId);
    }

    private static uint GetWindowProcessId(IntPtr window)
    {
        WindowNativeMethods.GetWindowThreadProcessId(window, out var processId);
        return processId;
    }

    private void LogHideAsClose(string message) =>
        AppLog.Write(LogLevel.Information, "WindowMonitor", message, processId: checked((int)_processId));

    private void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThreadId,
        uint eventTime)
    {
        if (Volatile.Read(ref _disposed) != 0 || objectId != WindowNativeMethods.ObjectIdWindow
            || childId != WindowNativeMethods.ChildIdSelf || window == IntPtr.Zero)
        {
            return;
        }

        if (eventType == WindowNativeMethods.EventObjectDestroy
            && window.ToInt64() == Interlocked.Read(ref _selectedWindowValue))
        {
            PostMonitorMessage(MessageLoopNativeMethods.WmAppWindowDestroyed, window);
            return;
        }

        // EVENT_OBJECT_HIDE is only delivered when the opt-in hook range includes it.
        if (eventType == WindowNativeMethods.EventObjectHide)
        {
            if (_treatMainWindowHideAsClose && window.ToInt64() == Interlocked.Read(ref _selectedWindowValue))
            {
                PostMonitorMessage(MessageLoopNativeMethods.WmAppSelectedWindowHidden, window);
            }

            return;
        }

        // Out-of-context WinEvent callbacks run on the hooking (monitor) thread, and the hook
        // is registered for this process only (cross-Job path: filtered here). Coalesce DESTROY
        // refreshes to avoid floods.
        if (eventType == WindowNativeMethods.EventObjectDestroy
            && _destroyedWindowAwaitingHelpers != IntPtr.Zero
            && !_helperRefreshQueued)
        {
            if (_considerJobProcessWindows)
            {
                // A destroyed window's PID often cannot be read any more; skip only events that
                // resolve to a process outside the launched PID and the Job.
                var destroyedProcessId = GetWindowProcessId(window);
                if (destroyedProcessId != 0 && destroyedProcessId != _processId && !IsJobProcess(destroyedProcessId))
                {
                    return;
                }
            }

            _helperRefreshQueued = true;
            PostMonitorMessage(MessageLoopNativeMethods.WmAppRefreshWindows, IntPtr.Zero);
            return;
        }

        if (eventType is WindowNativeMethods.EventObjectCreate or WindowNativeMethods.EventObjectShow)
        {
            WindowNativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId == _processId
                || (_considerJobProcessWindows && _launchedWindowSeen && IsJobProcess(processId)))
            {
                if (_treatMainWindowHideAsClose && eventType == WindowNativeMethods.EventObjectShow
                    && window.ToInt64() == Interlocked.Read(ref _selectedWindowValue))
                {
                    // Opt-in only: arming restarts whenever the selected window becomes visible again.
                    Interlocked.Exchange(ref _selectedVisibleSinceTimestamp, Stopwatch.GetTimestamp());
                }

                PostMonitorMessage(MessageLoopNativeMethods.WmAppRefreshWindows, IntPtr.Zero);
            }
        }
    }

    private void OnProcessExited(object? sender, EventArgs eventArgs)
    {
        Interlocked.Exchange(ref _processExited, 1);
        PostMonitorMessage(MessageLoopNativeMethods.WmAppProcessExited, IntPtr.Zero);
    }

    private void PostMonitorMessage(uint message, IntPtr parameter)
    {
        var threadId = _monitorThreadId;
        if (threadId == 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!MessageLoopNativeMethods.PostThreadMessage(threadId, message, UIntPtr.Zero, parameter)
            && message is MessageLoopNativeMethods.WmAppWindowDestroyed
                or MessageLoopNativeMethods.WmAppConfirmWindowClosed)
        {
            lock (_gate)
            {
                _monitoringException = new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The window-close notification could not be queued.");
            }

            SetStatus(WindowMonitorStatus.MonitoringFailed, IntPtr.Zero, null);
        }
    }

    private void SetStatus(WindowMonitorStatus status, IntPtr windowHandle, string? title)
    {
        var scheduleDrain = false;
        var logSelectedWindow = false;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if ((_status == WindowMonitorStatus.ProcessExited && status != WindowMonitorStatus.ProcessExited)
                || (_status == WindowMonitorStatus.WindowClosed
                    && status is not (WindowMonitorStatus.WindowClosed or WindowMonitorStatus.ProcessExited)))
            {
                return;
            }

            var currentHandle = new IntPtr(Interlocked.Read(ref _selectedWindowValue));
            if (_status == status && currentHandle == windowHandle && _windowTitle == title)
            {
                return;
            }

            if (status == WindowMonitorStatus.Monitoring && currentHandle != windowHandle)
            {
                Interlocked.Exchange(ref _selectedVisibleSinceTimestamp, Stopwatch.GetTimestamp());
                logSelectedWindow = _treatMainWindowHideAsClose;
            }

            if (currentHandle != windowHandle)
            {
                _preexistingJobWindows = null;
            }

            _status = status;
            _windowTitle = title;
            Interlocked.Exchange(ref _selectedWindowValue, windowHandle.ToInt64());
            _pendingNotifications.Enqueue(new WindowMonitorStatusChangedEventArgs(status, windowHandle, title));
            if (!_notificationDrainScheduled)
            {
                _notificationDrainScheduled = true;
                scheduleDrain = true;
            }
        }

        if (logSelectedWindow)
        {
            // Opt-in only (Task 16): record which window, in which process, is now the main window.
            var windowProcessId = GetWindowProcessId(windowHandle);
            LogHideAsClose($"Hide-as-close: main window selected (pid={windowProcessId}, hwnd=0x{windowHandle.ToInt64():X}, title={title})");
            if (windowProcessId != _processId)
            {
                LogHideAsClose($"Hide-as-close: selected main window belongs to non-launched Job process {windowProcessId} (launched pid={_processId}, hwnd=0x{windowHandle.ToInt64():X}, title={title})");
            }
        }

        if (!scheduleDrain)
        {
            return;
        }

        if (_synchronizationContext is not null)
        {
            try
            {
                _synchronizationContext.Post(static state => ((WindowMonitor)state!).DrainNotifications(), this);
                return;
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    _callbackException = exception;
                }
            }
        }

        ThreadPool.QueueUserWorkItem(static state => ((WindowMonitor)state!).DrainNotifications(), this);
    }

    private void DrainNotifications()
    {
        while (true)
        {
            WindowMonitorStatusChangedEventArgs eventArgs;
            lock (_gate)
            {
                if (_disposed != 0)
                {
                    _pendingNotifications.Clear();
                    _notificationDrainScheduled = false;
                    return;
                }

                if (_pendingNotifications.Count == 0)
                {
                    _notificationDrainScheduled = false;
                    return;
                }

                eventArgs = _pendingNotifications.Dequeue();
            }

            DeliverStatus(eventArgs);
        }
    }

    private void DeliverStatus(WindowMonitorStatusChangedEventArgs eventArgs)
    {
        var threadId = Environment.CurrentManagedThreadId;
        lock (_gate)
        {
            if (_disposed != 0)
            {
                return;
            }

            _callbackThreads[threadId] = _callbackThreads.GetValueOrDefault(threadId) + 1;
        }

        try
        {
            StatusChanged?.Invoke(this, eventArgs);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _callbackException = exception;
            }
        }
        finally
        {
            lock (_gate)
            {
                var count = _callbackThreads[threadId] - 1;
                if (count == 0)
                {
                    _callbackThreads.Remove(threadId);
                }
                else
                {
                    _callbackThreads[threadId] = count;
                }

                Monitor.PulseAll(_gate);
            }
        }
    }

    private sealed record WindowCloseRequest(WindowMonitor Monitor, IntPtr Window);
}

/// <summary>
/// Timings for the opt-in hide-as-close path. ArmDelay: how long the selected window must have been
/// continuously visible before a hide can count as a close. ReplacementGrace: how long after an armed
/// hide a new reliable window of the launched process (cross-Job path: or a new reliable window of another
/// process in the same Job) still counts as a switch (minimum 400 ms).
/// </summary>
internal readonly record struct HideAsCloseTimings(TimeSpan ArmDelay, TimeSpan ReplacementGrace)
{
    internal static HideAsCloseTimings Default { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2));
}
