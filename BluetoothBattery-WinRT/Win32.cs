using System.Runtime.InteropServices;

namespace BluetoothBattery;

// Every Win32 declaration this suite needs, in one place. Everything is
// declared blittable - fixed character buffers rather than [MarshalAs]
// strings, structs rather than classes - so no marshalling stub has to be
// generated at run time, which is what keeps this ahead-of-time compilable.
internal static unsafe class Win32
{
    // -----------------------------------------------------------------------
    // Window styles and messages
    // -----------------------------------------------------------------------

    internal const uint WS_POPUP = 0x80000000;
    internal const uint WS_EX_LAYERED = 0x00080000;
    internal const uint WS_EX_TRANSPARENT = 0x00000020;
    internal const uint WS_EX_NOACTIVATE = 0x08000000;
    internal const uint WS_EX_TOOLWINDOW = 0x00000080;
    internal const uint WS_EX_TOPMOST = 0x00000008;

    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_ERASEBKGND = 0x0014;
    internal const uint WM_TIMER = 0x0113;
    internal const uint WM_DISPLAYCHANGE = 0x007E;
    internal const uint WM_NULL = 0x0000;
    internal const uint WM_COMMAND = 0x0111;
    internal const uint WM_POWERBROADCAST = 0x0218;
    internal const uint WM_APPCOMMAND = 0x0319;
    internal const uint WM_USER = 0x0400;

    internal const uint WM_PAINT = 0x000F;
    internal const uint WM_KEYDOWN = 0x0100;

    // Sent when the popup loses activation, i.e. click-away dismissal.
    // wParam low word is WA_INACTIVE when it is going away.
    internal const uint WM_ACTIVATE = 0x0006;

    internal const int WA_INACTIVE = 0;

    internal const int VK_ESCAPE = 0x1B;

    // Carries a queued callback from a worker thread onto the UI thread. See UiDispatcher.
    internal const uint WM_DISPATCH = WM_USER + 20;

    internal const int PBT_POWERSETTINGCHANGE = 0x8013;

    // Private message the shell sends for tray-icon activity.
    internal const uint WM_TRAYICON = WM_USER + 1;

    internal const uint WM_INVERTER_DIED = WM_USER + 2;

    internal const uint WM_DESKTOP_CHANGED = WM_USER + 3;

    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_CONTEXTMENU = 0x007B;

    // Notification *event* codes carried in the low word of lParam under
    // WM_TRAYICON, not window messages - Space sends NIN_SELECT, Enter sends
    // NIN_KEYSELECT.
    internal const uint NIN_SELECT = WM_USER + 0;
    internal const uint NIN_KEYSELECT = WM_USER + 1;

    internal const uint SW_HIDE = 0;
    internal const uint SW_SHOWNOACTIVATE = 4;

    internal const uint LWA_ALPHA = 0x00000002;

    // SetWindowPos
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;

    // -----------------------------------------------------------------------
    // Structures
    // -----------------------------------------------------------------------

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

