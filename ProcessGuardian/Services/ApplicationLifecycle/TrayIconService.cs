using System.Drawing;
using System.Windows.Forms;

namespace ProcessGuardian.Services.ApplicationLifecycle;

public sealed class TrayIconService : ITrayIconService
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly ContextMenuStrip _contextMenu;
    private bool _disposed;

    public TrayIconService(Icon icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        Icon? clonedIcon = null;
        ContextMenuStrip? contextMenu = null;
        NotifyIcon? notifyIcon = null;
        try
        {
            clonedIcon = (Icon)icon.Clone();
            contextMenu = new ContextMenuStrip();
            notifyIcon = new NotifyIcon
            {
                Icon = clonedIcon,
                Text = "Process Guardian",
                ContextMenuStrip = contextMenu,
                Visible = true,
            };
            notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

            contextMenu.Items.Add("Open Process Guardian", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
            contextMenu.Items.Add("Applications", null, (_, _) => ApplicationsRequested?.Invoke(this, EventArgs.Empty));
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
#if PG_FAULT_INJECTION
            ProcessGuardian.StartupFaultInjection.ThrowIfRequested(ProcessGuardian.StartupFaultInjection.TrayAfterVisible);
#endif
        }
        catch
        {
            // Release whatever was created before the failure, then let the original exception propagate.
            // A cleanup failure here must not replace that exception, so each step is attempted separately.
            if (notifyIcon is not null)
            {
                try { notifyIcon.Visible = false; } catch (Exception cleanupException) { System.Diagnostics.Trace.WriteLine("Tray icon cleanup (hide) failed: " + cleanupException); }
                try { notifyIcon.Dispose(); } catch (Exception cleanupException) { System.Diagnostics.Trace.WriteLine("Tray icon cleanup (NotifyIcon) failed: " + cleanupException); }
            }

            try { contextMenu?.Dispose(); } catch (Exception cleanupException) { System.Diagnostics.Trace.WriteLine("Tray icon cleanup (menu) failed: " + cleanupException); }
            try { clonedIcon?.Dispose(); } catch (Exception cleanupException) { System.Diagnostics.Trace.WriteLine("Tray icon cleanup (icon) failed: " + cleanupException); }
            throw;
        }

        _icon = clonedIcon;
        _contextMenu = contextMenu;
        _notifyIcon = notifyIcon;
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ApplicationsRequested;

    public event EventHandler? ExitRequested;

    public bool IsVisible => !_disposed && _notifyIcon.Visible;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _icon.Dispose();
    }
}
