namespace ScreenDimmer;

internal sealed class MessageWindow : Win32Window
{
    private const string ClassName = "ScreenDimmer_MessageWindow";

    private readonly uint taskbarCreatedMessage_;

    private readonly uint inversionMessage_;

    internal MessageWindow()
        : base(ClassName, style: Win32.WS_POPUP, exStyle: Win32.WS_EX_TOOLWINDOW)
    {
        taskbarCreatedMessage_ = Win32.RegisterWindowMessageW("TaskbarCreated");
        inversionMessage_ = Win32.RegisterWindowMessageW("ColorInvertWindow_InversionChanged");
    }

    internal event Action<bool>? InversionChanged;

    internal event Action<IntPtr>? PowerSettingChanged;

    internal event Action? DisplayChanged;

    internal event Action? TaskbarCreated;

    internal event Action? TrayActivated;

    internal event Action? InverterDied;

    internal event Action? DesktopKeyChanged;

    internal event Action? NativeColorFilterKeyChanged;

    internal event Action<int>? AppCommand;

    internal event Action<IntPtr>? InitMenuPopup;

    internal event Action<IntPtr>? CustomTriggerReady;

    protected override bool WndProc(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;

        if (inversionMessage_ != 0 && message == inversionMessage_)
        {
            InversionChanged?.Invoke(wParam != IntPtr.Zero);
            return true;
        }

        if (taskbarCreatedMessage_ != 0 && message == taskbarCreatedMessage_)
        {
            TaskbarCreated?.Invoke();
            return true;
        }

        switch (message)
        {
            case Win32.WM_CLOSE:
                Win32.PostQuitMessage(0);
                return true;

            case Win32.WM_INVERTER_DIED:
                InverterDied?.Invoke();
                return true;

            case Win32.WM_DESKTOP_CHANGED:
                DesktopKeyChanged?.Invoke();
                return true;

            case Win32.WM_NATIVE_COLOR_FILTER_CHANGED:
                NativeColorFilterKeyChanged?.Invoke();
                return true;

            case Win32.WM_TRAYICON:
                // NOTIFYICON_VERSION_4: the event is in the low word of lParam.
                var eventCode = (uint)((long)lParam & 0xFFFF);
                if (eventCode is Win32.WM_LBUTTONUP or Win32.WM_RBUTTONUP or
                                 Win32.WM_CONTEXTMENU or Win32.NIN_SELECT or Win32.NIN_KEYSELECT)
                {
                    TrayActivated?.Invoke();
                }

                return true;

            case Win32.WM_POWERBROADCAST:
                if ((int)wParam == Win32.PBT_POWERSETTINGCHANGE)
                {
                    PowerSettingChanged?.Invoke(lParam);
                }

                return false;

            case Win32.WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke();
                return false;

            case Win32.WM_APPCOMMAND:
                // Command is the high word of lParam, device/key-state flags in the top bits.
                var command = (int)((((long)lParam >> 16) & 0xFFFF) & ~0xF000);
                AppCommand?.Invoke(command);
                return false;

            case Win32.WM_INITMENUPOPUP:
                InitMenuPopup?.Invoke(wParam);
                return false;

            case Win32.WM_CUSTOM_TRIGGER_READY:
                CustomTriggerReady?.Invoke(wParam);
                return true;

            default:
                return false;
        }
    }
}