    // CharSet.Unicode is load-bearing: it is what StructLayout's default
    // (Ansi) governs for a fixed char buffer, and under Ansi this struct
    // stops being blittable - .NET marshals it, converting every char and
    // shrinking the native buffer relative to cbSize.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        public fixed char szDevice[32];
    }

    // The Vista-and-later layout; cbSize must match exactly or the shell
    // rejects the call. CharSet.Unicode for the same reason as
    // MONITORINFOEXW above.
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

    // Shell_NotifyIcon
    internal const uint NIM_ADD = 0x00000000;
    internal const uint NIM_MODIFY = 0x00000001;
    internal const uint NIM_DELETE = 0x00000002;
    internal const uint NIM_SETVERSION = 0x00000004;

    internal const uint NIF_MESSAGE = 0x00000001;
    internal const uint NIF_ICON = 0x00000002;
    internal const uint NIF_TIP = 0x00000004;

    // Required whenever NOTIFYICON_VERSION_4 is set: under version 4 the
    // shell stops showing the tooltip on its own, and NIF_TIP alone only
    // supplies the text, not the display.
    internal const uint NIF_SHOWTIP = 0x00000080;

    internal const uint NOTIFYICON_VERSION_4 = 4;

    // Menus
    internal const uint MF_STRING = 0x00000000;
    internal const uint MF_SEPARATOR = 0x00000800;
    internal const uint MF_POPUP = 0x00000010;
    internal const uint MF_CHECKED = 0x00000008;
    internal const uint MF_GRAYED = 0x00000001;
    internal const uint MF_OWNERDRAW = 0x00000100;

    internal const uint WM_MEASUREITEM = 0x002C;
    internal const uint WM_DRAWITEM = 0x002B;

    // Sent when a mnemonic key is pressed and Windows cannot resolve it
    // itself - needed for an owner-drawn menu item, whose text was never
    // handed to AppendMenu for Windows to match against.
    internal const uint WM_MENUCHAR = 0x0120;

    internal const int MNC_IGNORE = 0;
    internal const int MNC_EXECUTE = 2;

    internal const uint ODT_MENU = 1;
    internal const uint ODA_DRAWENTIRE = 0x0001;
    internal const uint ODS_SELECTED = 0x0001;
    internal const uint ODS_GRAYED = 0x0002;
    internal const uint ODS_DISABLED = 0x0004;
    internal const uint ODS_CHECKED = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEASUREITEMSTRUCT
    {
        public uint CtlType;
        public uint CtlID;
        public uint itemID;
        public uint itemWidth;
        public uint itemHeight;
        public nuint itemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DRAWITEMSTRUCT
    {
        public uint CtlType;
        public uint CtlID;
        public uint itemID;
        public uint itemAction;
        public uint itemState;
        public IntPtr hwndItem;
        public IntPtr hDC;
        public RECT rcItem;
        public nuint itemData;
    }

    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_BOTTOMALIGN = 0x0020;
    internal const uint TPM_LEFTALIGN = 0x0000;

    // GDI
    internal const uint SRCCOPY = 0x00CC0020;
    internal const int PS_SOLID = 0;

    // -----------------------------------------------------------------------
    // user32
    // -----------------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern ushort RegisterClassExW(in WNDCLASSEXW windowClass);

    internal const int IDC_ARROW = 32512;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(
        uint exStyle, char* className, char* windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    // The DPI-correct small-icon size Windows actually wants for a tray icon.
    internal const int SM_CXSMICON = 49;

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    internal const uint DefaultDpi = 96;

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr window);

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
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string? item);

    // Second declaration of the same function: an MF_OWNERDRAW item's fourth
    // parameter marshals as raw data rather than a string.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    internal static extern bool AppendMenuOwnerDrawW(IntPtr menu, uint flags, IntPtr id, IntPtr itemData);

    [DllImport("user32.dll")]
    internal static extern bool DestroyMenu(IntPtr menu);

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

    [DllImport("user32.dll")]
    internal static extern int TrackPopupMenu(
        IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rect);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr window);

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

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr BeginPaint(IntPtr window, out PAINTSTRUCT paint);

    [DllImport("user32.dll")]
    internal static extern bool EndPaint(IntPtr window, in PAINTSTRUCT paint);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nuint SetTimer(IntPtr window, nuint id, uint intervalMs, IntPtr callback);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool KillTimer(IntPtr window, nuint id);

    // -----------------------------------------------------------------------
    // gdi32
    // -----------------------------------------------------------------------

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
    internal static extern uint SetBkColor(IntPtr dc, uint color);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    internal static extern int SelectClipRgn(IntPtr dc, IntPtr region);

    // -----------------------------------------------------------------------
    // shell32 / kernel32
    // -----------------------------------------------------------------------

    [DllImport("shell32.dll", SetLastError = true)]
    internal static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll")]
    internal static extern int Shell_NotifyIconGetRect(
        in NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    // -----------------------------------------------------------------------
    // advapi32 - registry change notification
    // -----------------------------------------------------------------------

    internal const uint REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;

    // Asks Windows to signal an event when the key changes. The notification
    // is one-shot and must be re-armed after every signal.
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

    [DllImport("kernel32.dll")]
    internal static extern bool FreeLibrary(IntPtr module);

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

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
