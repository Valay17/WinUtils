namespace BluetoothBattery;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\BluetoothBattery_SingleInstance";

    [STAThread]
    private static int Main()
    {
        using var instanceLock = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Diagnostics.Write("Another instance is already running - exiting.");
            return 0;
        }

        MonitorContext? context = null;

        Timing.LogResolution();
        using var _session = Timing.Measure("session");

        try
        {
            Diagnostics.Write(
                "Started. Refresh strategy: on-demand (no background polling). " +
                $"Low-battery color at {BatteryIcon.LowBatteryPercent}% or below."
                );

            using (Timing.Measure("startup"))
            {
                context = new MonitorContext();
            }

            RunMessageLoop();
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"FATAL: {ex}");
            return 1;
        }
        finally
        {
            context?.Dispose();
            Diagnostics.Write("Exited.");
            instanceLock.ReleaseMutex();
        }

        return 0;
    }

    private static void RunMessageLoop()
    {
        while (true)
        {
            var result = Win32.GetMessageW(out var message, IntPtr.Zero, 0, 0);

            if (result == 0)
            {
                return;
            }

            if (result == -1)
            {
                Diagnostics.Write("GetMessage failed - ending the message loop.");
                return;
            }

            Win32.TranslateMessage(in message);
            Win32.DispatchMessageW(in message);
        }
    }
}

internal sealed partial class MonitorContext : IDisposable
{
    private readonly MessageWindow window_;
    private readonly UiDispatcher dispatcher_;
    private readonly BluetoothMonitor monitor_;
    private readonly TrayController tray_;
    private readonly ShutdownSignal shutdown_;

    private bool disposed_;

    internal MonitorContext()
    {
        DarkModeExperiment.TryEnable();

        window_ = new MessageWindow();

        dispatcher_ = new UiDispatcher(window_.Handle);

        monitor_ = new BluetoothMonitor(dispatcher_, window_);
        tray_ = new TrayController(window_.Handle, monitor_, dispatcher_, RequestExit);

        window_.TaskbarCreated += tray_.RestoreIcon;
        window_.TrayActivated += tray_.OnTrayActivated;
        window_.DispatchRequested += dispatcher_.Drain;

        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        shutdown_ = new ShutdownSignal("BluetoothBattery_Shutdown", RequestExit);

        _ = StartMonitorAsync();
    }

    private async Task StartMonitorAsync()
    {
        try
        {
            await monitor_.StartAsync();
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Starting the Bluetooth monitor failed: {ex}");
        }
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.Window
            or Microsoft.Win32.UserPreferenceCategory.General
            or Microsoft.Win32.UserPreferenceCategory.VisualStyle
            or Microsoft.Win32.UserPreferenceCategory.Color)
        {
            tray_.OnThemeChanged();
        }
    }

    private void RequestExit()
    {
        Diagnostics.Write("Shutdown requested - exiting cleanly so the tray icon is removed.");
        Win32.PostMessageW(window_.Handle, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (disposed_)
        {
            return;
        }

        disposed_ = true;

        Diagnostics.Write("Shutdown: releasing the shutdown signal.");
        shutdown_.Dispose();

        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        Diagnostics.Write("Shutdown: removing the tray icon and popup.");
        tray_.Dispose();

        Diagnostics.Write("Shutdown: stopping the device watchers.");
        monitor_.Dispose();

        window_.Dispose();
        Diagnostics.Write("Shutdown: done.");
    }
}
