using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AppTiling;

internal static class ProcessWatcher
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    internal static IntPtr FindWindow(params string[] processNames)
    {
        foreach (var name in processNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception ex)
            {
                Log.Write($"Enumerating processes named '{name}' failed: {ex.Message}");
                continue;
            }

            try
            {
                foreach (var process in processes)
                {
                    var hwnd = FindWindowForPid(process.Id);
                    if (hwnd != IntPtr.Zero)
                    {
                        Log.Write($"Found window for {name}.exe (pid {process.Id}, hwnd 0x{hwnd:X}, title \"{GetTitle(hwnd)}\")");
                        return hwnd;
                    }
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return IntPtr.Zero;
    }

    private static IntPtr FindWindowForPid(int pid)
    {
        var found = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            if (GetWindowThreadProcessId(hwnd, out var windowPid) == 0 || windowPid != (uint)pid)
            {
                return true;
            }

            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
            {
                return true;
            }

            if ((GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0)
            {
                return true;
            }

            if (GetWindowTextLengthW(hwnd) == 0)
            {
                return true;
            }

            found = hwnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }
}
