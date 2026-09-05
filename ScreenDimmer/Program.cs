namespace ScreenDimmer;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\ScreenDimmer_SingleInstance";

    internal static Config CurrentConfig { get; private set; } = new();

    [STAThread]
    private static int Main()
    {
        using var instanceLock = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Diagnostics.Write("Another instance is already running - exiting.");
            return 0;
        }

        DimmerContext? context = null;

        try
        {
            CurrentConfig = Config.Load();

            Diagnostics.Write("=== Screen Dimmer start ===");
            Diagnostics.Write(
                $"per-monitor {(CurrentConfig.PerMonitor ? "on" : "off")}, " +
                $"per-desktop {(CurrentConfig.PerVirtualDesktop ? "on" : "off")}, " +
                $"step {CurrentConfig.StepPercent}%, cap {CurrentConfig.MaximumDim * 100:F0}%");

            context = new DimmerContext(CurrentConfig);
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
            Diagnostics.Write("=== Screen Dimmer end ===");
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

internal sealed class DimmerContext : IDisposable
{
    private readonly Config config_;
    private readonly MessageWindow window_;
    private readonly VDTracker desktops_;
    private readonly OverlayManager overlays_;
    private readonly BrightnessDetector brightness_;
    private readonly TrayController tray_;
    private readonly ShutdownSignal shutdown_;
    private readonly InversionWatch inversionWatch_;
    private readonly NativeColorFilterWatch nativeColorFilter_;

    private bool colorInvertWindowInverted_;
    private bool nativeFilterInverted_;

    private readonly IntPtr uiWindow_;

    private bool disposed_;

    internal DimmerContext(Config config)
    {
        DarkModeExperiment.TryEnable();

        config_ = config;

        window_ = new MessageWindow();
        uiWindow_ = window_.Handle;

        desktops_ = new VDTracker(window_.Handle);
        overlays_ = new OverlayManager(config, desktops_);
        brightness_ = new BrightnessDetector();

        tray_ = new TrayController(
            window_.Handle, config, overlays_, desktops_, brightness_,
            RestartDesktopTracking, RequestExit);

        window_.DisplayChanged += OnDisplayChanged;
        window_.TaskbarCreated += tray_.RestoreIcon;
        window_.PowerSettingChanged += brightness_.HandlePowerSetting;
        window_.AppCommand += OnAppCommand;
        window_.InversionChanged += OnInversionChanged;
        window_.TrayActivated += tray_.ShowMenu;
        window_.InitMenuPopup += tray_.OnInitMenuPopup;
        window_.CustomTriggerReady += tray_.OnCustomTriggerReady;
        window_.DesktopKeyChanged += desktops_.OnKeyChanged;
        window_.InverterDied += () => OnInversionChanged(false);

        desktops_.DesktopChanged += OnDesktopChanged;

        brightness_.Register(window_.Handle);

        inversionWatch_ = new InversionWatch(
            () => Win32.PostMessageW(uiWindow_, Win32.WM_INVERTER_DIED, IntPtr.Zero, IntPtr.Zero));

        nativeColorFilter_ = new NativeColorFilterWatch(window_.Handle);
        nativeColorFilter_.InvertingChanged += OnNativeColorFilterChanged;
        window_.NativeColorFilterKeyChanged += () => nativeColorFilter_.OnKeyChanged();
        nativeColorFilter_.Start();

        shutdown_ = new ShutdownSignal("ScreenDimmer_Shutdown", RequestExit);

        RestartDesktopTracking();

        ReportFeasibility();
    }

    private void ReportFeasibility()
    {
        Diagnostics.Write(
            "Input: tray menu only. Brightness reporting: " +
            (brightness_.HasReading ? $"{brightness_.Level}%" : "no reading yet") + ".");

        Diagnostics.Write(
            "Brightness-key handover is not implemented. On this laptop those keys are " +
            "raised by ACPI/WMI, not the keyboard, so no hook can observe them - confirmed " +
            "from Linux on the same hardware. Brightness changes are still tracked, which is " +
            "why the levels above are reported.");
    }

    private void OnDisplayChanged()
    {
        Diagnostics.Write("Display configuration changed - rebuilding overlays.");
        overlays_.RebuildOverlays();
    }

    private void OnDesktopChanged()
    {
        overlays_.Apply();
        tray_.RefreshVisuals();
    }

    private void OnInversionChanged(bool inverted)
    {
        colorInvertWindowInverted_ = inverted;
        ApplyInversionState();

        if (inverted)
        {
            inversionWatch_.Start();
        }
        else
        {
            inversionWatch_.Stop();
        }
    }

    private void OnNativeColorFilterChanged(bool inverted)
    {
        nativeFilterInverted_ = inverted;
        ApplyInversionState();
    }

    private void ApplyInversionState() => overlays_.SetInverted(colorInvertWindowInverted_ ^ nativeFilterInverted_);

    private static void OnAppCommand(int command) =>
        Diagnostics.Write($"WM_APPCOMMAND received: {command}");

    private void RestartDesktopTracking()
    {
        desktops_.Stop();

        if (config_.PerVirtualDesktop)
        {
            desktops_.Start();
        }
        else
        {
            Diagnostics.Write("Per-desktop dimming off - no desktop polling.");
        }

        overlays_.Apply();

        tray_.RefreshVisuals();
    }

    private void RequestExit()
    {
        Diagnostics.Write("Shutdown requested - exiting cleanly so overlays and the tray icon are removed.");
        Win32.PostMessageW(uiWindow_, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (disposed_)
        {
            return;
        }

        disposed_ = true;

        shutdown_.Dispose();
        inversionWatch_.Dispose();
        nativeColorFilter_.Dispose();

        tray_.Dispose();
        overlays_.Dispose();

        brightness_.Dispose();
        desktops_.Dispose();
        window_.Dispose();
    }
}
