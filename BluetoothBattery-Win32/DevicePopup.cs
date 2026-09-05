using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace BluetoothBattery;

internal sealed partial class DevicePopup : Win32Window
{
    private const string ClassName = "BluetoothBattery_Popup";

    private const int PopupWidth = 300;

    private const int TopPadding = 8;

    private const int RowHeight = 62;

    private const int RowNameTop = 4;

    private const int RowBarTop = 26;

    private const int RowBarHeight = 6;

    private const int RowDetailTop = RowBarTop + RowBarHeight + 10;

    private const int FooterHeight = 28;
    private const int EdgePadding = 12;
    private const int ScreenMargin = 8;

    private const int EmptyStateHeight = 30;

    private IReadOnlyList<DeviceView> devices_ = Array.Empty<DeviceView>();
    private RadioState radioState_ = RadioState.Unknown;
    private bool refreshing_;
    private bool visible_;

    private PaintResources? paint_;

    private Func<Win32.RECT?>? anchor_;

    internal DevicePopup()
        : base(ClassName,
               style: Win32.WS_POPUP,
               // TOOLWINDOW keeps it out of Alt-Tab; TOPMOST keeps it above the taskbar.
               exStyle: Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST,
               width: PopupWidth,
               height: TopPadding + EmptyStateHeight + FooterHeight)
    {
    }

    internal bool Visible => visible_;

    internal void AnchorTo(Func<Win32.RECT?> anchor) => anchor_ = anchor;

    internal void ShowDevices(
        IReadOnlyList<DeviceView> devices,
        RadioState radioState,
        bool refreshing)
    {
        using var _timing = Timing.Measure($"popup.ShowDevices({devices.Count})");

        devices_ = devices;
        radioState_ = radioState;
        refreshing_ = refreshing;

        var wanted = HeightFor(devices.Count, radioState);

        if (!visible_)
        {
            PositionNearTray(wanted);
            Win32.ShowWindow(Handle, Win32.SW_SHOWNOACTIVATE);
            visible_ = true;

            TrayInterop.ForceForeground(Handle);
        }
        else
        {
            PositionNearTray(wanted);
        }

        Win32.InvalidateRect(Handle, IntPtr.Zero, true);
    }

    internal void Hide()
    {
        if (!visible_)
        {
            return;
        }

        visible_ = false;
        Win32.ShowWindow(Handle, Win32.SW_HIDE);
    }

    private static int HeightFor(int deviceCount, RadioState radioState)
    {
        if (radioState != RadioState.On)
        {
            return TopPadding + EmptyStateHeight;
        }

        return TopPadding
             + (deviceCount > 0 ? deviceCount * RowHeight : EmptyStateHeight)
             + FooterHeight;
    }

    private void PositionNearTray(int height)
    {
        var iconRect = anchor_?.Invoke();

        var probe = iconRect is { } r
            ? new Win32.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 }
            : (Win32.GetCursorPos(out var cursor) ? cursor : default);

        var monitor = Win32.MonitorFromPoint(probe, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFOEXW
        {
            cbSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<Win32.MONITORINFOEXW>(),
        };

        if (!Win32.GetMonitorInfoW(monitor, ref info))
        {
            Win32.SetWindowPos(
                Handle, Win32.HWND_TOPMOST, 0, 0, PopupWidth, height,
                Win32.SWP_NOACTIVATE | Win32.SWP_NOMOVE);
            return;
        }

        var work = info.rcWork;

        var x = iconRect is { } icon ? icon.Left : work.Right - PopupWidth - ScreenMargin;
        var y = iconRect is { } above ? above.Top - height - ScreenMargin
                                      : work.Bottom - height - ScreenMargin;

        x = Math.Clamp(x, work.Left + ScreenMargin, Math.Max(work.Left + ScreenMargin, work.Right - PopupWidth - ScreenMargin));
        y = Math.Clamp(y, work.Top + ScreenMargin, Math.Max(work.Top + ScreenMargin, work.Bottom - height - ScreenMargin));

        Win32.SetWindowPos(
            Handle, Win32.HWND_TOPMOST, x, y, PopupWidth, height,
            Win32.SWP_NOACTIVATE);
    }

