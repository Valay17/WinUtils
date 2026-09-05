using System.Runtime.InteropServices;

namespace ScreenDimmer;

internal static unsafe class Win32
{
    internal const uint WS_POPUP = 0x80000000;
    internal const uint WS_EX_LAYERED = 0x00080000;
    internal const uint WS_EX_TRANSPARENT = 0x00000020;
    internal const uint WS_EX_NOACTIVATE = 0x08000000;
    internal const uint WS_EX_TOOLWINDOW = 0x00000080;

    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_ERASEBKGND = 0x0014;
    internal const uint WM_TIMER = 0x0113;
    internal const uint WM_DISPLAYCHANGE = 0x007E;
    internal const uint WM_NULL = 0x0000;
    internal const uint WM_COMMAND = 0x0111;
    internal const uint WM_POWERBROADCAST = 0x0218;
    internal const uint WM_APPCOMMAND = 0x0319;
    internal const uint WM_USER = 0x0400;

    internal const int PBT_POWERSETTINGCHANGE = 0x8013;

    // Private message the shell sends us for tray-icon activity.
    internal const uint WM_TRAYICON = WM_USER + 1;

    // Posted by InversionWatch when Color Invert Window dies without clearing inversion.
    internal const uint WM_INVERTER_DIED = WM_USER + 2;

    // Posted when the registry value holding the current virtual desktop changes.
    internal const uint WM_DESKTOP_CHANGED = WM_USER + 3;

    // Posted by NativeColorFilterWatch's registry change notification.
    internal const uint WM_NATIVE_COLOR_FILTER_CHANGED = WM_USER + 4;

    // Posted to self from WM_INITMENUPOPUP for a custom-trigger submenu, once
    // the child submenu is actually laid out and GetMenuItemRect can answer for it.
    internal const uint WM_CUSTOM_TRIGGER_READY = WM_USER + 5;

    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_CONTEXTMENU = 0x007B;

    // Keyboard activation of a tray icon: NIN_SELECT/NIN_KEYSELECT are
    // notification event codes in the low word of lParam, not window messages.
    internal const uint NIN_SELECT = WM_USER + 0;
    internal const uint NIN_KEYSELECT = WM_USER + 1;

    internal const uint SW_HIDE = 0;
    internal const uint SW_SHOWNOACTIVATE = 4;

