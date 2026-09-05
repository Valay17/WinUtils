namespace ScreenDimmer;

internal sealed unsafe class TrayIcon : IDisposable
{
    private const int IconSize = 16;

    private const int Supersample = 4;
    private const int BigSize = IconSize * Supersample;

    // szTip is 128 chars under NOTIFYICON_VERSION_4; this leaves room for the terminator.
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
        var data = NewData(
            Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        data.uCallbackMessage = Win32.WM_TRAYICON;
        data.hIcon = icon_;
        Win32.CopyToFixed(data.szTip, 128, tooltip_);

        if (!Win32.Shell_NotifyIconW(added_ ? Win32.NIM_MODIFY : Win32.NIM_ADD, ref data))
        {
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

        var data = NewData(Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        Win32.CopyToFixed(data.szTip, 128, tooltip_);
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
    }

    internal void SetDim(double dim)
    {
        var replacement = CreateIcon(dim);
        if (replacement == IntPtr.Zero)
        {
            return;
        }

        var previous = icon_;
        icon_ = replacement;

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
        var identifier = new Win32.NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)sizeof(Win32.NOTIFYICONIDENTIFIER),
            hWnd = window_,
            uID = id_,
        };

        if (Win32.Shell_NotifyIconGetRect(in identifier, out var rect) != 0)
        {
            return null;
        }

        return new Win32.POINT { X = rect.Left, Y = rect.Top };
    }

    private Win32.NOTIFYICONDATAW NewData(uint flags) => new()
    {
        cbSize = (uint)sizeof(Win32.NOTIFYICONDATAW),
        hWnd = window_,
        uID = id_,
        uFlags = flags,
    };

    private static IntPtr CreateIcon(double dim)
    {
        var screen = Win32.GetDC(IntPtr.Zero);

        var bigDc = Win32.CreateCompatibleDC(screen);
        var bigBitmap = Win32.CreateCompatibleBitmap(screen, BigSize, BigSize);
        var previousBig = Win32.SelectObject(bigDc, bigBitmap);

        var bigBounds = new Win32.RECT { Left = 0, Top = 0, Right = BigSize, Bottom = BigSize };
        var bigBackground = Win32.CreateSolidBrush(Win32.Rgb(0, 0, 0));
        Win32.FillRect(bigDc, in bigBounds, bigBackground);
        Win32.DeleteObject(bigBackground);

        var outlinePen = Win32.CreatePen(Win32.PS_SOLID, Supersample, Win32.Rgb(255, 255, 255));
        var previousPen = Win32.SelectObject(bigDc, outlinePen);
        // (1,1,1), not (0,0,0): the mask keys on exact black, so true black here would be cut out as transparent.
        var hollow = Win32.CreateSolidBrush(Win32.Rgb(1, 1, 1));
        var previousBrush = Win32.SelectObject(bigDc, hollow);
        Win32.Ellipse(bigDc, 2 * Supersample, 2 * Supersample, (IconSize - 2) * Supersample, (IconSize - 2) * Supersample);

        if (dim > 0.001)
        {
            // 11: fill band height in 16px-icon units, before supersampling.
            var height = (int)Math.Round(11 * Supersample * Math.Clamp(dim / Program.CurrentConfig.MaximumDim, 0, 1));
            if (height > 0)
            {
                var region = Win32.CreateRectRgn(
                    2 * Supersample, 2 * Supersample + (11 * Supersample - height),
                    (IconSize - 2) * Supersample, (IconSize - 2) * Supersample);
                Win32.SelectClipRgn(bigDc, region);

                var fill = Win32.CreateSolidBrush(Win32.Rgb(190, 190, 190));
                var previousFill = Win32.SelectObject(bigDc, fill);
                Win32.Ellipse(bigDc, 2 * Supersample, 2 * Supersample, (IconSize - 2) * Supersample, (IconSize - 2) * Supersample);
                Win32.SelectObject(bigDc, previousFill);
                Win32.DeleteObject(fill);

                Win32.SelectClipRgn(bigDc, IntPtr.Zero);
                Win32.DeleteObject(region);
            }
        }

        Win32.SelectObject(bigDc, previousBrush);
        Win32.DeleteObject(hollow);
        Win32.SelectObject(bigDc, previousPen);
        Win32.DeleteObject(outlinePen);

        var memory = Win32.CreateCompatibleDC(screen);
        var color = Win32.CreateCompatibleBitmap(screen, IconSize, IconSize);
        var mask = Win32.CreateBitmap(IconSize, IconSize, 1, 1, IntPtr.Zero);
        var previous = Win32.SelectObject(memory, color);

        Win32.SetStretchBltMode(memory, Win32.HALFTONE);
        Win32.SetBrushOrgEx(memory, 0, 0, IntPtr.Zero);
        Win32.StretchBlt(memory, 0, 0, IconSize, IconSize, bigDc, 0, 0, BigSize, BigSize, Win32.SRCCOPY);

        Win32.SelectObject(bigDc, previousBig);
        Win32.DeleteObject(bigBitmap);
        Win32.DeleteDC(bigDc);

        var maskDc = Win32.CreateCompatibleDC(screen);
        var previousMask = Win32.SelectObject(maskDc, mask);
        Win32.SetBkColor(memory, Win32.Rgb(0, 0, 0));
        Win32.BitBlt(maskDc, 0, 0, IconSize, IconSize, memory, 0, 0, Win32.SRCCOPY);
        Win32.SelectObject(maskDc, previousMask);
        Win32.DeleteDC(maskDc);

        // memory must stay selected with `color` until the mask BitBlt above reads from it.
        Win32.SelectObject(memory, previous);

        var info = new Win32.ICONINFO
        {
            fIcon = 1,
            hbmMask = mask,
            hbmColor = color,
        };

        var handle = Win32.CreateIconIndirect(in info);

        Win32.DeleteObject(color);
        Win32.DeleteObject(mask);
        Win32.DeleteDC(memory);
        Win32.ReleaseDC(IntPtr.Zero, screen);

        return handle;
    }

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
