namespace BluetoothBattery;

// A real top-level window rather than message-only: HWND_MESSAGE windows do
// not receive broadcasts, so TaskbarCreated would never arrive after an
// Explorer restart.
internal sealed partial class MessageWindow : Win32Window
{
    private const string ClassName = "BluetoothBattery_MessageWindow";

    private readonly uint taskbarCreatedMessage_;

    internal MessageWindow()
        : base(ClassName, style: Win32.WS_POPUP, exStyle: Win32.WS_EX_TOOLWINDOW)
    {
        // Registered, not hardcoded: assigned by the system at first use and
        // only stable within a session.
        taskbarCreatedMessage_ = Win32.RegisterWindowMessageW("TaskbarCreated");
    }

    internal event Action? TaskbarCreated;

    internal event Action<bool>? TrayActivated;

    internal event Action? DispatchRequested;

    protected override bool WndProc(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;

        if (taskbarCreatedMessage_ != 0 && message == taskbarCreatedMessage_)
        {
            TaskbarCreated?.Invoke();
            return true;
        }

        switch (message)
        {
            case Win32.WM_CLOSE:
                // Posted here (rather than called) because the exit request can
                // arrive on a thread-pool thread, and PostQuitMessage only ends
                // the loop of the thread that calls it.
                Win32.PostQuitMessage(0);
                return true;

            case Win32.WM_DISPATCH:
                DispatchRequested?.Invoke();
                return true;

            case Win32.WM_TRAYICON:
                // With NOTIFYICON_VERSION_4 the event is in the low word of lParam.
                var eventCode = (uint)((long)lParam & 0xFFFF);

                switch (eventCode)
                {
                    case Win32.WM_LBUTTONUP:
                    case Win32.NIN_SELECT:
                    case Win32.NIN_KEYSELECT:
                        TrayActivated?.Invoke(true);
                        return true;

                    case Win32.WM_RBUTTONUP:
                    case Win32.WM_CONTEXTMENU:
                        TrayActivated?.Invoke(false);
                        return true;

                    default:
                        return true;
                }

            default:
                return false;
        }
    }
}