    internal const uint LWA_ALPHA = 0x00000002;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    // CharSet.Unicode keeps this blittable so the fixed char buffer marshals
    // correctly - not decoration.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        public fixed char szDevice[32];
    }

    internal const uint MONITORINFOF_PRIMARY = 0x1;

    // CharSet.Unicode keeps this blittable; cbSize must match exactly or the
    // shell rejects the call.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersionOrTimeout;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    internal const uint NIM_ADD = 0x00000000;
    internal const uint NIM_MODIFY = 0x00000001;
    internal const uint NIM_DELETE = 0x00000002;
    internal const uint NIM_SETVERSION = 0x00000004;

    internal const uint NIF_MESSAGE = 0x00000001;
    internal const uint NIF_ICON = 0x00000002;
    internal const uint NIF_TIP = 0x00000004;

    // Required alongside NOTIFYICON_VERSION_4 or the shell stops showing the tooltip on its own.
    internal const uint NIF_SHOWTIP = 0x00000080;

    internal const uint NOTIFYICON_VERSION_4 = 4;

    internal const uint MF_STRING = 0x00000000;
    internal const uint MF_SEPARATOR = 0x00000800;
    internal const uint MF_POPUP = 0x00000010;
    internal const uint MF_CHECKED = 0x00000008;
    internal const uint MF_GRAYED = 0x00000001;

    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_BOTTOMALIGN = 0x0020;
    internal const uint TPM_LEFTALIGN = 0x0000;
    internal const uint TPM_LAYOUTRTL = 0x8000;

    internal const uint SRCCOPY = 0x00CC0020;
    internal const int PS_SOLID = 0;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern ushort RegisterClassExW(in WNDCLASSEXW windowClass);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(
        uint exStyle, char* className, char* windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern int GetMessageW(out MSG message, IntPtr window, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(in MSG message);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessageW(in MSG message);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    internal static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr window, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetLayeredWindowAttributes(
        IntPtr window, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    internal static extern bool EnumDisplayMonitors(
        IntPtr dc, IntPtr clip, IntPtr callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFOEXW info);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    internal const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    internal const int SM_CXMENUCHECK = 71;

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string? item);

    [DllImport("user32.dll")]
    internal static extern bool DestroyMenu(IntPtr menu);

    // Selects MENUINFO.hbrBack, the only field this project uses it for.
    internal const uint MIM_BACKGROUND = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MENUINFO
    {
        public uint cbSize;
        public uint fMask;
        public uint dwStyle;
        public uint cyMax;
        public IntPtr hbrBack;
        public uint dwContextHelpID;
        public nuint dwMenuData;
    }

    [DllImport("user32.dll")]
    internal static extern bool SetMenuInfo(IntPtr menu, in MENUINFO info);

    // TrackPopupMenuEx, not the plain TrackPopupMenu - TPM_LAYOUTRTL is
    // documented for the Ex form specifically.
    [DllImport("user32.dll")]
    internal static extern int TrackPopupMenuEx(
        IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr lptpm);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr window);

    internal const uint WM_INITMENUPOPUP = 0x0117;

    [DllImport("user32.dll")]
    internal static extern bool GetMenuItemRect(IntPtr window, IntPtr menu, uint item, out RECT rect);

    [DllImport("user32.dll")]
    internal static extern bool EndMenu();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    [DllImport("user32.dll")]
    internal static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreateIconIndirect(in ICONINFO info);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("user32.dll")]
    internal static extern int FillRect(IntPtr dc, in RECT rect, IntPtr brush);

    [DllImport("user32.dll")]
    internal static extern bool GetClientRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll")]
    internal static extern bool InvalidateRect(IntPtr window, IntPtr rect, bool erase);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nuint SetTimer(IntPtr window, nuint id, uint intervalMs, IntPtr callback);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool KillTimer(IntPtr window, nuint id);

    internal const uint WS_CHILD = 0x40000000;
    internal const uint WS_VISIBLE = 0x10000000;
    internal const uint WS_TABSTOP = 0x00010000;
    internal const uint SW_SHOWNA = 8;

    internal const uint WM_ACTIVATE = 0x0006;
    internal const uint WM_KEYDOWN = 0x0100;
    internal const uint WM_SYSKEYDOWN = 0x0104;
    internal const uint WM_HSCROLL = 0x0114;
    internal const uint WM_SETFOCUS = 0x0007;
    internal const int WA_INACTIVE = 0;

    internal const int VK_ESCAPE = 0x1B;
    internal const int VK_PRIOR = 0x21;
    internal const int VK_NEXT = 0x22;
    internal const int VK_END = 0x23;
    internal const int VK_HOME = 0x24;
    internal const int VK_LEFT = 0x25;
    internal const int VK_UP = 0x26;
    internal const int VK_RIGHT = 0x27;
    internal const int VK_DOWN = 0x28;

    // MA_NOACTIVATE: a click on the popup must not activate it, or it would
    // end the still-open root menu's own TrackPopupMenu tracking.
    internal const uint WM_MOUSEACTIVATE = 0x0021;
    internal const int MA_NOACTIVATE = 3;

    internal const int WH_KEYBOARD_LL = 13;
    internal const int WH_MOUSE_LL = 14;
    internal const int HC_ACTION = 0;

    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_RBUTTONDOWN = 0x0204;
    internal const uint WM_NCLBUTTONDOWN = 0x00A1;
    internal const uint WM_NCRBUTTONDOWN = 0x00A4;

    internal const uint WM_MOUSEMOVE = 0x0200;
    internal const int MK_LBUTTON = 0x0001;

    [DllImport("user32.dll")]
    internal static extern bool ScreenToClient(IntPtr window, ref POINT point);

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookExW(int idHook, IntPtr lpfn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    // Not registered without a comctl32 v6 manifest dependency; InitCommonControlsEx
    // below registers it regardless of manifest state.
    internal const string TRACKBAR_CLASS = "msctls_trackbar32";

    internal const uint TBS_HORZ = 0x0000;
    internal const uint TBS_AUTOTICKS = 0x0001;
    internal const uint TBS_NOTICKS = 0x0010;

    // Without this the stock control paints its own opaque background under
    // the custom-drawn channel.
    internal const uint TBS_TRANSPARENTBKGND = 0x1000;

    internal const uint TBM_SETRANGE = WM_USER + 6;
    internal const uint TBM_SETPOS = WM_USER + 5;
    internal const uint TBM_GETPOS = WM_USER;

    // Notification code in the low word of wParam for WM_HSCROLL from a
    // trackbar. TB_THUMBTRACK/TB_THUMBPOSITION carry the live position in the
    // high word; the rest need TBM_GETPOS read back separately.
    internal const int TB_LINEUP = 0;
    internal const int TB_LINEDOWN = 1;
    internal const int TB_PAGEUP = 2;
    internal const int TB_PAGEDOWN = 3;
    internal const int TB_THUMBPOSITION = 4;
    internal const int TB_THUMBTRACK = 5;
    internal const int TB_ENDTRACK = 8;

    [StructLayout(LayoutKind.Sequential)]
    internal struct INITCOMMONCONTROLSEX
    {
        public uint dwSize;
        public uint dwICC;
    }

    internal const uint ICC_BAR_CLASSES = 0x00000004;

    [DllImport("comctl32.dll")]
    internal static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);

    // The callback must be a permanent native entry point ([UnmanagedCallersOnly]):
    // the subclass can be invoked long after the call that installed it returns.
    [DllImport("comctl32.dll")]
    internal static extern bool SetWindowSubclass(IntPtr window, IntPtr callback, nuint id, IntPtr refData);

    [DllImport("comctl32.dll")]
    internal static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("comctl32.dll")]
    internal static extern bool RemoveWindowSubclass(IntPtr window, IntPtr callback, nuint id);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int DrawTextW(IntPtr dc, string text, int count, ref RECT rect, uint format);

    internal const uint DT_LEFT = 0x00000000;
    internal const uint DT_RIGHT = 0x00000002;
    internal const uint DT_WORDBREAK = 0x00000010;
    internal const uint DT_NOCLIP = 0x00000100;

    [DllImport("user32.dll")]
    internal static extern int FrameRect(IntPtr dc, in RECT rect, IntPtr brush);

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int virtualKey);

    internal const int VK_CONTROL = 0x11;

    // A WH_KEYBOARD_LL hook reports these side-specific codes for Ctrl, not the generic VK_CONTROL above.
    internal const int VK_LCONTROL = 0xA2;
    internal const int VK_RCONTROL = 0xA3;

    internal const int VK_SNAPSHOT = 0x2C;

    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    internal const uint WM_NOTIFY = 0x004E;

    internal const uint WM_CTLCOLORSTATIC = 0x0138;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NMHDR
    {
        public IntPtr hwndFrom;
        public UIntPtr idFrom;
        public uint code;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NMCUSTOMDRAW
    {
        public NMHDR hdr;
        public uint dwDrawStage;
        public IntPtr hdc;
        public RECT rc;
        public UIntPtr dwItemSpec;
        public uint uItemState;
        public IntPtr lItemlParam;
    }

    // NM_FIRST - 12, as an unsigned NMHDR.code value.
    internal const uint NM_CUSTOMDRAW = unchecked((uint)-12);

    internal const uint CDDS_PREPAINT = 0x00000001;
    internal const uint CDDS_ITEMPREPAINT = 0x00010001;
    internal const nint CDRF_DODEFAULT = 0x00000000;
    internal const nint CDRF_NOTIFYITEMDRAW = 0x00000020;
    internal const nint CDRF_SKIPDEFAULT = 0x00000004;

    internal const uint CDDS_POSTPAINT = 0x00000002;
    internal const uint CDDS_PREERASE = 0x00000003;
    internal const uint CDDS_POSTERASE = 0x00000004;
    internal const uint CDDS_ITEM = 0x00010000;
    internal const uint CDDS_ITEMPOSTPAINT = CDDS_ITEM | CDDS_POSTPAINT;
    internal const uint CDDS_ITEMPREERASE = CDDS_ITEM | CDDS_PREERASE;
    internal const uint CDDS_ITEMPOSTERASE = CDDS_ITEM | CDDS_POSTERASE;

    // dwItemSpec values for a trackbar's own NM_CUSTOMDRAW.
    internal const nuint TBCD_TICS = 1;
    internal const nuint TBCD_THUMB = 2;
    internal const nuint TBCD_CHANNEL = 3;

    internal const int NULL_PEN = 8;

    [DllImport("gdi32.dll")]
    internal static extern bool RoundRect(IntPtr dc, int left, int top, int right, int bottom, int width, int height);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int cx;
        public int cy;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetTextExtentPoint32W(IntPtr dc, string text, int count, out SIZE size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct TEXTMETRICW
    {
        public int tmHeight;
        public int tmAscent;
        public int tmDescent;
        public int tmInternalLeading;
        public int tmExternalLeading;
        public int tmAveCharWidth;
        public int tmMaxCharWidth;
        public int tmWeight;
        public int tmOverhang;
        public int tmDigitizedAspectX;
        public int tmDigitizedAspectY;
        public char tmFirstChar;
        public char tmLastChar;
        public char tmDefaultChar;
        public char tmBreakChar;
        public byte tmItalic;
        public byte tmUnderlined;
        public byte tmStruckOut;
        public byte tmPitchAndFamily;
        public byte tmCharSet;
    }

    [DllImport("gdi32.dll")]
    internal static extern bool GetTextMetricsW(IntPtr dc, out TEXTMETRICW metrics);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool TextOutW(IntPtr dc, int x, int y, string text, int count);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, IntPtr bits);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreatePen(int style, int width, uint color);

    [DllImport("gdi32.dll")]
    internal static extern bool Ellipse(IntPtr dc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    internal static extern bool BitBlt(
        IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint rasterOperation);

    [DllImport("gdi32.dll")]
    internal static extern bool StretchBlt(
        IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint rasterOperation);

    [DllImport("gdi32.dll")]
    internal static extern int SetStretchBltMode(IntPtr dc, int mode);

    // Must be called immediately after switching to HALFTONE, or brush misalignment occurs.
    [DllImport("gdi32.dll")]
    internal static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);

    internal const int HALFTONE = 4;

    [DllImport("gdi32.dll")]
    internal static extern uint SetBkColor(IntPtr dc, uint color);

    [DllImport("gdi32.dll")]
    internal static extern uint SetTextColor(IntPtr dc, uint color);

    internal const int TRANSPARENT = 1;

    [DllImport("gdi32.dll")]
    internal static extern int SetBkMode(IntPtr dc, int mode);

    internal const int DEFAULT_GUI_FONT = 17;

    [DllImport("gdi32.dll")]
    internal static extern IntPtr GetStockObject(int fnObject);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    internal static extern int SelectClipRgn(IntPtr dc, IntPtr region);

    [DllImport("shell32.dll", SetLastError = true)]
    internal static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll")]
    internal static extern int Shell_NotifyIconGetRect(
        in NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    // Signals when a value under the key is written.
    internal const uint REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;

    // One-shot: must be re-armed after every signal.
    [DllImport("advapi32.dll", SetLastError = false)]
    internal static extern int RegNotifyChangeKeyValue(
        Microsoft.Win32.SafeHandles.SafeRegistryHandle key,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter,
        Microsoft.Win32.SafeHandles.SafeWaitHandle notifyEvent,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandleW(string? moduleName);

    internal const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadLibraryExW(string fileName, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);

    // COLORREF is 0x00BBGGRR - the byte order is the opposite of HTML.
    internal static uint Rgb(byte red, byte green, byte blue) =>
        (uint)(red | (green << 8) | (blue << 16));

    internal static void CopyToFixed(char* destination, int capacity, string value)
    {
        var length = Math.Min(value.Length, capacity - 1);
        for (var i = 0; i < length; i++)
        {
            destination[i] = value[i];
        }

        destination[length] = '\0';
    }
}
