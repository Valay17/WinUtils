namespace BluetoothBattery;

internal sealed unsafe partial class TrayIcon : IDisposable
{
    private const int IconSize = 16;

    // szTip is 128 characters under NOTIFYICON_VERSION_4; this leaves room
    // for the terminator.
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
        // NIF_SHOWTIP is not optional here: NIM_SETVERSION below switches to
        // version 4, which turns off the standard tooltip unless this flag
        // asks for it back.
        var data = NewData(
            Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        data.uCallbackMessage = Win32.WM_TRAYICON;
        data.hIcon = icon_;
        Win32.CopyToFixed(data.szTip, 128, tooltip_);

        if (!Win32.Shell_NotifyIconW(added_ ? Win32.NIM_MODIFY : Win32.NIM_ADD, ref data))
        {
            // A failed NIM_MODIFY usually means the icon is gone - after an
            // Explorer restart, for instance - so fall back to adding it.
            added_ = false;
            if (!Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref data))
            {
                Diagnostics.Write("Shell_NotifyIcon could not add the tray icon.");
                return;
            }
        }

        added_ = true;

        // Version 4 gives the click position in the message. Failure is not
        // fatal - the icon still works, it just reports clicks the old way.
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

        // NIF_SHOWTIP on every update, not just the first: under version 4 a
        // NIM_MODIFY carrying NIF_TIP alone supplies new text that the shell
        // then declines to display.
        var data = NewData(Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        Win32.CopyToFixed(data.szTip, 128, tooltip_);
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
    }

    // Takes ownership of icon and destroys the one it replaces - but only
    // after the shell has been handed the new one, since destroying an icon
    // the shell is still drawing leaves a blank square in the tray.
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

        // Returns S_OK (0) on success. It fails while the icon is in the
        // overflow flyout and that flyout is closed - not an error, just no
        // screen position right now.
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
