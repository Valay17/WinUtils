namespace BluetoothBattery;

internal sealed unsafe partial class TrayIcon : IDisposable
{
    private const int IconSize = 16;

    // szTip is 128 characters under NOTIFYICON_VERSION_4; leave room for the terminator.
    private const int MaxTooltipLength = 127;

    private readonly IntPtr window_;
    private readonly uint id_;

    private IntPtr icon_;
    private string tooltip_ = string.Empty;
    private bool added_;
    private bool disposed_;

    internal TrayIcon(IntPtr window, uint id = 1)
    {
        window_ = window;
        id_ = id;
    }

    internal void Show()
    {
        // NIF_SHOWTIP is required - NIM_SETVERSION below switches to version 4,
        // which turns the standard tooltip off unless this flag asks for it back.
        var data = NewData(
            Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        data.uCallbackMessage = Win32.WM_TRAYICON;
        data.hIcon = icon_;
        Win32.CopyToFixed(data.szTip, 128, tooltip_);

        if (!Win32.Shell_NotifyIconW(added_ ? Win32.NIM_MODIFY : Win32.NIM_ADD, ref data))
        {
            // A failed NIM_MODIFY usually means the icon is gone - fall back to adding it.
            added_ = false;
            if (!Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref data))
            {
                Diagnostics.Write("Shell_NotifyIcon could not add the tray icon.");
                return;
            }
        }

        added_ = true;

        var version = NewData(0);
        version.uVersionOrTimeout = Win32.NOTIFYICON_VERSION_4;
        Win32.Shell_NotifyIconW(Win32.NIM_SETVERSION, ref version);
    }

    internal void SetTooltip(string tooltip)
    {
        tooltip_ = tooltip.Length <= MaxTooltipLength
            ? tooltip
            : tooltip[..(MaxTooltipLength - 3)] + "...";

        if (!added_)
        {
            return;
        }

        // NIF_SHOWTIP on every update, not just the first - the suppression is a
        // property of the icon under version 4.
        var data = NewData(Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        Win32.CopyToFixed(data.szTip, 128, tooltip_);
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
    }

    internal void SetIcon(IntPtr icon)
    {
        if (icon == IntPtr.Zero)
        {
            return;
        }

        var previous = icon_;
        icon_ = icon;

        if (added_)
        {
            var data = NewData(Win32.NIF_ICON);
            data.hIcon = icon_;
            Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
        }

        if (previous != IntPtr.Zero)
        {
            Win32.DestroyIcon(previous);
        }
    }

    internal Win32.POINT? Anchor()
    {
        return AnchorRect() is { } rect
            ? new Win32.POINT { X = rect.Left, Y = rect.Top }
            : null;
    }

    internal Win32.RECT? AnchorRect()
    {
        var identifier = new Win32.NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)sizeof(Win32.NOTIFYICONIDENTIFIER),
            hWnd = window_,
            uID = id_,
        };

        // Returns S_OK (0) on success; fails while the icon is in a closed overflow
        // flyout, which is not an error - it just has no screen position right now.
        return Win32.Shell_NotifyIconGetRect(in identifier, out var rect) == 0
            ? rect
            : null;
    }

    private Win32.NOTIFYICONDATAW NewData(uint flags) => new()
    {
        cbSize = (uint)sizeof(Win32.NOTIFYICONDATAW),
        hWnd = window_,
        uID = id_,
        uFlags = flags,
    };

    public void Dispose()
    {
        if (disposed_)
        {
            return;
        }

        disposed_ = true;

        if (added_)
        {
            var data = NewData(0);
            Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref data);
            added_ = false;
        }

        if (icon_ != IntPtr.Zero)
        {
            Win32.DestroyIcon(icon_);
            icon_ = IntPtr.Zero;
        }
    }
}
