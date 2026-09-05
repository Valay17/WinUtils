using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ScreenDimmer;

internal sealed unsafe class CustomValuePopup : Win32Window
{
    private const string ClassName = "ScreenDimmer_CustomValuePopup";
    private const int PopupWidth = 300;
    private const int PopupHeight = 130;
    private const int TrackMin = 0;

    private static readonly int TrackMax = (int)Math.Round(Program.CurrentConfig.MaximumDim * 100);

    private const int ScreenMargin = 8;

    private static readonly uint ColorBackground = Win32.Rgb(0x2b, 0x2b, 0x2b);
    private static readonly uint ColorBorder = Win32.Rgb(80, 80, 80);
    private static readonly uint ColorText = Win32.Rgb(240, 240, 240);
    private static readonly uint ColorMuted = Win32.Rgb(180, 180, 180);
    private static readonly uint ColorChannel = Win32.Rgb(60, 60, 60);
    private static readonly uint ColorAccent = Win32.Rgb(0x00, 0x78, 0xd4);
    private static readonly uint ColorChipFill = Win32.Rgb(0x3a, 0x3a, 0x3a);

    private const string FooterText = "Hold Ctrl to Snap to Nearest 5%.";
    private const int CtrlExtraGap = 4;

    private static bool commonControlsInitialized_;

    // TrayController owns a single instance, so one static slot is enough to
    // recover it from the [UnmanagedCallersOnly] hook callbacks below, which
    // cannot close over an instance.
    private static CustomValuePopup? activeForHooks_;

    private readonly IntPtr backgroundBrush_;
    private readonly IntPtr borderBrush_;

    private readonly IntPtr channelBrush_;
    private readonly IntPtr thumbBrush_;

    private readonly IntPtr trackbar_;
    private IntPtr keyboardHook_;
    private IntPtr mouseHook_;

    private string targetLabel_ = string.Empty;
    private int currentPercent_;
    private int lastCommittedPercent_;
    private bool dragging_;
    private bool visible_;
    private Action<int>? onCommit_;

    internal CustomValuePopup()
        : base(ClassName, style: Win32.WS_POPUP, exStyle: Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
               width: PopupWidth, height: PopupHeight)
    {
        backgroundBrush_ = Win32.CreateSolidBrush(ColorBackground);
        borderBrush_ = Win32.CreateSolidBrush(ColorBorder);

        channelBrush_ = Win32.CreateSolidBrush(ColorChannel);
        thumbBrush_ = Win32.CreateSolidBrush(ColorAccent);

        EnsureCommonControlsInitialized();
        trackbar_ = CreateTrackbar();
        InstallTrackbarSubclass();
    }

    internal void Show(string targetLabel, int currentPercent, in Win32.RECT anchor, Action<int> onCommit)
    {
        targetLabel_ = targetLabel;
        currentPercent_ = Math.Clamp(currentPercent, TrackMin, TrackMax);
        lastCommittedPercent_ = currentPercent_;
        onCommit_ = onCommit;
        dragging_ = false;

        Win32.SendMessageW(trackbar_, Win32.TBM_SETPOS, new IntPtr(1), new IntPtr(currentPercent_));

        // -3: horizontal correction for the themed popup-menu border, which
        // GetMenuItemRect (a pre-visual-styles API) does not account for.
        var x = anchor.Left - 3;
        var y = anchor.Bottom;

        var anchorPoint = new Win32.POINT { X = anchor.Left, Y = anchor.Top };
        var monitor = Win32.MonitorFromPoint(anchorPoint, Win32.MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new Win32.MONITORINFOEXW { cbSize = (uint)sizeof(Win32.MONITORINFOEXW) };
            if (Win32.GetMonitorInfoW(monitor, ref info))
            {
                var work = info.rcWork;
                x = Math.Clamp(x, work.Left + ScreenMargin, Math.Max(work.Left + ScreenMargin, work.Right - PopupWidth - ScreenMargin));
                y = Math.Clamp(y, work.Top + ScreenMargin, Math.Max(work.Top + ScreenMargin, work.Bottom - PopupHeight - ScreenMargin));
            }
        }

        Diagnostics.Write($"CustomValuePopup.Show({targetLabel}): anchor=({anchor.Left},{anchor.Top},{anchor.Right}," +
            $"{anchor.Bottom}), placing at ({x},{y}) {PopupWidth}x{PopupHeight}.");

        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, x, y, PopupWidth, PopupHeight, Win32.SWP_NOACTIVATE);
        Win32.ShowWindow(Handle, Win32.SW_SHOWNA);

