using System.Runtime.InteropServices;
using System.Text;
using ProcessGuardian.Interop;

namespace ProcessGuardian.Services.WindowMonitoring;

internal static class WindowSelector
{
    private const int MaximumTitleLength = 1024;

    internal static WindowSelection Select(uint processId)
    {
        var context = Enumerate(new HashSet<uint> { processId });
        return Choose(context.Candidates, context.TargetWindowCount);
    }

    // Task 16: used only when TreatMainWindowHideAsClose is on. Applies exactly the same
    // per-window rules as Select(uint), but across a set of process IDs (the non-launched
    // members of the managed Job Object). Windows in excludedWindows are ignored.
    internal static WindowSelection SelectAmong(IReadOnlyCollection<uint> processIds, IReadOnlySet<IntPtr>? excludedWindows)
    {
        if (processIds.Count == 0)
        {
            return new WindowSelection(WindowMonitorStatus.WaitingForWindow, IntPtr.Zero, null);
        }

        var context = Enumerate(new HashSet<uint>(processIds));
        var candidates = excludedWindows is null || excludedWindows.Count == 0
            ? context.Candidates
            : context.Candidates.Where(candidate => !excludedWindows.Contains(candidate.Handle)).ToList();
        return Choose(candidates, context.TargetWindowCount);
    }

    // Task 16: handles of every currently qualifying main-window candidate owned by any of processIds.
    internal static HashSet<IntPtr> CollectCandidateWindows(IReadOnlyCollection<uint> processIds)
    {
        var result = new HashSet<IntPtr>();
        if (processIds.Count == 0)
        {
            return result;
        }

        foreach (var candidate in Enumerate(new HashSet<uint>(processIds)).Candidates)
        {
            result.Add(candidate.Handle);
        }

        return result;
    }

    private static EnumerationContext Enumerate(HashSet<uint> processIds)
    {
        var candidates = new List<WindowCandidate>();
        var context = new EnumerationContext(processIds, candidates);
        var contextHandle = GCHandle.Alloc(context);
        try
        {
            if (!WindowNativeMethods.EnumWindows(CollectCandidate, GCHandle.ToIntPtr(contextHandle)))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "EnumWindows failed.");
            }
        }
        finally
        {
            contextHandle.Free();
        }

        return context;
    }

    private static bool CollectCandidate(IntPtr window, IntPtr parameter)
    {
        var handle = GCHandle.FromIntPtr(parameter);
        var context = (EnumerationContext)handle.Target!;

        if (!WindowNativeMethods.IsWindow(window))
        {
            return true;
        }

        WindowNativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (!context.ProcessIds.Contains(processId))
        {
            return true;
        }

        context.TargetWindowCount++;
        if (!WindowNativeMethods.IsWindowVisible(window))
        {
            return true;
        }

        // EnumWindows returns top-level windows only. Exclude owned auxiliary windows
        // and windows explicitly marked as tool windows.
        if (WindowNativeMethods.GetWindow(window, WindowNativeMethods.GwOwner) != IntPtr.Zero)
        {
            return true;
        }

        var extendedStyle = WindowNativeMethods.GetWindowLongPtr(window, WindowNativeMethods.GwlExStyle).ToInt64();
        if ((extendedStyle & WindowNativeMethods.WsExToolWindow) != 0)
        {
            return true;
        }

        var titleBuffer = new StringBuilder(MaximumTitleLength);
        WindowNativeMethods.GetWindowText(window, titleBuffer, titleBuffer.Capacity);
        var title = titleBuffer.ToString().Trim();
        if (title.Length == 0 || !WindowNativeMethods.GetWindowRect(window, out var rectangle))
        {
            return true;
        }

        var width = Math.Max(0L, (long)rectangle.Right - rectangle.Left);
        var height = Math.Max(0L, (long)rectangle.Bottom - rectangle.Top);
        var area = width * height;
        if (area == 0)
        {
            return true;
        }

        context.Candidates.Add(new WindowCandidate(window, title, area, processId));
        return true;
    }

    private static WindowSelection Choose(List<WindowCandidate> candidates, int targetWindowCount)
    {
        if (candidates.Count == 0)
        {
            var status = targetWindowCount == 0
                ? WindowMonitorStatus.WaitingForWindow
                : WindowMonitorStatus.NoReliableWindow;
            return new WindowSelection(status, IntPtr.Zero, null);
        }

        var ranked = candidates.OrderByDescending(candidate => candidate.Area).ToArray();
        if (ranked.Length > 1 && ranked[0].Area == ranked[1].Area)
        {
            return new WindowSelection(WindowMonitorStatus.AmbiguousWindows, IntPtr.Zero, null);
        }

        var selected = ranked[0];
        return new WindowSelection(WindowMonitorStatus.Monitoring, selected.Handle, selected.Title, selected.ProcessId);
    }

    private sealed class EnumerationContext(HashSet<uint> processIds, List<WindowCandidate> candidates)
    {
        internal HashSet<uint> ProcessIds { get; } = processIds;

        internal List<WindowCandidate> Candidates { get; } = candidates;

        internal int TargetWindowCount { get; set; }
    }

    private sealed record WindowCandidate(IntPtr Handle, string Title, long Area, uint ProcessId);

    // ProcessId is the owner of Handle when Status is Monitoring; otherwise 0.
    internal sealed record WindowSelection(WindowMonitorStatus Status, IntPtr Handle, string? Title, uint ProcessId = 0);
}
