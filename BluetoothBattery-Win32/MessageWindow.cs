namespace BluetoothBattery;

internal sealed partial class MessageWindow : Win32Window
{
    private const string ClassName = "BluetoothBattery_MessageWindow";

    private readonly uint taskbarCreatedMessage_;

    internal MessageWindow()
        : base(ClassName, style: Win32.WS_POPUP, exStyle: Win32.WS_EX_TOOLWINDOW)
    {
        // Registered, not hardcoded: the value is assigned by the system at first use.
        taskbarCreatedMessage_ = Win32.RegisterWindowMessageW("TaskbarCreated");
    }

    internal event Action? TaskbarCreated;

    internal event Action<bool>? TrayActivated;

    internal event Action? DispatchRequested;

    internal event Action<int, Win32.DEV_BROADCAST_DEVICEINTERFACE>? DeviceInterfaceChanged;

    internal event Action<Guid>? BluetoothCustomEvent;

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
                // PostQuitMessage only ends the loop of the thread that calls it,
                // so it has to run here, on the UI thread.
                Win32.PostQuitMessage(0);
                return true;

            case Win32.WM_DISPATCH:
                DispatchRequested?.Invoke();
                return true;

            case Win32.WM_DEVICECHANGE:
                Diagnostics.Write($"WM_DEVICECHANGE: wParam={(int)wParam:X} lParam={(lParam == IntPtr.Zero ? "null" : "set")}");

                // Only the two event kinds a device interface notification ever sends
                // with a DEV_BROADCAST_DEVICEINTERFACE payload - others carry no such
                // struct and would be an invalid PtrToStructure.
                if ((int)wParam is Win32.DBT_DEVICEARRIVAL or Win32.DBT_DEVICEREMOVECOMPLETE
                    && lParam != IntPtr.Zero)
                {
                    var header = System.Runtime.InteropServices.Marshal
                        .PtrToStructure<Win32.DEV_BROADCAST_DEVICEINTERFACE>(lParam);

                    Diagnostics.Write($"WM_DEVICECHANGE: devicetype={header.dbcc_devicetype} classguid={header.dbcc_classguid}");

                    if (header.dbcc_devicetype == Win32.DBT_DEVTYP_DEVICEINTERFACE)
                    {
                        DeviceInterfaceChanged?.Invoke((int)wParam, header);
                    }
                }
                else if ((int)wParam == Win32.DBT_CUSTOMEVENT && lParam != IntPtr.Zero)
                {
                    var handleHeader = System.Runtime.InteropServices.Marshal
                        .PtrToStructure<Win32.DEV_BROADCAST_HANDLE>(lParam);

                    Diagnostics.Write($"WM_DEVICECHANGE: DBT_CUSTOMEVENT devicetype={handleHeader.dbch_devicetype} " +
                                      $"eventguid={handleHeader.dbch_eventguid}");

                    if (handleHeader.dbch_devicetype == Win32.DBT_DEVTYP_HANDLE)
                    {
                        BluetoothCustomEvent?.Invoke(handleHeader.dbch_eventguid);
                    }
                }

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
