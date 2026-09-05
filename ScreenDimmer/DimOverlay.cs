namespace ScreenDimmer;

internal sealed class DimOverlay : Win32Window
{
    private const string ClassName = "ScreenDimmer_DimOverlay";

    private IntPtr brush_;
    private double dim_;
    private bool visible_;
    private bool inverted_;

    internal DimOverlay(string monitorId)
        : base(
            ClassName,
            style: Win32.WS_POPUP,
            exStyle: Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT |
                     Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW)
    {
        MonitorId = monitorId;
        brush_ = Win32.CreateSolidBrush(Win32.Rgb(0, 0, 0));
    }

    internal string MonitorId { get; }

    protected override bool WndProc(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;

        if (message != Win32.WM_ERASEBKGND)
        {
            return false;
        }

        if (Win32.GetClientRect(Handle, out var client))
        {
            Win32.FillRect(wParam, in client, brush_);
        }

        result = new IntPtr(1);
        return true;
    }

    internal void ApplyBounds(in Win32.RECT bounds)
    {
        Win32.SetWindowPos(
            Handle, Win32.HWND_TOPMOST,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            Win32.SWP_NOACTIVATE);
    }

    // Under inversion a black overlay brightens the screen (1-S(1-a)); white
    // composes to (1-a)(1-S) after invert, the correct dim on the other side.
    internal void SetInverted(bool inverted)
    {
        if (inverted_ == inverted)
        {
            return;
        }

        var replacement = Win32.CreateSolidBrush(
            inverted ? Win32.Rgb(255, 255, 255) : Win32.Rgb(0, 0, 0));

        if (replacement == IntPtr.Zero)
        {
            return;
        }

        inverted_ = inverted;

        var previous = brush_;
        brush_ = replacement;
        Win32.DeleteObject(previous);

        if (visible_)
        {
            Win32.InvalidateRect(Handle, IntPtr.Zero, true);
        }
    }

    internal void ApplyDim(double dim)
    {
        dim = Math.Clamp(dim, 0.0, Program.CurrentConfig.MaximumDim);
        if (Math.Abs(dim - dim_) < 0.001 && (dim > 0) == visible_)
        {
            return;
        }

        dim_ = dim;

        if (dim <= 0.001)
        {
            if (visible_)
            {
                Win32.ShowWindow(Handle, Win32.SW_HIDE);
                visible_ = false;
            }

            return;
        }

        var alpha = (byte)Math.Clamp(Math.Round(dim * 255.0), 0, 255);
        Win32.SetLayeredWindowAttributes(Handle, 0, alpha, Win32.LWA_ALPHA);

        if (!visible_)
        {
            Win32.ShowWindow(Handle, Win32.SW_SHOWNOACTIVATE);
            visible_ = true;

            Win32.SetWindowPos(
                Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOACTIVATE | Win32.SWP_NOSIZE | Win32.SWP_NOMOVE);
        }
    }

    public override void Dispose()
    {
        base.Dispose();

        if (brush_ != IntPtr.Zero)
        {
            Win32.DeleteObject(brush_);
            brush_ = IntPtr.Zero;
        }
    }
}