    protected override bool WndProc(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;

        switch (message)
        {
            case Win32.WM_PAINT:
                OnPaint();
                return true;

            case Win32.WM_ACTIVATE:
                // WA_INACTIVE in the low word means the popup is losing activation.
                if (((long)wParam & 0xFFFF) == Win32.WA_INACTIVE && visible_)
                {
                    Hide();
                }

                return false;

            case Win32.WM_KEYDOWN:
                if ((int)wParam == Win32.VK_ESCAPE)
                {
                    Hide();
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    private void OnPaint()
    {
        var dc = Win32.BeginPaint(Handle, out var ps);
        if (dc == IntPtr.Zero)
        {
            return;
        }

        try
        {
            using var g = Graphics.FromHdc(dc);
            Draw(g);
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Painting the popup failed: {ex.Message}");
        }
        finally
        {
            Win32.EndPaint(Handle, in ps);
        }
    }

    private void Draw(Graphics g)
    {
        var paint = PaintKit;

        Win32.GetClientRect(Handle, out var client);
        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        g.FillRectangle(paint.Background, 0, 0, width, height);
        g.DrawRectangle(paint.Border, 0, 0, width - 1, height - 1);

        var y = TopPadding;

        if (radioState_ != RadioState.On)
        {
            g.DrawString("Bluetooth is OFF", paint.Body, paint.Gray, EdgePadding, y);
        }
        else if (devices_.Count == 0)
        {
            g.DrawString("No Bluetooth Device Connected", paint.Body, paint.Gray, EdgePadding, y);
        }
        else
        {
            foreach (var device in devices_)
            {
                DrawDeviceRow(g, paint, device, y, width);
                y += RowHeight;
            }
        }

        if (radioState_ != RadioState.On)
        {
            return;
        }

        var footer = refreshing_
            ? "Refreshing..."
            : "Values read on open. Click the icon to refresh.";
        g.DrawString(footer, paint.Muted, paint.Gray,
                     EdgePadding, height - FooterHeight + 6);
    }

    private static void DrawDeviceRow(
        Graphics g, PaintResources paint, DeviceView device, int top, int width)
    {
        var nameFont = paint.Bold;

        g.DrawString(TrimToWidth(g, device.Name, nameFont, width - 90),
                     nameFont, paint.Text, EdgePadding, top + RowNameTop);

        var battery = device.BatteryText;
        var batteryFont = paint.Bold;

        // Measured in the font it is drawn in - bold is wider than regular.
        var batterySize = g.MeasureString(battery, batteryFont);
        g.DrawString(battery, batteryFont, paint.Text,
                     width - EdgePadding - batterySize.Width, top + RowNameTop);

        var barY = top + RowBarTop;
        var barWidth = width - (EdgePadding * 2);
        g.FillRectangle(paint.Track, EdgePadding, barY, barWidth, RowBarHeight);

        if (device.BatteryPercent is { } percent)
        {
            var filled = (int)Math.Round(barWidth * (Math.Clamp(percent, 0, 100) / 100.0));
            if (filled > 0)
            {
                g.FillRectangle(paint.BatteryFillFor(percent), EdgePadding, barY, filled, RowBarHeight);
            }
        }

        var detail = $"Last Updated - {device.LastUpdatedText}";
        g.DrawString(detail, paint.Muted, paint.Gray, EdgePadding, top + RowDetailTop);
    }

    private static string TrimToWidth(Graphics g, string value, Font font, float maxWidth)
    {
        if (g.MeasureString(value, font).Width <= maxWidth)
        {
            return value;
        }

        // low is a length known to fit, high one known not to.
        var low = 1;
        var high = value.Length;

        while (low < high)
        {
            // Rounded up so the range always shrinks when high == low + 1.
            var middle = low + ((high - low + 1) / 2);

            if (g.MeasureString(string.Concat(value.AsSpan(0, middle), "..."), font).Width <= maxWidth)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return string.Concat(value.AsSpan(0, low), "...");
    }

    private PaintResources PaintKit => paint_ ??= new PaintResources();

    internal void DiscardPaintResources()
    {
        paint_?.Dispose();
        paint_ = null;

        if (visible_)
        {
            Win32.InvalidateRect(Handle, IntPtr.Zero, true);
        }
    }

    private sealed partial class PaintResources : IDisposable
    {
        internal Font Body { get; } = new(SystemFonts.MessageBoxFont!.FontFamily, 9f);
        internal Font Bold { get; } = new(SystemFonts.MessageBoxFont!.FontFamily, 9f, FontStyle.Bold);
        internal Font Muted { get; } = new(SystemFonts.MessageBoxFont!.FontFamily, 8f);

        internal SolidBrush Text { get; } = new(Color.White);
        internal SolidBrush Gray { get; } = new(Color.FromArgb(180, 180, 180));
        internal SolidBrush Track { get; } = new(Color.FromArgb(60, 60, 60));
        internal SolidBrush Background { get; } = new(Color.FromArgb(0x2b, 0x2b, 0x2b));
        internal Pen Border { get; } = new(Color.FromArgb(80, 80, 80));

        internal SolidBrush LowBatteryFill { get; } = new(BatteryIcon.ColorFor(BatteryIcon.LowBatteryPercent));
        internal SolidBrush NormalBatteryFill { get; } = new(BatteryIcon.ColorFor(100));

        internal SolidBrush BatteryFillFor(int percent) =>
            percent <= BatteryIcon.LowBatteryPercent ? LowBatteryFill : NormalBatteryFill;

        public void Dispose()
        {
            Body.Dispose();
            Bold.Dispose();
            Muted.Dispose();
            Text.Dispose();
            Gray.Dispose();
            Track.Dispose();
            Background.Dispose();
            Border.Dispose();
            LowBatteryFill.Dispose();
            NormalBatteryFill.Dispose();
        }
    }

    public override void Dispose()
    {
        paint_?.Dispose();
        paint_ = null;
        base.Dispose();
    }
}
