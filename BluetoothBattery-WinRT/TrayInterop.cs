namespace BluetoothBattery;

internal static class TrayInterop
{
    internal static void ForceForeground(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var foreground = Win32.GetForegroundWindow();
            if (foreground == window)
            {
                return;
            }

            var foregroundThread = foreground == IntPtr.Zero
                ? 0
                : Win32.GetWindowThreadProcessId(foreground, IntPtr.Zero);
            var thisThread = Win32.GetCurrentThreadId();

            var attached = foregroundThread != 0 &&
                           foregroundThread != thisThread &&
                           Win32.AttachThreadInput(thisThread, foregroundThread, true);

            Win32.SetForegroundWindow(window);

            if (attached)
            {
                Win32.AttachThreadInput(thisThread, foregroundThread, false);
            }
        }
        catch
        {
        }
    }

    internal static void NudgeMessageQueue(IntPtr window)
    {
        if (window != IntPtr.Zero)
        {
            Win32.PostMessageW(window, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