        Diagnostics.Write($"CustomValuePopup.Show({targetLabel}): this popup's own DPI={Win32.GetDpiForWindow(Handle)}.");

        InstallInputHooks();
        visible_ = true;

        Win32.InvalidateRect(Handle, IntPtr.Zero, true);
    }

    internal void Hide(string reason = "unspecified")
    {
        if (!visible_)
        {
            return;
        }

        Diagnostics.Write($"CustomValuePopup.Hide(): {reason}");

        visible_ = false;
        Win32.ShowWindow(Handle, Win32.SW_HIDE);
        RemoveInputHooks();

        Win32.EndMenu();
    }

    internal bool Visible => visible_;

    private IntPtr CreateTrackbar()
    {
        fixed (char* className = Win32.TRACKBAR_CLASS)
        {
            var handle = Win32.CreateWindowExW(
                0, className, null,
                Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_TABSTOP |
                Win32.TBS_HORZ | Win32.TBS_NOTICKS | Win32.TBS_TRANSPARENTBKGND,
                20, 40, PopupWidth - 40, 30,
                Handle, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);

            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"CreateWindowExW failed for the trackbar (Win32 {Marshal.GetLastWin32Error()}).");
            }

            var range = new IntPtr((TrackMin & 0xFFFF) | ((TrackMax & 0xFFFF) << 16));
            Win32.SendMessageW(handle, Win32.TBM_SETRANGE, new IntPtr(1), range);

            return handle;
        }
    }

    private static void EnsureCommonControlsInitialized()
    {
        if (commonControlsInitialized_)
        {
            return;
        }

        commonControlsInitialized_ = true;

        var icc = new Win32.INITCOMMONCONTROLSEX
        {
            dwSize = (uint)sizeof(Win32.INITCOMMONCONTROLSEX),
            dwICC = Win32.ICC_BAR_CLASSES,
        };

        if (!Win32.InitCommonControlsEx(ref icc))
        {
            Diagnostics.Write("InitCommonControlsEx (ICC_BAR_CLASSES) failed - the trackbar may not create.");
        }
    }

    private void InstallTrackbarSubclass()
    {
        var callback = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, nuint, IntPtr, IntPtr>)
            &TrackbarSubclassProc;
        Win32.SetWindowSubclass(trackbar_, callback, 0, IntPtr.Zero);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr TrackbarSubclassProc(
        IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint idSubclass, IntPtr refData)
    {
        try
        {
            if (message == Win32.WM_KEYDOWN && (int)wParam == Win32.VK_ESCAPE &&
                activeForHooks_ is { } self && window == self.trackbar_)
            {
                self.Hide("Escape, via the trackbar subclass");
                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"CustomValuePopup subclass threw for message 0x{message:X4}: {ex}");
        }

        return Win32.DefSubclassProc(window, message, wParam, lParam);
    }

    private void InstallInputHooks()
    {
        activeForHooks_ = this;

        if (keyboardHook_ == IntPtr.Zero)
        {
            var keyboardCallback = (IntPtr)(delegate* unmanaged[Stdcall]<int, IntPtr, IntPtr, IntPtr>)&KeyboardHookProc;
            keyboardHook_ = Win32.SetWindowsHookExW(Win32.WH_KEYBOARD_LL, keyboardCallback, IntPtr.Zero, 0);

            if (keyboardHook_ == IntPtr.Zero)
            {
                Diagnostics.Write($"SetWindowsHookExW (WH_KEYBOARD_LL) failed (Win32 {Marshal.GetLastWin32Error()}).");
            }
        }

        if (mouseHook_ == IntPtr.Zero)
        {
            var mouseCallback = (IntPtr)(delegate* unmanaged[Stdcall]<int, IntPtr, IntPtr, IntPtr>)&MouseHookProc;
            mouseHook_ = Win32.SetWindowsHookExW(Win32.WH_MOUSE_LL, mouseCallback, IntPtr.Zero, 0);

            if (mouseHook_ == IntPtr.Zero)
            {
                Diagnostics.Write($"SetWindowsHookExW (WH_MOUSE_LL) failed (Win32 {Marshal.GetLastWin32Error()}).");
            }
        }
    }

    private void RemoveInputHooks()
    {
        if (keyboardHook_ != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(keyboardHook_);
            keyboardHook_ = IntPtr.Zero;
        }

        if (mouseHook_ != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(mouseHook_);
            mouseHook_ = IntPtr.Zero;
        }

        if (ReferenceEquals(activeForHooks_, this))
        {
            activeForHooks_ = null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code == Win32.HC_ACTION && activeForHooks_ is { } self &&
                ((uint)wParam == Win32.WM_KEYDOWN || (uint)wParam == Win32.WM_SYSKEYDOWN))
            {
                var hook = *(Win32.KBDLLHOOKSTRUCT*)lParam.ToPointer();
                var vk = (int)hook.vkCode;

                if (IsNavigationKey(vk))
                {
                    Win32.SendMessageW(self.trackbar_, Win32.WM_KEYDOWN, new IntPtr(vk), IntPtr.Zero);
                    return new IntPtr(1);
                }

                if (vk != Win32.VK_CONTROL && vk != Win32.VK_LCONTROL && vk != Win32.VK_RCONTROL &&
                    vk != Win32.VK_SNAPSHOT)
                {
                    Diagnostics.Write($"CustomValuePopup keyboard hook: closing for vk=0x{vk:X2} " +
                        $"(message=0x{(uint)wParam:X4}).");
                    self.Hide($"keyboard hook, vk=0x{vk:X2}");
                }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"CustomValuePopup keyboard hook threw: {ex}");
        }

        return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static bool IsNavigationKey(int vk) => vk is
        Win32.VK_LEFT or Win32.VK_RIGHT or Win32.VK_UP or Win32.VK_DOWN or
        Win32.VK_HOME or Win32.VK_END or Win32.VK_PRIOR or Win32.VK_NEXT or
        Win32.VK_ESCAPE;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code == Win32.HC_ACTION && activeForHooks_ is { } self &&
                (IsButtonDown((uint)wParam) || (uint)wParam == Win32.WM_MOUSEMOVE))
            {
                var hook = *(Win32.MSLLHOOKSTRUCT*)lParam.ToPointer();
                var gotRect = Win32.GetWindowRect(self.Handle, out var rect);
                var inside = gotRect && Contains(rect, hook.pt);

                if ((uint)wParam == Win32.WM_MOUSEMOVE)
                {
                    if (self.dragging_)
                    {
                        var client = hook.pt;
                        Win32.ScreenToClient(self.trackbar_, ref client);
                        var lp = new IntPtr((client.X & 0xFFFF) | ((client.Y & 0xFFFF) << 16));
                        Win32.SendMessageW(self.trackbar_, Win32.WM_MOUSEMOVE, new IntPtr(Win32.MK_LBUTTON), lp);
                    }

                    return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
                }

                Diagnostics.Write($"CustomValuePopup mouse hook: point=({hook.pt.X},{hook.pt.Y}), " +
                    $"popupRect=({(gotRect ? $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}" : "GetWindowRect failed")}), " +
                    (inside ? "inside - ignored." : "outside - closing."));

                if (gotRect && !inside)
                {
                    self.Hide("mouse hook, click outside the popup's rect");
                    return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
                }

                if (inside && (uint)wParam == Win32.WM_LBUTTONDOWN)
                {
                    var gotTrackbarRect = Win32.GetWindowRect(self.trackbar_, out var trackbarRect);
                    if (gotTrackbarRect && Contains(trackbarRect, hook.pt))
                    {
                        var client = hook.pt;
                        Win32.ScreenToClient(self.trackbar_, ref client);
                        Diagnostics.Write($"CustomValuePopup mouse hook: injecting synthetic WM_LBUTTONDOWN into " +
                            $"the trackbar at client ({client.X},{client.Y}) - real click may still be getting " +
                            "swallowed by the still-tracking menu.");
                        var lp = new IntPtr((client.X & 0xFFFF) | ((client.Y & 0xFFFF) << 16));
                        Win32.SendMessageW(self.trackbar_, Win32.WM_LBUTTONDOWN, new IntPtr(Win32.MK_LBUTTON), lp);
                    }
                }

                return new IntPtr(1);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"CustomValuePopup mouse hook threw: {ex}");
        }

        return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static bool IsButtonDown(uint message) => message is
        Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN or Win32.WM_NCLBUTTONDOWN or Win32.WM_NCRBUTTONDOWN;

    private static bool Contains(in Win32.RECT rect, in Win32.POINT point) =>
        point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;

    protected override bool WndProc(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;

        switch (message)
        {
            case Win32.WM_ERASEBKGND:
                Paint(wParam);
                result = new IntPtr(1);
                return true;

            case Win32.WM_CTLCOLORSTATIC:
                Win32.SetTextColor(wParam, ColorText);
                Win32.SetBkMode(wParam, Win32.TRANSPARENT);
                result = backgroundBrush_;
                return true;

            case Win32.WM_MOUSEACTIVATE:
                result = new IntPtr(Win32.MA_NOACTIVATE);
                return true;

            case Win32.WM_HSCROLL:
                if (lParam == trackbar_)
                {
                    HandleScroll((int)((long)wParam & 0xFFFF), (int)(((long)wParam >> 16) & 0xFFFF));
                }

                return true;

            case Win32.WM_NOTIFY:
                var header = (Win32.NMHDR*)lParam.ToPointer();
                if (header->code == Win32.NM_CUSTOMDRAW && header->hwndFrom == trackbar_)
                {
                    result = HandleCustomDraw((Win32.NMCUSTOMDRAW*)lParam.ToPointer());
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    private static bool loggedPrepaint_;
    private static bool loggedChannel_;
    private static bool loggedThumb_;
    private static bool loggedChannelFallthrough_;
    private static bool loggedEraseStage_;

    private IntPtr HandleCustomDraw(Win32.NMCUSTOMDRAW* draw)
    {
        if (draw->dwDrawStage == Win32.CDDS_PREPAINT)
        {
            if (!loggedPrepaint_)
            {
                loggedPrepaint_ = true;
                Diagnostics.Write("CustomValuePopup trackbar: CDDS_PREPAINT reached, returning CDRF_NOTIFYITEMDRAW.");
            }

            return Win32.CDRF_NOTIFYITEMDRAW;
        }

        if (draw->dwDrawStage == Win32.CDDS_ITEMPREPAINT)
        {
            if (draw->dwItemSpec == Win32.TBCD_CHANNEL)
            {
                if (!loggedChannel_)
                {
                    loggedChannel_ = true;
                    Diagnostics.Write($"CustomValuePopup trackbar: CDDS_ITEMPREPAINT/TBCD_CHANNEL reached, rc=" +
                        $"({draw->rc.Left},{draw->rc.Top},{draw->rc.Right},{draw->rc.Bottom}) - drawing the dark " +
                        "channel and returning CDRF_SKIPDEFAULT.");
                }

                var channel = draw->rc;
                var mid = (channel.Top + channel.Bottom) / 2;
                channel.Top = mid - 2;
                channel.Bottom = mid + 2;

                Win32.FillRect(draw->hdc, in channel, channelBrush_);
                return Win32.CDRF_SKIPDEFAULT;
            }

            if (draw->dwItemSpec == Win32.TBCD_THUMB)
            {
                if (!loggedThumb_)
                {
                    loggedThumb_ = true;
                    Diagnostics.Write("CustomValuePopup trackbar: CDDS_ITEMPREPAINT/TBCD_THUMB reached - " +
                        "drawing the thumb and returning CDRF_SKIPDEFAULT.");
                }

                var oldBrush = Win32.SelectObject(draw->hdc, thumbBrush_);
                var oldPen = Win32.SelectObject(draw->hdc, Win32.GetStockObject(Win32.NULL_PEN));
                Win32.Ellipse(draw->hdc, draw->rc.Left, draw->rc.Top, draw->rc.Right, draw->rc.Bottom);
                Win32.SelectObject(draw->hdc, oldBrush);
                Win32.SelectObject(draw->hdc, oldPen);
                return Win32.CDRF_SKIPDEFAULT;
            }

            if (draw->dwItemSpec == Win32.TBCD_TICS)
            {
                if (!loggedChannelFallthrough_)
                {
                    loggedChannelFallthrough_ = true;
                    Diagnostics.Write("CustomValuePopup trackbar: CDDS_ITEMPREPAINT/TBCD_TICS reached - " +
                        "filling it with the background color directly (skipping the draw entirely " +
                        "didn't clear the white fill on real hardware).");
                }

                var ticsRect = draw->rc;
                Win32.FillRect(draw->hdc, in ticsRect, backgroundBrush_);
                return Win32.CDRF_SKIPDEFAULT;
            }

            return Win32.CDRF_DODEFAULT;
        }

        if (draw->dwDrawStage is Win32.CDDS_PREERASE or Win32.CDDS_POSTERASE or Win32.CDDS_POSTPAINT or
            Win32.CDDS_ITEMPREERASE or Win32.CDDS_ITEMPOSTERASE or Win32.CDDS_ITEMPOSTPAINT)
        {
            if (!loggedEraseStage_)
            {
                loggedEraseStage_ = true;
                Diagnostics.Write($"CustomValuePopup trackbar: CDDS stage 0x{draw->dwDrawStage:X} reached " +
                    "(PREERASE/POSTERASE/POSTPAINT family) - skipping default, nothing left for it to draw.");
            }

            return Win32.CDRF_SKIPDEFAULT;
        }

        return Win32.CDRF_DODEFAULT;
    }

    private void HandleScroll(int code, int rawValue)
    {
        var live = code is Win32.TB_THUMBTRACK or Win32.TB_THUMBPOSITION;
        var snap = (Win32.GetKeyState(Win32.VK_CONTROL) & 0x8000) != 0;

        var value = live ? rawValue : GetTrackbarPos();

        if (live)
        {
            dragging_ = true;

            if (snap)
            {
                value = SnapTo5(value);
                SetTrackbarPos(value);
            }
        }
        else if (dragging_)
        {
            dragging_ = false;
            value = GetTrackbarPos();
        }
        else if (snap)
        {
            var direction = value > lastCommittedPercent_ ? 1 : value < lastCommittedPercent_ ? -1 : 0;

            // Direction-aware floor/ceil to the nearest multiple of 5 strictly
            // past lastCommittedPercent_ - a plain round-then-+-5 can eat part
            // of the step whenever the last value isn't itself a multiple of 5.
            var target = lastCommittedPercent_;
            if (direction > 0)
            {
                target = (lastCommittedPercent_ / 5 + 1) * 5;
            }
            else if (direction < 0)
            {
                target = ((lastCommittedPercent_ + 4) / 5 - 1) * 5;
            }

            value = Math.Clamp(target, TrackMin, TrackMax);
            SetTrackbarPos(value);
        }

        currentPercent_ = value;

        RepaintNameAndPercent();

        if (live)
        {
            return;
        }

        lastCommittedPercent_ = value;
        onCommit_?.Invoke(value);
    }

    private static int SnapTo5(int value) =>
        Math.Clamp((int)Math.Round(value / 5.0) * 5, TrackMin, TrackMax);

    private int GetTrackbarPos() =>
        (int)Win32.SendMessageW(trackbar_, Win32.TBM_GETPOS, IntPtr.Zero, IntPtr.Zero);

    private void SetTrackbarPos(int value) =>
        Win32.SendMessageW(trackbar_, Win32.TBM_SETPOS, new IntPtr(1), new IntPtr(value));

    private void Paint(IntPtr dc)
    {
        if (!Win32.GetClientRect(Handle, out var client))
        {
            return;
        }

        Win32.FillRect(dc, in client, backgroundBrush_);
        Win32.FrameRect(dc, in client, borderBrush_);

        DrawNameAndPercent(dc, client.Right);
        DrawFooter(dc, client);
    }

    private void DrawNameAndPercent(IntPtr dc, int clientRight)
    {
        var font = Win32.GetStockObject(Win32.DEFAULT_GUI_FONT);
        Win32.SelectObject(dc, font);
        Win32.SetBkMode(dc, Win32.TRANSPARENT);
        Win32.SetTextColor(dc, ColorText);

        var nameRect = new Win32.RECT { Left = 12, Top = 8, Right = clientRight - 12, Bottom = 32 };
        Win32.DrawTextW(dc, targetLabel_, -1, ref nameRect, Win32.DT_LEFT | Win32.DT_NOCLIP);

        var percentRect = new Win32.RECT { Left = 12, Top = 8, Right = clientRight - 12, Bottom = 32 };
        Win32.DrawTextW(dc, $"{currentPercent_}%", -1, ref percentRect, Win32.DT_RIGHT | Win32.DT_NOCLIP);
    }

    private void RepaintNameAndPercent()
    {
        if (!Win32.GetClientRect(Handle, out var client))
        {
            return;
        }

        var dc = Win32.GetDC(Handle);
        if (dc == IntPtr.Zero)
        {
            return;
        }

        var row = new Win32.RECT { Left = client.Left, Top = 0, Right = client.Right, Bottom = 32 };
        Win32.FillRect(dc, in row, backgroundBrush_);
        DrawNameAndPercent(dc, client.Right);

        Win32.ReleaseDC(Handle, dc);
    }

    private static void DrawFooter(IntPtr dc, in Win32.RECT client)
    {
        var area = new Win32.RECT { Left = 12, Top = 78, Right = client.Right - 12, Bottom = client.Bottom - 8 };

        Win32.SetTextColor(dc, ColorMuted);
        Win32.GetTextMetricsW(dc, out var metrics);
        var lineHeight = metrics.tmHeight + metrics.tmExternalLeading;

        Win32.GetTextExtentPoint32W(dc, " ", 1, out var spaceSize);

        var x = area.Left;
        var y = area.Top;
        var words = FooterText.Split(' ');

        foreach (var word in words)
        {
            Win32.GetTextExtentPoint32W(dc, word, word.Length, out var wordSize);

            var isCtrl = word == "Ctrl";
            var leadingGap = isCtrl ? CtrlExtraGap : 0;

            if (x != area.Left && x + leadingGap + wordSize.cx > area.Right)
            {
                x = area.Left;
                y += lineHeight;
            }
            else
            {
                x += leadingGap;
            }

            if (isCtrl)
            {
                var chip = new Win32.RECT { Left = x - 3, Top = y, Right = x + wordSize.cx + 3, Bottom = y + lineHeight - 2 };

                var chipBrush = Win32.CreateSolidBrush(ColorChipFill);
                var oldBrush = Win32.SelectObject(dc, chipBrush);
                var chipPen = Win32.CreatePen(Win32.PS_SOLID, 1, ColorBorder);
                var oldPen = Win32.SelectObject(dc, chipPen);

                Win32.RoundRect(dc, chip.Left, chip.Top, chip.Right, chip.Bottom, 4, 4);

                Win32.SelectObject(dc, oldBrush);
                Win32.SelectObject(dc, oldPen);
                Win32.DeleteObject(chipBrush);
                Win32.DeleteObject(chipPen);
            }

            Win32.TextOutW(dc, x, y, word, word.Length);
            x += wordSize.cx + spaceSize.cx + leadingGap;
        }
    }

    public override void Dispose()
    {
        RemoveInputHooks();

        if (backgroundBrush_ != IntPtr.Zero)
        {
            Win32.DeleteObject(backgroundBrush_);
        }

        if (borderBrush_ != IntPtr.Zero)
        {
            Win32.DeleteObject(borderBrush_);
        }

        if (channelBrush_ != IntPtr.Zero)
        {
            Win32.DeleteObject(channelBrush_);
        }

        if (thumbBrush_ != IntPtr.Zero)
        {
            Win32.DeleteObject(thumbBrush_);
        }

        base.Dispose();
    }
}
